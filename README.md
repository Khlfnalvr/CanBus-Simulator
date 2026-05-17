# CAN Bus BMS Simulator

Aplikasi Windows untuk mensimulasikan ESP32 yang bertindak sebagai master Battery Management System. Aplikasi membaca data BMS (generated atau dari file replay) dan mendorongnya ke COM port dalam format yang sama seperti firmware ESP32 + CAN transceiver akan kirim ke host. Software BMS Monitor di sisi pasangan virtual COM port membaca data ini seperti membaca real device.

- Target: **.NET 10**, **WinUI 3** (Windows App SDK), single-file self-contained `.exe`.
- Serial layer pakai `System.IO.Ports`.
- MVVM pakai `CommunityToolkit.Mvvm`.

## Format Wire (Dipilih di UI)

| Format | Output                                              | Cocok untuk |
| ------ | --------------------------------------------------- | ----------- |
| Custom | `$ID:0x100,DLC:8,DATA:0FA0FFEC4B000000\r\n`         | BMS Monitor versi awal Anda |
| SLCAN  | `t10080FA0FFEC4B000000\r`                           | De-facto USB-CAN bridge (ESP32 + MCP2515, candleLight, Lawicel) |
| Binary | Raw bytes `[ID_HI][ID_LO][DLC][DATA...]`            | Reader yang parse byte stream langsung |

Optional XOR checksum bisa di-append:
- Custom: tambah token `,CHK:HH`
- Binary: trailing byte XOR
- SLCAN: terminator only, checksum diabaikan

## CAN Frame yang Disimulasikan

Mengikuti perilaku ESP master yang membaca CAN bus 20S BMS pack. Tiap group punya cadence sendiri:

| CAN ID    | Group        | Default period | Konten |
| --------- | ------------ | -------------- | ------ |
| 0x100     | Pack status  | 100 ms         | status, SOC 0.1%, current 0.1A signed, pack V 0.01V |
| 0x101-105 | Cells 1-20   | 200 ms         | 20 cell voltages, 4 cell/frame, mV |
| 0x110-112 | Temps 1-10   | 500 ms         | 10 sensors, 0.1 C signed |
| 0x120     | Balancing    | 1000 ms        | 20-bit mask |
| 0x130     | Diagnostic   | 500 ms         | protection/warning bits, balance count, dV mV, cycle count |
| 0x140     | Heartbeat    | 250 ms         | signature `BMSM`, counter, uptime seconds |

Semua period dapat disesuaikan di UI.

## Setup com0com (Disarankan)

1. Install `com0com` untuk Windows.
2. Buat virtual COM pair, misal `COM15 <-> COM16`.
3. Jalankan simulator, pilih `COM15`, klik Connect lalu Start TX.
4. Jalankan BMS Monitor Anda dengan pembacaan diarahkan ke `COM16`.
5. Baud rate default: `115200`.

Catatan: dua aplikasi tidak bisa buka COM port yang sama. Simulator buka satu sisi pair, BMS Monitor buka sisi lainnya.

## Cara Build

Membutuhkan .NET 10 SDK + Windows App SDK runtime (1.6+).

```powershell
dotnet build -c Release -p:Platform=x64
```

## Cara Publish Single-File .exe (Ukuran Minimum)

Properti optimisasi sudah tertanam di `CanBusSimulator.csproj` (trim, compress, framework feature switches). Cukup jalankan:

```powershell
dotnet publish -c Release -p:Platform=x64 -r win-x64
```

Output:

```text
bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish\CanBusSimulator.exe
```

Cukup distribusikan satu file `.exe`. App jalan tanpa install di Windows 10 (1809+) dan Windows 11. Tidak perlu Windows App SDK runtime terpisah (bundled).

### Detail Optimisasi Publish

Project sudah mengaktifkan secara default (di Release):

