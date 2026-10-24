using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OneDriveMonitor;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogAndShowError("Dispatcher Hata: " + e.Exception.ToString());
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogAndShowError("Domain Hata: " + e.ExceptionObject?.ToString());
    }

    private void LogAndShowError(string error)
    {
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] {error}\n\n");
            System.Windows.MessageBox.Show(error, "OneDrive Monitor Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
