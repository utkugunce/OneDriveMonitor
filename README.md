# OneDrive Sync Monitor ☁️⚡

A high-performance, real-time native Windows power-user monitoring tool for Microsoft OneDrive sync engine activity.

Built with **C# / .NET 8 WPF**, **Win32 API (`kernel32.dll`, `dwmapi.dll`)**, **Windows Restart Manager (`rstrtmgr.dll`)**, and **SQLite WAL**.

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-blue)
![Framework](https://img.shields.io/badge/Framework-.NET%208%20WPF-purple)
![Size](https://img.shields.io/badge/Binary%20Size-~490%20KB-success)
![Memory](https://img.shields.io/badge/RAM%20Usage-~28--34%20MB-brightgreen)
![Version](https://img.shields.io/badge/Version-v1.1.0-orange)

---

## 🚀 Power-User Features (v1.1.0)

- **⚡ Isolated Per-Process Transfer Rates:** Direct Win32 `OpenProcess(PROCESS_QUERY_INFORMATION)` and `GetProcessIoCounters` monitoring isolates upload and download speeds specifically for `OneDrive.exe` without capturing outside browser/stream traffic.
- **🔒 Zero-Lock SQLite WAL Engine:** Reads live progress directly from `%LOCALAPPDATA%\Microsoft\OneDrive\settings\Personal\SyncEngineDatabase.db` in under **2 ms** without lock contention or file copy overhead.
- **🛡️ Autonomous Schema Resilience (`PRAGMA schema_version`):** Nano-second $O(1)$ database header inspection detects silent Microsoft OneDrive background migrations and dynamically rebuilds queries on-the-fly.
- **🔍 Active Stuck File & Process Lock Watchdog (`rstrtmgr.dll`):**
  - Monitors pending/in-flight files (`fileStatus != 2 OR reuploadNeeded != 0`).
  - Resolves the exact local file path on disk in $O(1)$ time via `od_ClientFolder_Records` hierarchy caching.
  - Queries Windows **Restart Manager API** (`rstrtmgr.dll`) to identify the exact locking application (`ProcessName`, `PID`).
  - Interactive Action Panel allows you to:
    - **📂 Klasörü Aç:** Reveals and selects the stuck file in Windows File Explorer.
    - **🔓 Eşitlemeyi Dürt (Nudge/Wake):** Re-touches and forces timestamp reconciliation to unblock file filter driver deadlocks.
    - **🔄 OneDrive Yenile:** Gracefully terminates and relaunches `OneDrive.exe /background`.
- **📋 Smart Queue Inspector & Instant Search:**
  - Expandable drawer displays pending queue files with status indicators (⏳ Queued, ▲ Uploading, ▼ Downloading, ⚠️ Stuck).
  - Real-time search/filter box to find files by name or extension in milliseconds.
  - Right-click context menu: Reveal in Explorer, Copy full path, Inspect process lock, or Nudge.
  - Extension breakdown profile (e.g. `45 .png • 12 .docx • 3 .zip`) and developer clutter warnings (`node_modules`, `.venv`, `.git`).
- **🗕 Draggable Mini Pill (Capsule) Mode:**
  - Switch between a 440x570 full dashboard and a 290x38 floating pill displaying `% | Speeds | ETA`.
  - Left-click drag moves the pill anywhere on your multi-monitor desktop.
  - Double-click effortlessly restores the full dashboard.
- **🔄 Dynamic System Tray Progress (GDI+):** Draws a live circular progress ring directly onto the Taskbar notification tray icon in real time.
- **⚡ Smart Actions & Remote Telemetry:**
  - Automated power actions on sync completion: Shutdown (`/s`), Sleep (`SetSuspendState`), Hibernate (`/h`), terminate `OneDrive.exe`, or trigger a **Custom Script (`.bat` / `.ps1`)**.
  - Push notifications via **`ntfy.sh`** and **Telegram Bot API**.
  - Native Windows Toast & Balloon tip notifications.
- **📊 Session Statistics & CSV Export:** Generates timestamped `.csv` reports on your Desktop summarizing total session bytes transferred, peak speed, and duration.
- **🪟 Windows 11 Fluent Dark Integration:** Native DWM immersive dark mode (`DWMWA_USE_IMMERSIVE_DARK_MODE`) and rounded window corners (`DWMWCP_ROUND`).

---

## 🛠️ Architecture

```
                      +-----------------------------+
                      |   OneDrive Sync Monitor     |
                      |   (.NET 8 WPF - 490 KB)     |
                      +--------------+--------------+
                                     |
             +-----------------------+-----------------------+
             |                                               |
  [Layer 1: Win32 & Restart Manager]               [Layer 2: SQLite WAL Engine]
  - kernel32!GetProcessIoCounters                  - SyncEngineDatabase.db (WAL Mode)
  - rstrtmgr!RmGetList (Lock Detection)            - PRAGMA schema_version check
  - dwmapi!DwmSetWindowAttribute                   - od_ClientFolder_Records Path Cache
  - Isolated OneDrive.exe I/O                      - Query: od_ClientFile_Records
             |                                               |
             +-----------------------+-----------------------+
                                     |
                       [Hybrid Speed Estimator]
                   Exponential Moving Average (EMA)
                                     |
                         [Hardware-Accelerated UI]
         - Queue Inspector & Search Filter with Context Actions
         - Stuck File Watchdog & Nudge Quick Fixer
         - Dynamic GDI+ System Tray Progress Ring
         - Draggable Mini Pill Mode / Obsidian Glass Dashboard
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
MIT License. © 2026 utkugunce
