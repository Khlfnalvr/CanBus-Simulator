# CAN Bus BMS Simulator

Aplikasi Windows C# (.NET 8 WinForms) untuk mensimulasikan komunikasi CAN bus BMS melalui virtual COM port. Simulator menulis frame dalam format text human-readable ke COM5, lalu BMS Monitor dapat membaca dari pasangan virtualnya, misalnya COM6.

Simulator bisa berjalan dalam dua mode:

- Generated/manual data dari UI.
- Replay data dari file `.csv`, `.tsv`, `.xlsx`, atau `.xlsm`.
- Dummy replay bawaan dari `BMS_Simulation_20S4P.csv` tanpa perlu upload file lagi.

## Format Output

Default line yang dikirim ke serial:

```text
$ID:0x100,DLC:8,DATA:0FA0FFEC4B000000\r\n
```

Struktur raw yang direpresentasikan:

```text
[CAN_ID:2 byte big-endian] [DLC:1 byte] [PAYLOAD:0-8 byte]
```

Checksum XOR dihitung untuk setiap frame dan tampil di log. Secara default checksum tidak dikirim ke COM port agar wire format tetap persis seperti requirement. Jika monitor membutuhkan field checksum, aktifkan `Append CHK to wire format` di UI atau `AppendChecksumToWireFormat` di `appsettings.json`.

## Message

Satu cycle lengkap dikirim setiap 1000 ms. Dalam satu cycle aplikasi mengirim seluruh frame yang diperlukan BMS Monitor: pack, 20 cell, 10 temperature, dan balancing.

Payload multi-byte ditulis big-endian.

Catatan skala default disesuaikan dengan contoh payload:

- Pack voltage menggunakan `20 mV/bit`, sehingga 80 V menjadi raw `4000` atau `0x0FA0`.
- Pack current menggunakan `1 A/bit`, sehingga -20 A menjadi raw `0xFFEC`.
- Cell voltage tetap millivolt langsung, misalnya 4.106 V menjadi `0x100A`.

Skala tersebut bisa diubah pada `appsettings.json` jika BMS Monitor memakai konvensi berbeda.

## Setup com0com

1. Install com0com untuk Windows.
2. Buat virtual pair, misalnya `COM15 <-> COM16`. Hindari COM yang sudah dipakai Bluetooth/USB UART.
3. Jalankan simulator dan pilih salah satu sisi pair, misalnya `COM15`.
4. Jalankan BMS Monitor dan arahkan pembacaan ke sisi pasangannya, misalnya `COM16`.
5. Baud rate default: `115200`.

Penting: Windows serial port bersifat eksklusif. Dua aplikasi tidak boleh membuka COM yang sama. Simulator membuka sisi TX, aplikasi pembaca membuka sisi pair yang berbeda.

## Cara Build

Pastikan .NET 8 Desktop Runtime atau SDK tersedia di Windows.

```powershell
dotnet build -c Release
```

Executable hasil build:

```text
bin\Release\net8.0-windows\CanBusSimulator.exe
```

Untuk membuat executable self-contained Windows x64:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Output publish:

```text
bin\Release\net8.0-windows\win-x64\publish\CanBusSimulator.exe
```

## Cara Run

```powershell
dotnet run -c Release
```

Atau buka langsung `CanBusSimulator.exe` dari folder build/publish.

## Fitur UI

- Pilih COM port dan baud rate.
- Connect/Disconnect serial port.
- Start/Stop transmission.
- Pilih file simulasi CSV/Excel dan replay baris data ke CAN frame.
- Auto simulation atau manual mode.
- Pilih scenario: `Discharging`, `Charging`, `Idle`.
- Runtime adjustment untuk voltage, current, SOC, temperature, cell voltage, balance mask.
- Konfigurasi interval message `0x100`, `0x101`, `0x102`.
- Log last 20 transmitted messages dengan timestamp, payload hex, checksum, dan decoded values.

## File Simulasi

Klik `Browse...` di bagian `Simulation File Replay`, pilih file CSV atau Excel, lalu aktifkan `Use file data`.

Kolom yang dikenali otomatis:

- `Timestamp`
- `PackVoltage_V`
- `SOC_pct`
- `Current_A`
- `Status` dengan nilai seperti `charging`, `discharging`, atau `idle`
- `Cell1_V` sampai `Cell20_V`
- `Bal1` sampai `Bal20`
- `Temp1_C` sampai `Temp32_C`

Frame yang dikirim mengikuti mapping BMS Monitor:

- `0x100`: status, SOC 0.1%, current 0.1A signed, pack voltage 0.01V.
- `0x101-0x105`: cell voltage `Cell1_V` sampai `Cell20_V` dalam mV.
- `0x110-0x112`: `Temp1_C` sampai `Temp10_C` dalam 0.1 C.
- `0x120`: balancing flags `Bal1` sampai `Bal20`.

Format Excel yang didukung langsung adalah `.xlsx` dan `.xlsm`. Untuk file `.xls` lama, simpan ulang sebagai `.xlsx` atau `.csv`.

## Error Handling

- Koneksi serial memakai Win32 API langsung dan menampilkan pesan error yang jelas.
- Saat write gagal atau port terputus, transport ditutup dan scheduler mencoba reconnect setiap 1 detik.
- UI update dari background thread dilakukan melalui `BeginInvoke`.

## File Penting

- `Models/CanFrame.cs`: validasi CAN ID, DLC, payload, raw binary, checksum, wire format.
- `Can/BmsCanFrameFactory.cs`: mapping BMS snapshot ke payload CAN.
- `Simulation/BmsDataSimulator.cs`: auto/manual data simulator.
- `Serial/Win32SerialTransport.cs`: serial COM writer tanpa NuGet dependency.
- `Transmission/TransmissionService.cs`: queue dan periodic transmission.
- `UI/MainForm.cs`: WinForms UI.
