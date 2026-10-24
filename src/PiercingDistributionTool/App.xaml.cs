using System.IO;
using System.Windows;

namespace PiercingDistributionTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(args.Exception.Message, "Piercing distribution tool", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        base.OnStartup(e);
        new MainWindow().Show();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PiercingDistributionTool");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:u}\n{ex}\n\n");
        }
        catch
        {
            // nothing more to do
        }
    }
}