- `PublishSingleFile=true` — semua dependency dibundel menjadi satu `.exe`.
- `EnableCompressionInSingleFile=true` — single-file payload dikompresi.
- `IncludeNativeLibrariesForSelfExtract=true` — native lib ikut dibundel.
- `PublishTrimmed=true` + `TrimMode=partial` — IL yang tidak terpakai dibuang. Mode `partial` aman untuk WinUI 3 (full trim bisa break XAML reflection).
- `PublishReadyToRun=false` — prefer ukuran kecil di atas startup time (R2R akan menambah ~30-50% size).
- `IlcOptimizationPreference=Size` — preference ukuran terkecil untuk codegen.
- `DebugType=none`, `DebugSymbols=false` — tidak ada PDB di output Release.
- Framework feature switches off:
  - `EventSourceSupport`
  - `HttpActivityPropagationSupport`
  - `MetadataUpdaterSupport`
  - `DebuggerSupport`
  - `EnableUnsafeBinaryFormatterSerialization`
  - `EnableUnsafeUTF7Encoding`
  - `UseNativeHttpHandler`
  - `NullabilityInfoContextSupport`
  - `XmlResolverIsNetworkingEnabledByDefault`
  - `UseSystemResourceKeys=true` (resource keys diganti dengan key string pendek)

Untuk maximum-tiny build (eksperimen), tambah:

```powershell
dotnet publish -c Release -p:Platform=x64 -r win-x64 -p:TrimMode=full
```

Hati-hati: `TrimMode=full` boleh saja break WinUI XAML reflection di skenario tertentu — uji jalannya UI dulu.

## Optimisasi Runtime

Hot path transmission sudah dihardenkan untuk reduce GC pressure:

- **`CanFrame.TryRender(Span<byte>, ...)`** — render frame langsung ke caller buffer; nol byte[] alokasi di hot path.
- **`TransmissionService`** — single `byte[64]` worker buffer dipakai ulang untuk setiap frame. Tidak ada `ConcurrentQueue` antara producer dan consumer (worker single-threaded). Settings dibaca via atomic reference swap (volatile record), tidak ada lock per-iterasi.
- **`PeriodicTimer`** menggantikan `Task.Delay(10)` polling loop untuk timing tick yang lebih akurat.
- **`BmsCanFrameFactory`** — tidak ada LINQ di hot path. Min/max + counting via plain loop.
- **String describe** menggunakan `string.Create(CultureInfo, ...)` interpolation handler — minimal intermediate string allocation.

## Fitur Baru di UI

- **Stats counter** di log section: total frame terkirim, total byte, jumlah error, uptime TX.
- **Clear log** button untuk reset isi log tanpa restart TX.
- **Export log** button untuk save snapshot 100 frame terakhir ke file teks.
- **Reset stats** button untuk reset semua counter ke nol.

## File Replay (CSV / XLSX)

Klik `Browse...` di section `Simulation File Replay`. Kolom yang dikenali:

- `Timestamp`, `PackVoltage_V`, `SOC_pct`, `Current_A`, `Status`
- `Cell1_V` sampai `Cell20_V`
- `Bal1` sampai `Bal20`
- `Temp1_C` sampai `Temp10_C`
- `MaxTemp_C`, `MinTemp_C` (opsional)

## Struktur Folder

- `Models/` — `CanFrame`, `BmsSnapshot`, `WireFormat`
- `Can/BmsCanFrameFactory.cs` — mapping BMS snapshot → CAN frames (0x100/0x101–0x105/0x110–0x112/0x120/0x130/0x140)
- `Simulation/` — auto/manual simulator + file replay
- `Serial/SerialPortTransport.cs` — System.IO.Ports wrapper
- `Transmission/TransmissionService.cs` — scheduler per-group cadence
- `ViewModels/MainViewModel.cs` — state + commands (CommunityToolkit.Mvvm)
- `Views/MainWindow.xaml` — WinUI 3 UI
- `App.xaml(.cs)` — entry point, DI wiring
