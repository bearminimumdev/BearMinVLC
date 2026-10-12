using Microsoft.Win32;

namespace BearMinVLC.Services;

/// <summary>
/// Registers BearMinVLC with Windows as a media player (per user, no admin): it shows up under
/// "Open with" and in Settings → Default apps. Windows doesn't allow apps to make themselves the
/// default; the person picks it there.
/// </summary>
static class ShellIntegration
{
    const string ProgId = "BearMinVLC.Media";
    const string CapabilitiesKey = @"Software\BearMinVLC\Capabilities";

    static readonly string[] Extensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts",
        ".3gp", ".ogv", ".vob", ".divx", ".rmvb", ".asf",
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma", ".mka",
    ];

    public static void Register(string exePath)
    {
        var open = $"\"{exePath}\" \"%1\"";
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "Media file");
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exePath}\",0");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", open);
        }

        // "Open with → Choose an app" picks made against the bare exe (like an earlier portable copy)
        // live here; point them at this install so an existing default keeps working.
        using (var app = classes.CreateSubKey(@"Applications\BearMinVLC.exe"))
        {
            app.SetValue("FriendlyAppName", "BearMinVLC");
            using var cmd = app.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", open);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in Extensions) types.SetValue(ext, "");
        }

        foreach (var ext in Extensions)
        {
            using var withProgIds = classes.CreateSubKey($@"{ext}\OpenWithProgids");
            withProgIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", "BearMinVLC");
            caps.SetValue("ApplicationDescription", "A sleek, minimal video player built on VLC's playback engine.");
            caps.SetValue("ApplicationIcon", $"\"{exePath}\",0");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions) assoc.SetValue(ext, ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue("BearMinVLC", CapabilitiesKey);

        Native.NotifyAssociationsChanged();
    }

    public static void Unregister()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        if (classes != null)
        {
            classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(@"Applications\BearMinVLC.exe", throwOnMissingSubKey: false);
            foreach (var ext in Extensions)
            {
                using var withProgIds = classes.OpenSubKey($@"{ext}\OpenWithProgids", writable: true);
                withProgIds?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\BearMinVLC", throwOnMissingSubKey: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue("BearMinVLC", throwOnMissingValue: false);

        Native.NotifyAssociationsChanged();
    }
}
