using System.Runtime.InteropServices;

namespace BearMinVLC.Services;

static partial class Native
{
    const uint EsContinuous = 0x80000000;
    const uint EsSystemRequired = 0x00000001;
    const uint EsDisplayRequired = 0x00000002;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    /// <summary>Stops the screen sleeping while a video plays. Must be called from the UI thread.</summary>
    public static void KeepAwake(bool on)
    {
        if (OperatingSystem.IsWindows())
            SetThreadExecutionState(on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
    }

    const int DwmwaWindowCornerPreference = 33;
    public const int CornerDefault = 0, CornerSquare = 1, CornerRound = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void SetCorners(IntPtr hwnd, int preference)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && hwnd != IntPtr.Zero)
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    /// <summary>Cursor position in screen pixels; steadier than pointer events while the window is moving.</summary>
    public static Avalonia.PixelPoint CursorPosition() =>
        GetCursorPos(out var p) ? new Avalonia.PixelPoint(p.X, p.Y) : default;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tells Explorer that file associations changed so "Open with" refreshes.</summary>
    public static void NotifyAssociationsChanged() =>
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    /// <summary>Explorer's ordering: "Episode 2" before "Episode 10".</summary>
    public static int CompareNatural(string a, string b) =>
        OperatingSystem.IsWindows() ? StrCmpLogicalW(a, b) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
}
