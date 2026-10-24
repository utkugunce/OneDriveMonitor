# OneDrive Sync Monitor ☁️⚡

A high-performance, real-time native Windows monitoring tool for Microsoft OneDrive sync engine activity.

Built with **C# / .NET 8 WPF**, **Win32 API (`kernel32.dll`)**, and **SQLite WAL**.

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-blue)
![Framework](https://img.shields.io/badge/Framework-.NET%208%20WPF-purple)
![Size](https://img.shields.io/badge/Binary%20Size-~480%20KB-success)
![Memory](https://img.shields.io/badge/RAM%20Usage-~28--32%20MB-brightgreen)

---

## 🚀 Power-User Features

- **⚡ Isolated Per-Process Transfer Rates:** Direct Win32 `OpenProcess(PROCESS_QUERY_INFORMATION)` and `GetProcessIoCounters` monitoring isolates upload and download speeds specifically for `OneDrive.exe` without capturing outside browser/stream traffic.
- **🔒 Zero-Lock SQLite WAL Engine:** Reads live progress directly from `%LOCALAPPDATA%\Microsoft\OneDrive\settings\Personal\SyncEngineDatabase.db` in under **2 ms** without lock contention or file copy overhead.
- **🛡️ Autonomous Schema Resilience (`PRAGMA schema_version`):** Nano-second $O(1)$ database header inspection detects silent Microsoft OneDrive background migrations and dynamically rebuilds queries on-the-fly.
- **📋 Smart Queue Inspector & Extension Profile:** Expandable drawer displays the top 15 pending files in queue, real-time extension breakdown (e.g. `4,200 .png • 120 .docx`), and warns on developer clutter (`node_modules`, `.venv`, `.git`).
- **⚠️ Stuck File & Lock Watchdog:** Automatically detects when a file transfer hangs for >3 minutes with zero transfer progress and warns of potential local file locks.
- **🗕 Mini Pill (Capsule) Mode:** Switch between a rich 430x540 dashboard and a sleek 290x38 floating capsule displaying `% | Speeds | ETA`.
- **🔄 Dynamic System Tray Progress (GDI+):** Draws a live circular progress ring directly onto the Taskbar notification tray icon in real time.
- **⚡ Smart Actions & Remote Telemetry:**
  - Automated power actions on sync completion: Shutdown (`/s`), Sleep (`SetSuspendState`), Hibernate (`/h`), or terminate `OneDrive.exe`.
  - Native Windows Toast balloons and instant remote push notifications via **`ntfy.sh`**.
- **📊 Session Statistics & CSV Export:** Generates timestamped `.csv` reports on your Desktop summarizing total session bytes transferred, peak speed, and duration.

---

## 🛠️ Architecture

```
                      +-----------------------------+
                      |   OneDrive Sync Monitor     |
                      |   (.NET 8 WPF - 480 KB)     |
                      +--------------+--------------+
                                     |
             +-----------------------+-----------------------+
             |                                               |
  [Layer 1: Win32 API]                            [Layer 2: SQLite WAL Engine]
  kernel32!GetProcessIoCounters                   SyncEngineDatabase.db (WAL Mode)
  - Isolated OneDrive.exe I/O                     - PRAGMA schema_version check
  - Exact socket & disk bytes/s                   - Query: od_ClientFile_Records
             |                                               |
             +-----------------------+-----------------------+
                                     |
                       [Hybrid Speed Estimator]
                   Exponential Moving Average (EMA)
                                     |
                         [Hardware-Accelerated UI]
         - Queue Inspector & Extension Breakdown
         - Dynamic GDI+ System Tray Progress Ring
         - Mini Pill Mode / Full Obsidian Glass Dashboard
```

---

## 📦 Building from Source

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.
- Windows 10 / 11.

### Publish Standalone Single-File Executable
```powershell
dotnet publish ./OneDriveMonitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./dist
```

---

## 📄 License
MIT License.
