using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

using Point = System.Windows.Point;
using Canvas = System.Windows.Controls.Canvas;
using WpfApplication = System.Windows.Application;
using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;
using Path = System.IO.Path;

namespace OneDriveMonitor
{
    public class QueueItem
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string FormattedSize => MainWindow.FormatBytesStatic(Size);
        public string ParentResourceId { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int FileStatus { get; set; }
        public string StatusIcon => FileStatus switch
        {
            8 => "⚠️",
            3 => "▲",
            1 => "▼",
            _ => "⏳"
        };
        public string StatusText => FileStatus switch
        {
            8 => "Takıldı",
            3 => "Yükleniyor",
            1 => "İndiriliyor",
            _ => "Kuyrukta"
        };
    }

    public partial class MainWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(IntPtr ProcessHandle, out IO_COUNTERS IoCounters);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("PowrProf.dll", SetLastError = true)]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        private readonly string _syncDbPath = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\OneDrive\settings\Personal\SyncEngineDatabase.db");

        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private Forms.NotifyIcon? _notifyIcon;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        private ulong _lastReadBytes = 0;
        private ulong _lastWriteBytes = 0;
        private long _lastRemBytes = -1;
        private DateTime _lastPollTime = DateTime.UtcNow;

        private double _smoothedUploadSpeed = 0;
        private double _smoothedDownloadSpeed = 0;
        private double _peakSpeed = 0;

        private readonly Queue<double> _speedHistory = new Queue<double>();
        private const int MaxHistoryCount = 36;

        private long _maxBatchBytes = 0;
        private bool _hasNotifiedCompletion = false;

        // Oturum İstatistikleri
        private readonly DateTime _sessionStartTime = DateTime.Now;
        private double _sessionTotalBytesTransferred = 0;

        // Kilitli / Takılan Dosya Takibi (Watchdog)
        private string _currentStuckFileName = "";
        private string _currentStuckFilePath = "";
        private int _stuckTickCount = 0;

        // Otonom Şema Takibi (PRAGMA schema_version)
        private readonly HashSet<string> _knownColumns = new(StringComparer.OrdinalIgnoreCase);
        private int _lastSchemaVersion = -1;

        // UI Modu & Kuyruk Verisi
        private bool _isPillMode = false;
        private List<QueueItem> _allQueueItems = new();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            for (int i = 0; i < MaxHistoryCount; i++)
            {
                _speedHistory.Enqueue(0);
            }

            SetupSystemTray();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => UpdateMetrics();
            _timer.Start();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Right - Width - 20;
                Top = workArea.Bottom - Height - 20;

                // Windows 11 Fluent Dark Mode
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                int darkMode = 1;
                DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                int cornerPref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(helper.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            }
            catch { }

            UpdateMetrics();
        }

        private void SetupSystemTray()
        {
            try
            {
                _notifyIcon = new Forms.NotifyIcon
                {
                    Text = "OneDrive Sync Monitor",
                    Visible = true
                };

                UpdateTrayIcon(0);

                var contextMenu = new Forms.ContextMenuStrip();
                var itemShow = new Forms.ToolStripMenuItem("Aç / Göster", null, (s, e) => ShowWindow());
                var itemPill = new Forms.ToolStripMenuItem("Mini Kapsül Moduna Geç", null, (s, e) => TogglePillMode());
                var itemRestartOd = new Forms.ToolStripMenuItem("OneDrive'ı Yeniden Başlat", null, (s, e) => RestartOneDriveProcess());
                var itemExit = new Forms.ToolStripMenuItem("Çıkış", null, (s, e) => ExitApplication());

                contextMenu.Items.Add(itemShow);
                contextMenu.Items.Add(itemPill);
                contextMenu.Items.Add(itemRestartOd);
                contextMenu.Items.Add(new Forms.ToolStripSeparator());
                contextMenu.Items.Add(itemExit);

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) => ShowWindow();
            }
            catch { }
        }

        private void UpdateTrayIcon(double pct)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(16, 16);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.Clear(DrawingColor.Transparent);

                // Zemin halkası
                using var penBg = new DrawingPen(DrawingColor.FromArgb(40, 45, 60), 2.5f);
                g.DrawEllipse(penBg, 2, 2, 11, 11);

                // İlerleme yayı
                float sweepAngle = (float)(Math.Clamp(pct, 0.0, 100.0) * 3.6);
                using var penArc = new DrawingPen(DrawingColor.FromArgb(56, 189, 248), 2.5f);
                g.DrawArc(penArc, 2, 2, 11, 11, -90, sweepAngle);

                IntPtr hIcon = bmp.GetHicon();
                if (_notifyIcon != null)
                {
                    _notifyIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
                }
                DestroyIcon(hIcon);
            }
            catch { }
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }

        private void CheckAndValidateSchema(SqliteConnection conn)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA schema_version;";
                int currentVersion = Convert.ToInt32(cmd.ExecuteScalar());

                if (currentVersion != _lastSchemaVersion)
                {
                    _knownColumns.Clear();
                    using var cmdInfo = conn.CreateCommand();
                    cmdInfo.CommandText = "PRAGMA table_info(od_ClientFile_Records);";
                    using var reader = cmdInfo.ExecuteReader();
                    while (reader.Read())
                    {
                        _knownColumns.Add(reader.GetString(1));
                    }
                    _lastSchemaVersion = currentVersion;
                }

                OneDrivePathResolver.RefreshFolderCache(conn);
            }
            catch { }
        }

        private void UpdateMetrics()
        {
            var now = DateTime.UtcNow;
            var dt = (now - _lastPollTime).TotalSeconds;
            if (dt <= 0.001) dt = 1.0;
            _lastPollTime = now;

            // 1. Process I/O Hızı
            ulong curRead = 0;
            ulong curWrite = 0;

            try
            {
                var procs = Process.GetProcessesByName("OneDrive");
                foreach (var p in procs)
                {
                    IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (hProc != IntPtr.Zero)
                    {
                        if (GetProcessIoCounters(hProc, out var counters))
                        {
                            curRead += counters.ReadTransferCount;
                            curWrite += counters.WriteTransferCount;
                        }
                        CloseHandle(hProc);
                    }
                    p.Dispose();
                }
            }
            catch { }

            // 2. Veritabanı Sorguları
            long remCount = 0;
            long remBytes = 0;
            long totalFiles = 181268;
            string lastFile = "";
            var queueList = new List<QueueItem>();
            var extensionStats = new Dictionary<string, (int count, long size)>(StringComparer.OrdinalIgnoreCase);
            int junkFilesCount = 0;
            int conflictCount = 0;

            string candidateStuckName = "";
            string candidateStuckParentId = "";
            int candidateStuckStatus = 0;

            if (File.Exists(_syncDbPath))
            {
                try
                {
                    var cs = new SqliteConnectionStringBuilder
                    {
                        DataSource = _syncDbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Shared
                    }.ToString();

                    using var conn = new SqliteConnection(cs);
                    conn.Open();

                    CheckAndValidateSchema(conn);

                    // A. Kalan dosya ve bayt
                    string sizeCol = _knownColumns.Contains("size") ? "coalesce(sum(size), 0)" : "0";
                    var conds = new List<string>();
                    if (_knownColumns.Contains("fileStatus")) conds.Add("fileStatus != 2");
                    if (_knownColumns.Contains("reuploadNeeded")) conds.Add("reuploadNeeded != 0");
                    string where = conds.Count > 0 ? "WHERE " + string.Join(" OR ", conds) : "";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT count(*), {sizeCol} FROM od_ClientFile_Records {where}";
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            remCount = reader.GetInt64(0);
                            remBytes = reader.GetInt64(1);
                        }
                    }

                    // B. Toplam Takip Edilen
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT count(*) FROM od_ClientFile_Records";
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read()) totalFiles = reader.GetInt64(0);
                    }

                    // C. Son Eşitlenen Dosya (fileStatus = 2)
                    if (_knownColumns.Contains("fileName") && _knownColumns.Contains("lastChange"))
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT fileName FROM od_ClientFile_Records WHERE fileStatus = 2 ORDER BY lastChange DESC LIMIT 1";
                        lastFile = cmd.ExecuteScalar()?.ToString() ?? "";
                    }

                    // D. Takılan Aday Dosya (Aktif bekleyen ilk dosya)
                    if (remCount > 0 && _knownColumns.Contains("fileName"))
                    {
                        using var cmd = conn.CreateCommand();
                        string parentCol = _knownColumns.Contains("parentResourceID") ? "parentResourceID" : "''";
                        string statusCol = _knownColumns.Contains("fileStatus") ? "fileStatus" : "0";
                        cmd.CommandText = $"SELECT fileName, {parentCol}, {statusCol} FROM od_ClientFile_Records {where} LIMIT 1";
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            candidateStuckName = reader.GetString(0);
                            candidateStuckParentId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                            candidateStuckStatus = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                        }
                    }

                    // E. Akıllı Kuyruk İnceleyici (İlk 50 Bekleyen Dosya)
                    if (remCount > 0 && _knownColumns.Contains("fileName"))
                    {
                        using var cmd = conn.CreateCommand();
                        string parentCol = _knownColumns.Contains("parentResourceID") ? "parentResourceID" : "''";
                        string statusCol = _knownColumns.Contains("fileStatus") ? "fileStatus" : "0";
                        cmd.CommandText = $"SELECT fileName, size, {parentCol}, {statusCol} FROM od_ClientFile_Records {where} LIMIT 50";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string fname = reader.GetString(0);
                            long fsize = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                            string parentId = reader.IsDBNull(2) ? "" : reader.GetString(2);
                            int status = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                            string fullPath = OneDrivePathResolver.ResolveFullPath(parentId, fname);

                            queueList.Add(new QueueItem
                            {
                                Name = fname,
                                Size = fsize,
                                ParentResourceId = parentId,
                                FullPath = fullPath,
                                FileStatus = status
                            });

                            // Uzantı analizi
                            string ext = Path.GetExtension(fname).ToLowerInvariant();
                            if (string.IsNullOrEmpty(ext)) ext = "(diğer)";
                            if (!extensionStats.ContainsKey(ext)) extensionStats[ext] = (0, 0);
                            extensionStats[ext] = (extensionStats[ext].count + 1, extensionStats[ext].size + fsize);

                            // Gereksiz yığın ve çakışma kontrolleri
                            if (fname.Contains("node_modules") || fname.Contains(".venv") || fname.Contains(".git") || fname.Contains(@"\bin\"))
                            {
                                junkFilesCount++;
                            }

                            if (fname.Contains("-DESKTOP-") || fname.Contains("-LAPTOP-") || fname.Contains("(1)") || fname.Contains("(2)"))
                            {
                                conflictCount++;
                            }
                        }
                    }
                }
                catch { }
            }

            // Hız & Delta
            double instDown = 0;
            double instUp = 0;
            if (_lastReadBytes > 0 && _lastWriteBytes > 0)
            {
                ulong dRead = curRead >= _lastReadBytes ? curRead - _lastReadBytes : 0;
                ulong dWrite = curWrite >= _lastWriteBytes ? curWrite - _lastWriteBytes : 0;
                instDown = dRead / dt;
                instUp = dWrite / dt;
            }

            if (_lastRemBytes > 0 && remBytes < _lastRemBytes)
            {
                double dbDeltaSpeed = (_lastRemBytes - remBytes) / dt;
                _sessionTotalBytesTransferred += (_lastRemBytes - remBytes);
                if (dbDeltaSpeed > instDown) instDown = dbDeltaSpeed;
            }

            double alpha = 0.4;
            _smoothedDownloadSpeed = (alpha * instDown) + ((1 - alpha) * _smoothedDownloadSpeed);
            _smoothedUploadSpeed = (alpha * instUp) + ((1 - alpha) * _smoothedUploadSpeed);

            _lastReadBytes = curRead;
            _lastWriteBytes = curWrite;
            _lastRemBytes = remBytes;

            double totalSpeed = _smoothedDownloadSpeed + _smoothedUploadSpeed;
            if (totalSpeed > _peakSpeed) _peakSpeed = totalSpeed;

            _speedHistory.Dequeue();
            _speedHistory.Enqueue(totalSpeed);

            if (remBytes > _maxBatchBytes) _maxBatchBytes = remBytes;

            // Kilitli / Takılan Dosya Watchdog Kontrolü
            if (remCount > 0 && !string.IsNullOrEmpty(candidateStuckName) && candidateStuckName == _currentStuckFileName && totalSpeed < 15 * 1024)
            {
                _stuckTickCount++;
            }
            else
            {
                _stuckTickCount = 0;
                _currentStuckFileName = candidateStuckName;
                _currentStuckFilePath = OneDrivePathResolver.ResolveFullPath(candidateStuckParentId, candidateStuckName);
            }

            // UI Güncellemeleri
            TxtUploadSpeed.Text = $"{FormatBytes(_smoothedUploadSpeed)}/s";
            TxtDownloadSpeed.Text = $"{FormatBytes(_smoothedDownloadSpeed)}/s";
            TxtTotalTracked.Text = $"{totalFiles:N0} dosya izleniyor";
            TxtPeakSpeed.Text = $"Zirve: {FormatBytes(_peakSpeed)}/s";
            TxtSessionSummary.Text = $"Oturum: {FormatBytes(_sessionTotalBytesTransferred)}";

            if (!string.IsNullOrEmpty(lastFile))
            {
                TxtLastFile.Text = $"Son işlem: {lastFile}";
            }

            // Kuyruk Listesi
            _allQueueItems = queueList;
            ApplyQueueFilter();
            TxtQueueSummary.Text = $"{remCount:N0} dosya bekliyor";

            // Uzantı Profili
            if (extensionStats.Count > 0)
            {
                var topExts = extensionStats.OrderByDescending(x => x.Value.size).Take(3);
                var parts = topExts.Select(x => $"{x.Value.count} {x.Key}");
                TxtExtensionProfile.Text = $"Profil: {string.Join(" • ", parts)}";
            }
            else
            {
                TxtExtensionProfile.Text = "";
            }

            // Yığın Dosyaları Uyarısı
            if (junkFilesCount > 0)
            {
                QueueJunkAlert.Visibility = Visibility.Visible;
                TxtQueueJunk.Text = $"⚠️ Kuyrukta {junkFilesCount} geliştirici yığın dosyası (.git/node_modules) var.";
            }
            else
            {
                QueueJunkAlert.Visibility = Visibility.Collapsed;
            }

            // Çakışma Rozeti
            ConflictBadge.Visibility = conflictCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (conflictCount > 0) TxtConflict.Text = $"⚠️ {conflictCount} Çakışma";

            // Takılma Watchdog Değerlendirmesi (> 20 saniye aynı dosyada durup hız < 15 KB/s ise)
            if (_stuckTickCount >= 20 && !string.IsNullOrEmpty(_currentStuckFileName))
            {
                WarningBadge.Visibility = Visibility.Visible;
                TxtWarning.Text = "⚠️ Dosya Takıldı / Kilitli";

                StuckFileActionPanel.Visibility = Visibility.Visible;
                TxtStuckFileName.Text = _currentStuckFileName;

                // RestartManager ile kilitleyen süreci bul
                string lockerText = " (Bulut Yanıt Bekliyor)";
                if (File.Exists(_currentStuckFilePath))
                {
                    var lockers = RestartManagerHelper.FindLockingProcesses(_currentStuckFilePath);
                    if (lockers.Count > 0)
                    {
                        lockerText = $" (Kilit: {lockers[0].ProcessName})";
                    }
                    else if (RestartManagerHelper.IsFileLocked(_currentStuckFilePath))
                    {
                        lockerText = " (Dosya Yerel Olarak Kilitli)";
                    }
                }
                TxtStuckLocker.Text = lockerText;
            }
            else
            {
                WarningBadge.Visibility = Visibility.Collapsed;
                StuckFileActionPanel.Visibility = Visibility.Collapsed;
            }

            // Yüzde Hesabı
            double pct = 100.0;
            if (_maxBatchBytes > 0 && remBytes > 0)
            {
                double done = Math.Max(0, _maxBatchBytes - remBytes);
                pct = Math.Clamp((done / _maxBatchBytes) * 100.0, 0.0, 100.0);
            }
            else if (remCount == 0)
            {
                pct = 100.0;
            }

            TxtProgressPct.Text = $"%{pct:F1}";
            ProgressBarFill.Width = Math.Max(0, (pct / 100.0) * 398);

            // Dinamik Tepsi İkonu
            UpdateTrayIcon(pct);

            // Kalan Süre (ETA)
            double effectiveSpeed = Math.Max(totalSpeed, 250 * 1024);
            string etaText = "0 sn";
            if (remCount > 0 || totalSpeed > 25 * 1024)
            {
                _hasNotifiedCompletion = false;
                StatusBadge.Background = Brush("#261B0C");
                StatusBadge.BorderBrush = Brush("#451A03");
                TxtStatusDot.Foreground = Brush("#FBBF24");
                TxtStatus.Text = "Eşitleme Aktif";
                TxtStatus.Foreground = Brush("#FBBF24");

                TxtRemainingInfo.Text = $"{remCount:N0} dosya • {FormatBytes(remBytes)} kaldı";

                double etaSec = remBytes > 0 ? (remBytes / effectiveSpeed) : (remCount * 80 * 1024 / effectiveSpeed);
                etaText = FormatDuration(etaSec);
                TxtEta.Text = etaText;

                var finishTime = DateTime.Now.AddSeconds(etaSec);
                TxtFinishTime.Text = $"Bitiş: {finishTime:HH:mm:ss}";
            }
            else
            {
                StatusBadge.Background = Brush("#052E16");
                StatusBadge.BorderBrush = Brush("#065F46");
                TxtStatusDot.Foreground = Brush("#34D399");
                TxtStatus.Text = "Bulutla Eşitlendi";
                TxtStatus.Foreground = Brush("#34D399");

                TxtRemainingInfo.Text = "Tüm dosyalar güncel";
                TxtEta.Text = "0 sn";
                TxtFinishTime.Text = "Bitiş: Tamamlandı";
                TxtProgressPct.Text = "%100";
                ProgressBarFill.Width = 398;

                if (!_hasNotifiedCompletion && _maxBatchBytes > 0)
                {
                    _hasNotifiedCompletion = true;
                    OnSyncCompleted();
                }
            }

            // Mini Kapsül Güncelleme
            PillPct.Text = $"%{pct:F1}";
            PillSpeeds.Text = $"▲ {FormatBytes(_smoothedUploadSpeed)}/s  ▼ {FormatBytes(_smoothedDownloadSpeed)}/s";
            PillEta.Text = etaText;

            DrawSparkline();
        }

        private void ApplyQueueFilter()
        {
            string filter = TxtQueueSearch.Text.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                QueueListBox.ItemsSource = _allQueueItems;
            }
            else
            {
                QueueListBox.ItemsSource = _allQueueItems
                    .Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void TxtQueueSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyQueueFilter();
        }

        private void OnSyncCompleted()
        {
            _notifyIcon?.ShowBalloonTip(3500, "OneDrive Eşitlemesi", "Tüm dosyalar başarıyla bulut ile eşitlendi! 🎉", Forms.ToolTipIcon.Info);

            // ntfy push bildirimi
            string topic = TxtNtfyTopic.Text.Trim();
            if (!string.IsNullOrEmpty(topic))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var content = new StringContent("OneDrive esitlemesi basariyla tamamlandi!", Encoding.UTF8);
                        await _httpClient.PostAsync($"https://ntfy.sh/{topic}", content);
                    }
                    catch { }
                });
            }

            // Telegram push bildirimi
            string tgToken = TxtTelegramToken.Text.Trim();
            string tgChatId = TxtTelegramChatId.Text.Trim();
            if (!string.IsNullOrEmpty(tgToken) && !string.IsNullOrEmpty(tgChatId))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        string msg = Uri.EscapeDataString("OneDrive eşitlemesi başarıyla tamamlandı! 🎉");
                        string url = $"https://api.telegram.org/bot{tgToken}/sendMessage?chat_id={tgChatId}&text={msg}";
                        await _httpClient.GetAsync(url);
                    }
                    catch { }
                });
            }

            // Otomasyon & Güç Eylemleri
            if (ChkAutoAction.IsChecked == true)
            {
                int action = CmbPowerAction.SelectedIndex;
                switch (action)
                {
                    case 1: // Bilgisayarı Kapat
                        Process.Start("shutdown.exe", "/s /t 60 /c \"OneDrive esitlemesi bitti. Bilgisayar kapatiliyor.\"");
                        break;
                    case 2: // Uyku Modu
                        SetSuspendState(false, true, true);
                        break;
                    case 3: // Hazırda Beklet
                        Process.Start("shutdown.exe", "/h");
                        break;
                    case 4: // Yalnızca OneDrive Sürecini Kapat
                        RestartOneDriveProcess(terminateOnly: true);
                        break;
                    case 5: // Özel Betik Çalıştır
                        RunCustomPostSyncScript();
                        break;
                }
            }
        }

        private void RunCustomPostSyncScript()
        {
            string scriptPath = TxtCustomScriptPath.Text.Trim();
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath)) return;

            try
            {
                if (scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = scriptPath,
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }

        private void DrawSparkline()
        {
            try
            {
                GraphCanvas.Children.Clear();
                var values = _speedHistory.ToList();
                if (values.Count < 2) return;

                double maxVal = Math.Max(values.Max(), 150 * 1024);
                double w = GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : 398;
                double h = GraphCanvas.ActualHeight > 0 ? GraphCanvas.ActualHeight : 75;
                double step = w / (values.Count - 1);

                var points = new PointCollection();
                for (int i = 0; i < values.Count; i++)
                {
                    double x = i * step;
                    double norm = values[i] / maxVal;
                    double y = h - 4 - (norm * (h - 12));
                    points.Add(new Point(x, y));
                }

                var fillPoints = new PointCollection { new Point(0, h) };
                foreach (var pt in points) fillPoints.Add(pt);
                fillPoints.Add(new Point(w, h));

                var fillPoly = new Polygon
                {
                    Points = fillPoints,
                    Fill = Brush("#122338")
                };
                GraphCanvas.Children.Add(fillPoly);

                var line = new Polyline
                {
                    Points = points,
                    Stroke = Brush("#38BDF8"),
                    StrokeThickness = 2
                };
                GraphCanvas.Children.Add(line);

                var lastPt = points.Last();
                var ellipse = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Brush("#67E8F9"),
                    Stroke = Brush("#0284C7"),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(ellipse, lastPt.X - 3);
                Canvas.SetTop(ellipse, lastPt.Y - 3);
                GraphCanvas.Children.Add(ellipse);
            }
            catch { }
        }

        // ================= KİLİT VE TAKILMA AKSİYONLARI =================
        private void WarningBadge_Click(object sender, MouseButtonEventArgs e)
        {
            StuckFileActionPanel.Visibility = StuckFileActionPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void BtnRevealStuck_Click(object sender, RoutedEventArgs e)
        {
            RevealInExplorer(_currentStuckFilePath);
        }

        private void BtnNudgeStuck_Click(object sender, RoutedEventArgs e)
        {
            NudgeFile(_currentStuckFilePath);
        }

        private void BtnRestartOneDrive_Click(object sender, RoutedEventArgs e)
        {
            RestartOneDriveProcess();
        }

        private static void RevealInExplorer(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                else if (Directory.Exists(fullPath))
                {
                    Process.Start("explorer.exe", $"\"{fullPath}\"");
                }
                else
                {
                    string root = OneDrivePathResolver.GetOneDriveRoot();
                    Process.Start("explorer.exe", $"\"{root}\"");
                }
            }
            catch { }
        }

        private void NudgeFile(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    // Dosyayı dokunup uyandır (touch/read-write)
                    using (var fs = File.Open(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        fs.Flush();
                    }
                    File.SetLastWriteTime(fullPath, DateTime.Now);
                    System.Windows.MessageBox.Show($"Dosya başarıyla dürtüldü ve zaman damgası güncellendi:\n{fullPath}", "Dürtme Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show("Dosya yerel diskte henüz bulunamadı veya bulut tarafında onay bekliyor.", "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Dosya dürtülemedi: {ex.Message}\nKullanan uygulamayı kapatmayı deneyin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RestartOneDriveProcess(bool terminateOnly = false)
        {
            Task.Run(() =>
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName("OneDrive"))
                    {
                        try { p.Kill(); } catch { }
                    }

                    if (!terminateOnly)
                    {
                        System.Threading.Thread.Sleep(1500);
                        string odExe = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe");
                        if (!File.Exists(odExe))
                        {
                            odExe = @"C:\Program Files\Microsoft OneDrive\OneDrive.exe";
                        }
                        if (File.Exists(odExe))
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = odExe,
                                Arguments = "/background",
                                UseShellExecute = true
                            });
                        }
                    }
                }
                catch { }
            });

            if (!terminateOnly)
            {
                System.Windows.MessageBox.Show("OneDrive kapatıldı ve arka planda yeniden başlatılıyor...", "OneDrive Yenilendi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ================= KUYRUK MENÜ ETKİLEŞİMLERİ =================
        private void ContextReveal_Click(object sender, RoutedEventArgs e)
        {
            if (QueueListBox.SelectedItem is QueueItem item)
            {
                RevealInExplorer(item.FullPath);
            }
        }

        private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (QueueListBox.SelectedItem is QueueItem item)
            {
                System.Windows.Clipboard.SetText(item.FullPath);
            }
        }

        private void ContextCheckLock_Click(object sender, RoutedEventArgs e)
        {
            if (QueueListBox.SelectedItem is QueueItem item)
            {
                var lockers = RestartManagerHelper.FindLockingProcesses(item.FullPath);
                if (lockers.Count > 0)
                {
                    var msg = string.Join("\n", lockers.Select(l => $"• {l.ProcessName} (PID: {l.ProcessId})"));
                    System.Windows.MessageBox.Show($"Bu dosya şu süreçler tarafından kullanılıyor:\n\n{msg}", "Kilit Bilgisi", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    bool isLocked = RestartManagerHelper.IsFileLocked(item.FullPath);
                    string status = isLocked ? "Dosya başka bir program tarafından kilitli." : "Dosya üzerinde aktif işlem kilidi bulunamadı (Eşitlemeye hazır).";
                    System.Windows.MessageBox.Show(status, "Kilit Bilgisi", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void ContextNudge_Click(object sender, RoutedEventArgs e)
        {
            if (QueueListBox.SelectedItem is QueueItem item)
            {
                NudgeFile(item.FullPath);
            }
        }

        // ================= AYARLAR & TEST ETKİLEŞİMLERİ =================
        private void BtnBrowseScript_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Betik Dosyaları (*.bat;*.ps1;*.cmd)|*.bat;*.ps1;*.cmd|Tüm Dosyalar (*.*)|*.*",
                Title = "Eşitleme Sonrası Çalıştırılacak Betiği Seçin"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtCustomScriptPath.Text = dlg.FileName;
            }
        }

        private void BtnTestNtfy_Click(object sender, RoutedEventArgs e)
        {
            string topic = TxtNtfyTopic.Text.Trim();
            if (string.IsNullOrEmpty(topic))
            {
                System.Windows.MessageBox.Show("Lütfen bir ntfy.sh topic adı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var content = new StringContent("OneDrive Monitor test bildirimi basarili! 🚀", Encoding.UTF8);
                    var resp = await _httpClient.PostAsync($"https://ntfy.sh/{topic}", content);
                    if (resp.IsSuccessStatusCode)
                    {
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"'{topic}' konusuna test bildirimi iletildi!", "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information));
                    }
                    else
                    {
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"ntfy yanıt kodu: {resp.StatusCode}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show($"Bağlantı hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error));
                }
            });
        }

        // ================= UI ETKİLEŞİMLERİ =================
        private void MiniPill_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                TogglePillMode();
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void TogglePillMode_Click(object sender, RoutedEventArgs e)
        {
            TogglePillMode();
        }

        private void TogglePillMode()
        {
            _isPillMode = !_isPillMode;
            if (_isPillMode)
            {
                FullDashboardView.Visibility = Visibility.Collapsed;
                MiniPillView.Visibility = Visibility.Visible;
                Width = 290;
                Height = 38;
            }
            else
            {
                MiniPillView.Visibility = Visibility.Collapsed;
                FullDashboardView.Visibility = Visibility.Visible;
                Width = 440;
                Height = 570;
            }
        }

        private void BtnViewQueue_Click(object sender, RoutedEventArgs e)
        {
            if (ViewQueue.Visibility == Visibility.Visible)
            {
                ViewQueue.Visibility = Visibility.Collapsed;
                ViewGraph.Visibility = Visibility.Visible;
                BtnViewQueue.Foreground = Brush("#94A3B8");
            }
            else
            {
                ViewSettings.Visibility = Visibility.Collapsed;
                ViewGraph.Visibility = Visibility.Collapsed;
                ViewQueue.Visibility = Visibility.Visible;
                BtnViewQueue.Foreground = Brush("#38BDF8");
                BtnViewSettings.Foreground = Brush("#94A3B8");
            }
        }

        private void BtnViewSettings_Click(object sender, RoutedEventArgs e)
        {
            if (ViewSettings.Visibility == Visibility.Visible)
            {
                ViewSettings.Visibility = Visibility.Collapsed;
                ViewGraph.Visibility = Visibility.Visible;
                BtnViewSettings.Foreground = Brush("#94A3B8");
            }
            else
            {
                ViewQueue.Visibility = Visibility.Collapsed;
                ViewGraph.Visibility = Visibility.Collapsed;
                ViewSettings.Visibility = Visibility.Visible;
                BtnViewSettings.Foreground = Brush("#38BDF8");
                BtnViewQueue.Foreground = Brush("#94A3B8");
            }
        }

        private void BtnExportReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string csvPath = Path.Combine(desktop, $"OneDrive_Sync_Raporu_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var sb = new StringBuilder();
                sb.AppendLine("Parametre,Deger");
                sb.AppendLine($"Oturum Baslangici,{_sessionStartTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Rapor Alinma Zamani,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Oturumda Aktarilan Toplam Veri,{FormatBytes(_sessionTotalBytesTransferred)}");
                sb.AppendLine($"Ulasilan Zirve Hiz,{FormatBytes(_peakSpeed)}/s");

                File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
                System.Windows.MessageBox.Show($"Oturum raporu masaüstünüze kaydedildi:\n{csvPath}", "Rapor Hazır", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Rapor kaydedilemedi: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static string FormatBytesStatic(double bytes) => FormatBytes(bytes);

        private static string FormatBytes(double bytes)
        {
            if (bytes < 1024) return $"{bytes:F0} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024):F1} MB";
            return $"{bytes / (1024 * 1024 * 1024):F2} GB";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds)) return "Hesaplanıyor...";
            int totalSec = (int)seconds;
            int m = (totalSec / 60) % 60;
            int h = totalSec / 3600;
            int s = totalSec % 60;

            if (h > 0) return $"{h} sa {m:D2} dk";
            if (m > 0) return $"{m} dk {s:D2} sn";
            return $"{s} sn";
        }

        private void BtnPin_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            BtnPin.Foreground = Topmost ? Brush("#38BDF8") : Brush("#64748B");
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            _notifyIcon?.ShowBalloonTip(2000, "OneDrive Sync Monitor", "Uygulama arka planda sistem tepsisinde çalışıyor.", Forms.ToolTipIcon.Info);
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _notifyIcon?.Dispose();
            WpfApplication.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            _notifyIcon?.Dispose();
            base.OnClosed(e);
        }
    }
}
