using Avalonia;
using Avalonia.Media;
using BearMinVLC.Services;
using Velopack;

namespace BearMinVLC;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles the installer's install/update/uninstall hooks (and exits for them),
        // and applies an update that was downloaded last session.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => ShellIntegration.Register(Environment.ProcessPath!))
            .OnAfterUpdateFastCallback(_ => ShellIntegration.Register(Environment.ProcessPath!))
            .OnBeforeUninstallFastCallback(_ => ShellIntegration.Unregister())
            .Run();

        if (args.Contains("--shots"))
        {
            // Screenshot runs use a throwaway data folder so they never touch the real recent list.
            var data = Path.Combine(Path.GetTempPath(), "BearMinVLC-shots");
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            Environment.SetEnvironmentVariable("BEARMINVLC_DATA", data);
        }
        // A second launch (e.g. "Open with" from Explorer) hands its file to the window that is already open.
        else if (!SingleInstance.TryBecomePrimary(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://BearMinVLC/Assets/Fonts#Geist" })
            .LogToTrace();
}
