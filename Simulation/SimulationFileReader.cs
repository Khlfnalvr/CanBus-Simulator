using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Reads BMS simulation rows from CSV, TSV, XLSX, and XLSM files.
/// XLSX path uses XmlReader streaming to avoid pulling in System.Xml.Linq.
/// </summary>
public static class SimulationFileReader
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static SimulationFileData Load(string filePath, SimulationSettings defaults)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Simulation file was not found.", filePath);
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var rows = extension switch
        {
            ".csv" => ReadDelimitedRows(filePath, ','),
            ".tsv" => ReadDelimitedRows(filePath, '\t'),
            ".xlsx" or ".xlsm" => ReadXlsxRows(filePath),
            ".xls" => throw new NotSupportedException("File .xls lama belum didukung tanpa driver Excel. Simpan ulang sebagai .xlsx atau .csv."),
            _ => throw new NotSupportedException("Format file tidak didukung. Gunakan .csv, .tsv, .xlsx, atau .xlsm.")
        };

        return MapRows(filePath, rows, defaults);
    }

    private static SimulationFileData MapRows(string sourcePath, IReadOnlyList<string[]> rows, SimulationSettings defaults)
    {
        if (rows.Count < 2)
        {
            throw new InvalidDataException("Simulation file must contain a header row and at least one data row.");
        }

        var warnings = new List<string>();
        var columnMap = BuildColumnMap(rows[0]);
        var snapshots = new List<BmsSnapshot>(rows.Count - 1);

        var packVoltageColumn = FindColumn(columnMap, "PackVoltage_V", "PackVoltage", "Voltage_V", "Voltage");
        var currentColumn = FindColumn(columnMap, "Current_A", "PackCurrent_A", "PackCurrent", "Current");
        var socColumn = FindColumn(columnMap, "SOC_pct", "SOC_percent", "SOC");
        var statusColumn = FindColumn(columnMap, "Status", "Scenario", "Mode", "State");
        var timestampColumn = FindColumn(columnMap, "Timestamp", "Time", "DateTime");
        var maxTempColumn = FindColumn(columnMap, "MaxTemp_C", "MaxTemperature_C", "MaxTemp");
        var minTempColumn = FindColumn(columnMap, "MinTemp_C", "MinTemperature_C", "MinTemp");

        AddWarningIfMissing(warnings, packVoltageColumn, "PackVoltage_V", defaults.DefaultPackVoltageVolts);
        AddWarningIfMissing(warnings, currentColumn, "Current_A", defaults.DefaultCurrentAmps);
        AddWarningIfMissing(warnings, socColumn, "SOC_pct", defaults.DefaultSocPercent);

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (IsEmptyRow(row)) continue;

            var packVoltage = GetDouble(row, packVoltageColumn, defaults.DefaultPackVoltageVolts);
            var current = GetDouble(row, currentColumn, defaults.DefaultCurrentAmps);
            var soc = ToByte(GetDouble(row, socColumn, defaults.DefaultSocPercent), 0, 100);
            var scenario = GetScenario(GetString(row, statusColumn), current);
            var timestamp = GetTimestamp(row, timestampColumn);
            var cellVoltages = GetCellVoltages(row, columnMap, packVoltage, defaults);
            var balanceMask = GetBalanceMask(row, columnMap);
            var temperatures = GetTemperatureArray(row, columnMap, defaults);
            var (maxTemp, minTemp) = GetTemperatures(row, maxTempColumn, minTempColumn, defaults, temperatures);
            var balanceFlags = GetBalanceFlags(row, columnMap);

            snapshots.Add(new BmsSnapshot(
                packVoltage,
                current,
                soc,
                maxTemp,
                minTemp,
                balanceMask,
                scenario,
                cellVoltages,
                temperatures,
                balanceFlags,
                timestamp));
        }

        if (snapshots.Count == 0)
        {
            throw new InvalidDataException("Simulation file does not contain usable data rows.");
        }

        return new SimulationFileData(sourcePath, snapshots, warnings);
    }

    private static IReadOnlyDictionary<string, int> BuildColumnMap(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headers.Count; index++)
        {
            var normalized = NormalizeHeader(headers[index]);
            if (!string.IsNullOrWhiteSpace(normalized) && !map.ContainsKey(normalized))
            {
                map[normalized] = index;
            }
        }

        return map;
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> map, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (map.TryGetValue(NormalizeHeader(alias), out var index)) return index;
        }
        return -1;
    }

    private static double[] GetCellVoltages(string[] row, IReadOnlyDictionary<string, int> map, double packVoltage, SimulationSettings defaults)
    {
        var cells = new double[20];
        var fallback = Math.Clamp(packVoltage / 20.0, 3.0, 4.2);
        var defaultCell = defaults.DefaultCellVoltageVolts > 0 ? defaults.DefaultCellVoltageVolts : fallback;
        for (var cell = 1; cell <= 20; cell++)
        {
            var column = FindColumn(map, $"Cell{cell}_V", $"Cell{cell}", $"CellVoltage{cell}_V", $"CellVoltage{cell}");
            cells[cell - 1] = GetDouble(row, column, defaultCell);
        }
        return cells;
    }

    private static ushort GetBalanceMask(string[] row, IReadOnlyDictionary<string, int> map)
    {
        ushort mask = 0;
        for (var cell = 1; cell <= 16; cell++)
        {
            var column = FindColumn(map, $"Bal{cell}", $"Balance{cell}", $"BalanceCell{cell}");
            if (IsTruthy(GetString(row, column)))
            {
                mask |= (ushort)(1 << (cell - 1));
            }
        }
        return mask;
    }

    private static bool[] GetBalanceFlags(string[] row, IReadOnlyDictionary<string, int> map)
    {
        var flags = new bool[20];
        for (var cell = 1; cell <= 20; cell++)
        {
            var column = FindColumn(map, $"Bal{cell}", $"Balance{cell}", $"BalanceCell{cell}");
            flags[cell - 1] = IsTruthy(GetString(row, column));
        }
        return flags;
    }

    private static (byte MaxTemp, byte MinTemp) GetTemperatures(
        string[] row,
        int maxTempColumn,
        int minTempColumn,
        SimulationSettings defaults,
        double[] knownTemperatures)
    {
        var maxTemp = GetDouble(row, maxTempColumn, double.NaN);
        var minTemp = GetDouble(row, minTempColumn, double.NaN);

        if (double.IsNaN(maxTemp) || double.IsNaN(minTemp))
        {
            double max = double.MinValue, min = double.MaxValue;
            var anyValid = false;
            for (var i = 0; i < knownTemperatures.Length; i++)
            {
                var v = knownTemperatures[i];
                if (double.IsNaN(v)) continue;
                if (v > max) max = v;
                if (v < min) min = v;
                anyValid = true;
            }

            if (double.IsNaN(maxTemp)) maxTemp = anyValid ? max : defaults.DefaultMaxTemperatureC;
            if (double.IsNaN(minTemp)) minTemp = anyValid ? min : defaults.DefaultMinTemperatureC;
        }

        return (ToByte(maxTemp, 0, 255), ToByte(minTemp, 0, 255));
    }

    private static double[] GetTemperatureArray(string[] row, IReadOnlyDictionary<string, int> map, SimulationSettings defaults)
    {
        var temps = new double[10];
        for (var index = 1; index <= temps.Length; index++)
        {
            var column = FindColumn(map, $"Temp{index}_C", $"Temp{index}", $"Temperature{index}_C", $"Temperature{index}");
            temps[index - 1] = GetDouble(row, column, defaults.DefaultMinTemperatureC);
        }
        return temps;
    }

    private static OperatingScenario GetScenario(string status, double current)
    {
        var normalized = status.Trim();
        if (normalized.StartsWith("dis", StringComparison.OrdinalIgnoreCase)) return OperatingScenario.Discharging;
        if (normalized.StartsWith("cha", StringComparison.OrdinalIgnoreCase)) return OperatingScenario.Charging;
        if (normalized.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return OperatingScenario.Idle;

        return current switch
        {
            < -0.5 => OperatingScenario.Discharging,
            > 0.5 => OperatingScenario.Charging,
            _ => OperatingScenario.Idle
        };
    }

    private static DateTimeOffset GetTimestamp(string[] row, int column)
    {
        var value = GetString(row, column);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
            || DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out timestamp))
        {
            return timestamp;
        }

        return DateTimeOffset.Now;
    }

    private static void AddWarningIfMissing(List<string> warnings, int column, string name, object defaultValue)
    {
        if (column < 0) warnings.Add($"Column {name} tidak ditemukan, memakai default {defaultValue}.");
    }

    private static bool IsEmptyRow(string[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(row[i])) return false;
        }
        return true;
    }

    private static string GetString(string[] row, int column) =>
        column >= 0 && column < row.Length ? row[column].Trim() : string.Empty;

    private static double GetDouble(string[] row, int column, double defaultValue)
    {
        var value = GetString(row, column);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            || double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed)
            ? parsed
            : defaultValue;
    }

    private static byte ToByte(double value, int min, int max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = min;
        return (byte)Math.Clamp((int)Math.Round(value), min, max);
    }

    private static bool IsTruthy(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return false;
        if (normalized.Equals("1", StringComparison.Ordinal)
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("y", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) && Math.Abs(n) > 0.0001;
    }

    private static string NormalizeHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static List<string[]> ReadDelimitedRows(string filePath, char delimiter)
    {
        var text = File.ReadAllText(filePath, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        currentField.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(c);
                }

                continue;
            }

            if (c == '"') inQuotes = true;
            else if (c == delimiter)
            {
                currentRow.Add(currentField.ToString());
                currentField.Clear();
            }
            else if (c is '\r' or '\n')
            {
                currentRow.Add(currentField.ToString());
                currentField.Clear();
                rows.Add(currentRow.ToArray());
                currentRow.Clear();

                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else
            {
                currentField.Append(c);
            }
        }

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow.ToArray());
        }

        return rows;
    }

    // --- XLSX streaming parser (XmlReader) ---

    private static List<string[]> ReadXlsxRows(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetPath = GetFirstWorksheetPath(archive);
        var sheetEntry = archive.GetEntry(sheetPath)
            ?? throw new InvalidDataException($"Worksheet {sheetPath} was not found in workbook.");

        var rows = new List<string[]>();
        using var stream = sheetEntry.Open();
        using var reader = XmlReader.Create(stream, XmlSettings());

        var rowCells = new SortedDictionary<int, string>();
        var inRow = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        rowCells.Clear();
                        inRow = true;
                        break;

                    case "c" when inRow:
                        ReadCell(reader, sharedStrings, rowCells);
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                if (rowCells.Count == 0)
                {
                    rows.Add(Array.Empty<string>());
                }
                else
                {
                    var row = new string[rowCells.Keys.Max() + 1];
                    foreach (var kv in rowCells) row[kv.Key] = kv.Value;
                    rows.Add(row);
                }
                inRow = false;
            }
        }

        return rows;
    }

    private static void ReadCell(XmlReader reader, IReadOnlyList<string> sharedStrings, SortedDictionary<int, string> rowCells)
    {
        var reference = reader.GetAttribute("r");
        var columnIndex = GetColumnIndex(reference ?? string.Empty);
        if (columnIndex < 0) columnIndex = rowCells.Count;
        var cellType = reader.GetAttribute("t");

        // <c .../> self-closing → no value
        if (reader.IsEmptyElement)
        {
            rowCells[columnIndex] = string.Empty;
            return;
        }

        string value = string.Empty;
        var depth = reader.Depth;

        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                case "v":
                {
                    var raw = reader.ReadElementContentAsString();
                    if (cellType == "s" && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                    {
                        value = (idx >= 0 && idx < sharedStrings.Count) ? sharedStrings[idx] : string.Empty;
                    }
                    else if (cellType == "b")
                    {
                        value = raw == "1" ? "true" : "false";
                    }
                    else
                    {
                        value = raw;
                    }
                    break;
                }
                case "is":
                case "t":
                {
                    // inlineStr can also be <is><t>text</t></is> or rich-text variants.
                    value = ReadAllTextNodes(reader);
                    break;
                }
            }
        }

        rowCells[columnIndex] = value;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();

        var list = new List<string>();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, XmlSettings());

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                list.Add(ReadAllTextNodes(reader));
            }
        }

        return list;
    }

    /// <summary>
    /// Consumes the current element and returns concatenated <c>&lt;t&gt;</c> content.
    /// Handles rich-text shared strings (<c>&lt;si&gt;&lt;r&gt;&lt;t&gt;...&lt;/t&gt;&lt;/r&gt;&lt;/si&gt;</c>).
    /// </summary>
    private static string ReadAllTextNodes(XmlReader reader)
    {
        if (reader.IsEmptyElement) return string.Empty;

        var depth = reader.Depth;
        var builder = new StringBuilder();

        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                builder.Append(reader.ReadElementContentAsString());
            }
        }

        return builder.ToString();
    }

    private static string GetFirstWorksheetPath(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Workbook metadata xl/workbook.xml was not found.");
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("Workbook relationships were not found.");

        string? relationshipId = null;
        using (var ws = workbookEntry.Open())
        using (var rd = XmlReader.Create(ws, XmlSettings()))
        {
            while (rd.Read())
            {
                if (rd.NodeType == XmlNodeType.Element && rd.LocalName == "sheet")
                {
                    relationshipId = rd.GetAttribute("id", RelationshipsNs);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(relationshipId))
            throw new InvalidDataException("Workbook does not contain a worksheet.");

        string? target = null;
        using (var rs = relationshipsEntry.Open())
        using (var rd = XmlReader.Create(rs, XmlSettings()))
        {
            while (rd.Read())
            {
                if (rd.NodeType == XmlNodeType.Element && rd.LocalName == "Relationship" &&
                    rd.GetAttribute("Id") == relationshipId)
                {
                    target = rd.GetAttribute("Target");
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidDataException("First worksheet relationship target was not found.");

        target = target.Replace('\\', '/');
        if (target.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase)) return target.TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : $"xl/{target.TrimStart('/')}";
    }

    private static int GetColumnIndex(string cellReference)
    {
        var column = 0;
        var foundLetter = false;
        foreach (var c in cellReference)
        {
            if (!char.IsLetter(c)) break;
            foundLetter = true;
            column = (column * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return foundLetter ? column - 1 : -1;
    }

    private static XmlReaderSettings XmlSettings() => new()
    {
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };
}
