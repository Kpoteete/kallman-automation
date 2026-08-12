using System.IO;
using System.Windows;

namespace KwiAutomationLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(argument => argument.Equals("--validate", StringComparison.OrdinalIgnoreCase)))
        {
            Directory.CreateDirectory(AutomationCatalog.LogDirectory);
            var isValid = AutomationCatalog.TryValidate(out var message);
            File.WriteAllText(
                Path.Combine(AutomationCatalog.LogDirectory, "Launcher-validation.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(isValid ? "PASS" : "FAIL")}: {message}{Environment.NewLine}");
            Shutdown(isValid ? 0 : 1);
            return;
        }

        var previewMode = e.Args.Any(argument => argument.Equals("--preview", StringComparison.OrdinalIgnoreCase));
        var previewSummary = e.Args.Any(argument => argument.Equals("--preview-summary", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(previewMode || previewSummary, previewSummary);
        MainWindow = window;
        window.Show();
    }
}
