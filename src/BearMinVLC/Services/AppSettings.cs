using System.Globalization;
using System.Text.Json;

namespace BearMinVLC.Services;

/// <summary>Window placement, volume and the Settings panel, stored in %AppData%\BearMinVLC\settings.json.</summary>
public sealed class AppSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 680;
    public bool Maximized { get; set; }
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public bool SeenShortcutHint { get; set; }

    // Settings panel
    public bool Resume { get; set; } = true;
    public int SkipSeconds { get; set; } = 10;
    public bool HardwareDecoding { get; set; } = true;
    public string Deinterlace { get; set; } = "auto";   // auto | on | off
    public int SubtitleScale { get; set; } = 100;        // percent
    public bool SubtitlesAboveControls { get; set; }
    public string SubtitleLanguage { get; set; } = "";   // ISO 639-1, "" = automatic
    public string AudioLanguage { get; set; } = "";
    public string AudioDevice { get; set; } = "";        // "" = Windows default

    /// <summary>
    /// libVLC options that only take effect when the engine starts (subtitle rendering, deinterlacing).
    /// Changing any of these rebuilds the engine.
    /// </summary>
    public string[] EngineArgs(double scaling)
    {
        var args = new List<string>
        {
            "--sub-text-scale=" + Math.Clamp(SubtitleScale, 10, 500).ToString(CultureInfo.InvariantCulture),
            Deinterlace switch { "on" => "--deinterlace=1", "off" => "--deinterlace=0", _ => "--deinterlace=-1" },
            "--deinterlace-mode=yadif2x",
        };
        // Lift subtitles clear of the control bar (~100 px tall) when asked to.
        if (SubtitlesAboveControls)
            args.Add("--sub-margin=" + ((int)Math.Round(100 * scaling)).ToString(CultureInfo.InvariantCulture));
        return args.ToArray();
    }

    static string FilePath => Path.Combine(Library.DataDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception)
        {
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Library.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception)
        {
        }
    }
}
