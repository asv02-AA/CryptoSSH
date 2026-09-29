using System.Windows;
using System.IO;

namespace CryptoSSH;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "CryptoSSH", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        try
        {
            if (e.Args.Contains("--self-test")) { Environment.Exit(SelfTest.Run()); return; }
            if (e.Args.Contains("--token-self-test")) { Environment.Exit(TokenTests.Run()); return; }
            var store = new Store(e.Args.Contains("--smoke-test") ? Environment.GetEnvironmentVariable("RUTOKEN_SSH_TEST_DATA") ?? Path.Combine(Path.GetTempPath(), "CryptoSSH-smoke-" + Guid.NewGuid().ToString("N")) : null);
            var window = new MainWindow(store);
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--smoke-test")) window.RunSmokeTest();
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Не удалось запустить CryptoSSH", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
}
