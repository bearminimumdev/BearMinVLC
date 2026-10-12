using System.Globalization;
using System.Text.RegularExpressions;

namespace BearMinVLC.Services;

static class Fmt
{
    /// <summary>12:48, or 1:18:40 past an hour.</summary>
    public static string Time(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Speed(float rate) =>
        rate.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}

/// <summary>Turns ISO 639 codes from media files into readable language names ("spa" → "Español").</summary>
static partial class Lang
{
    static readonly Lazy<Dictionary<string, string>> Names = new(Build);

    // ISO 639-2/B codes that differ from the 639-2/T codes .NET knows.
    static readonly Dictionary<string, string> BibliographicAliases = new()
    {
        ["fre"] = "fra", ["ger"] = "deu", ["chi"] = "zho", ["dut"] = "nld", ["cze"] = "ces",
        ["gre"] = "ell", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["ice"] = "isl",
        ["mac"] = "mkd", ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya",
        ["geo"] = "kat", ["may"] = "msa", ["wel"] = "cym", ["tib"] = "bod",
    };

    public static string? Name(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        code = code.Trim().ToLowerInvariant();
        if (code is "und" or "unknown" or "mis" or "mul" or "zxx")
            return null;
        if (BibliographicAliases.TryGetValue(code, out var alias))
            code = alias;
        return Names.Value.TryGetValue(code, out var name) ? name : null;
    }

    /// <summary>libVLC's own track names look like "Track 1 - [English]".</summary>
    public static string? FromVlcName(string? vlcName)
    {
        if (string.IsNullOrWhiteSpace(vlcName))
            return null;
        var m = BracketRegex().Match(vlcName);
        if (m.Success)
            return m.Groups[1].Value.Trim();
        return TrackNRegex().IsMatch(vlcName) ? null : vlcName.Trim();
    }

    static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.IsNullOrEmpty(c.Name))
                continue;
            var native = c.NativeName;
            var cut = native.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) native = native[..cut];
            native = native.Length > 0 ? char.ToUpper(native[0], c) + native[1..] : native;
            map.TryAdd(c.TwoLetterISOLanguageName, native);
            map.TryAdd(c.ThreeLetterISOLanguageName, native);
        }
        return map;
    }

    [GeneratedRegex(@"\[(.+?)\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"^Track \d+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrackNRegex();
}
