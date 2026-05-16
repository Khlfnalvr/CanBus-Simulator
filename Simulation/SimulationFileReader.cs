using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Reads BMS simulation rows from CSV, TSV, XLSX, and XLSM files.
/// </summary>
public static class SimulationFileReader
{
    /// <summary>
    /// Loads a simulation file and maps known BMS columns into snapshots.
    /// </summary>
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
        var headers = rows[0];
        var columnMap = BuildColumnMap(headers);
        var snapshots = new List<BmsSnapshot>();

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
            if (IsEmptyRow(row))
            {
                continue;
            }

            var packVoltage = GetDouble(row, packVoltageColumn, defaults.DefaultPackVoltageVolts);
            var current = GetDouble(row, currentColumn, defaults.DefaultCurrentAmps);
            var soc = ToByte(GetDouble(row, socColumn, defaults.DefaultSocPercent), 0, 100);
            var scenario = GetScenario(GetString(row, statusColumn), current);
            var timestamp = GetTimestamp(row, timestampColumn);
            var cellVoltages = GetCellVoltages(row, columnMap, packVoltage, defaults);
            var balanceMask = GetBalanceMask(row, columnMap);
            var temperatures = GetTemperatureArray(row, columnMap, defaults);
            var (maxTemp, minTemp) = GetTemperatures(row, columnMap, maxTempColumn, minTempColumn, defaults, temperatures);
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
            if (map.TryGetValue(NormalizeHeader(alias), out var index))
            {
                return index;
            }
        }

        return -1;
    }

    private static double[] GetCellVoltages(string[] row, IReadOnlyDictionary<string, int> map, double packVoltage, SimulationSettings defaults)
    {
        var cells = new double[20];
        var fallback = Math.Clamp(packVoltage / 20.0, 3.0, 4.2);
        for (var cell = 1; cell <= 20; cell++)
        {
            var column = FindColumn(map, $"Cell{cell}_V", $"Cell{cell}", $"CellVoltage{cell}_V", $"CellVoltage{cell}");
            cells[cell - 1] = GetDouble(row, column, defaults.DefaultCellVoltageVolts > 0 ? defaults.DefaultCellVoltageVolts : fallback);
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
        IReadOnlyDictionary<string, int> map,
        int maxTempColumn,
        int minTempColumn,
        SimulationSettings defaults,
        IReadOnlyList<double> knownTemperatures)
    {
        var maxTemp = GetDouble(row, maxTempColumn, double.NaN);
        var minTemp = GetDouble(row, minTempColumn, double.NaN);
        var allTemps = knownTemperatures.Where(value => !double.IsNaN(value)).ToArray();

        if (double.IsNaN(maxTemp))
        {
            maxTemp = allTemps.Length > 0 ? allTemps.Max() : defaults.DefaultMaxTemperatureC;
        }

        if (double.IsNaN(minTemp))
        {
            minTemp = allTemps.Length > 0 ? allTemps.Min() : defaults.DefaultMinTemperatureC;
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
        var normalized = status.Trim().ToLowerInvariant();
        if (normalized.StartsWith("dis", StringComparison.Ordinal))
        {
            return OperatingScenario.Discharging;
        }

        if (normalized.StartsWith("cha", StringComparison.Ordinal))
        {
            return OperatingScenario.Charging;
        }

        if (normalized.StartsWith("idle", StringComparison.Ordinal))
        {
            return OperatingScenario.Idle;
        }

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
        if (column < 0)
        {
            warnings.Add($"Column {name} tidak ditemukan, memakai default {defaultValue}.");
        }
    }

    private static bool IsEmptyRow(IEnumerable<string> row)
    {
        return row.All(string.IsNullOrWhiteSpace);
    }

    private static string GetString(string[] row, int column)
    {
        return column >= 0 && column < row.Length ? row[column].Trim() : string.Empty;
    }

    private static double GetDouble(string[] row, int column, double defaultValue)
    {
        var value = GetString(row, column);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            || double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed)
            ? parsed
            : defaultValue;
    }

    private static byte ToByte(double value, int min, int max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = min;
        }

        return (byte)Math.Clamp((int)Math.Round(value), min, max);
    }

    private static bool IsTruthy(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "y" or "on" or "active"
            || (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) && Math.Abs(number) > 0.0001);
    }

    private static string NormalizeHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static List<string[]> ReadDelimitedRows(string filePath, char delimiter)
    {
        var text = File.ReadAllText(filePath, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inQuotes)
            {
                if (character == '"')
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
                    currentField.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == delimiter)
            {
                currentRow.Add(currentField.ToString());
                currentField.Clear();
            }
            else if (character is '\r' or '\n')
            {
                currentRow.Add(currentField.ToString());
                currentField.Clear();
                rows.Add(currentRow.ToArray());
                currentRow.Clear();

                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                currentField.Append(character);
            }
        }

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow.ToArray());
        }

        return rows;
    }

    private static List<string[]> ReadXlsxRows(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var sharedStrings = ReadSharedStrings(archive);
        var firstSheetPath = GetFirstWorksheetPath(archive);
        var sheetEntry = archive.GetEntry(firstSheetPath)
            ?? throw new InvalidDataException($"Worksheet {firstSheetPath} was not found in workbook.");

        using var stream = sheetEntry.Open();
        var document = XDocument.Load(stream);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<string[]>();

        foreach (var rowElement in document.Descendants(spreadsheet + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (var cellElement in rowElement.Elements(spreadsheet + "c"))
            {
                var reference = cellElement.Attribute("r")?.Value ?? string.Empty;
                var columnIndex = GetColumnIndex(reference);
                if (columnIndex < 0)
                {
                    columnIndex = cells.Count;
                }

                cells[columnIndex] = ReadCellValue(cellElement, sharedStrings, spreadsheet);
            }

            if (cells.Count == 0)
            {
                rows.Add(Array.Empty<string>());
                continue;
            }

            var row = new string[cells.Keys.Max() + 1];
            foreach (var cell in cells)
            {
                row[cell.Key] = cell.Value;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return new List<string>();
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value)))
            .ToList();
    }

    private static string GetFirstWorksheetPath(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Workbook metadata xl/workbook.xml was not found.");
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("Workbook relationships were not found.");

        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

        using var workbookStream = workbookEntry.Open();
        var workbookDocument = XDocument.Load(workbookStream);
        var firstSheet = workbookDocument.Descendants(spreadsheet + "sheet").FirstOrDefault()
            ?? throw new InvalidDataException("Workbook does not contain a worksheet.");
        var relationshipId = firstSheet.Attribute(relationships + "id")?.Value
            ?? throw new InvalidDataException("First worksheet relationship id was not found.");

        using var relationshipsStream = relationshipsEntry.Open();
        var relationshipsDocument = XDocument.Load(relationshipsStream);
        var target = relationshipsDocument.Descendants(packageRelationships + "Relationship")
            .FirstOrDefault(element => element.Attribute("Id")?.Value == relationshipId)
            ?.Attribute("Target")
            ?.Value;

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidDataException("First worksheet relationship target was not found.");
        }

        target = target.Replace('\\', '/');
        if (target.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase))
        {
            return target.TrimStart('/');
        }

        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? target
            : $"xl/{target.TrimStart('/')}";
    }

    private static string ReadCellValue(XElement cellElement, IReadOnlyList<string> sharedStrings, XNamespace spreadsheet)
    {
        var type = cellElement.Attribute("t")?.Value;
        if (type == "inlineStr")
        {
            return string.Concat(cellElement.Descendants(spreadsheet + "t").Select(text => text.Value));
        }

        var rawValue = cellElement.Element(spreadsheet + "v")?.Value ?? string.Empty;
        if (type == "s" && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex))
        {
            return sharedIndex >= 0 && sharedIndex < sharedStrings.Count ? sharedStrings[sharedIndex] : string.Empty;
        }

        if (type == "b")
        {
            return rawValue == "1" ? "true" : "false";
        }

        return rawValue;
    }

    private static int GetColumnIndex(string cellReference)
    {
        var column = 0;
        var foundLetter = false;
        foreach (var character in cellReference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            foundLetter = true;
            column = (column * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return foundLetter ? column - 1 : -1;
    }
}
