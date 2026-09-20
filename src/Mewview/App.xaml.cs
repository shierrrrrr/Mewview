using System;
using System.IO;
using System.Windows;

namespace Mewview;

public partial class App : Application
{
    private static readonly string ErrorLogPath =
        Path.Combine(Path.GetTempPath(), "mewview_error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText(ErrorLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\n\n");
            }
            catch { /* ignore logging failures */ }

            MessageBox.Show("An unexpected error occurred. Details were written to:\n" + ErrorLogPath,
                "Mewview", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = new MainWindow();
        if (e.Args.Length > 0)
            window.InitialImagePath = e.Args[0];
        window.Show();
    }
}
