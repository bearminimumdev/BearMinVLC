using System.Text.Json;

namespace BearMinVLC.Services;

public sealed class RecentItem
{
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsUrl { get; set; }
    public long LengthMs { get; set; }
    public long PositionMs { get; set; }
    public bool Watched { get; set; }
    public DateTime LastPlayedUtc { get; set; }
    public int? SubtitleId { get; set; }
    public int? AudioId { get; set; }
    public string? Thumb { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanResume => !Watched && PositionMs > 0;
}

/// <summary>Recent files with resume points, stored in %AppData%\BearMinVLC\recent.json.</summary>
public sealed class Library
{
    const int MaxItems = 20;
    public const long MinResumeMs = 30_000;
    public const double WatchedFraction = 0.95;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // BEARMINVLC_DATA lets the screenshot harness run without touching the real recent list.
    static readonly string? DataOverride = Environment.GetEnvironmentVariable("BEARMINVLC_DATA");

    public static string DataDir { get; } = DataOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BearMinVLC");
    // Not under %LocalAppData%\BearMinVLC: that folder belongs to the installer and is replaced on uninstall.
    public static string ThumbDir { get; } = Path.Combine(DataDir, "thumbs");

    readonly string _file = Path.Combine(DataDir, "recent.json");
    public List<RecentItem> Items { get; private set; } = [];

    public static Library Load()
    {
        var lib = new Library();
        try
        {
            if (File.Exists(lib._file))
                lib.Items = JsonSerializer.Deserialize<List<RecentItem>>(File.ReadAllText(lib._file), Json) ?? [];
        }
        catch (Exception)
        {
            lib.Items = []; // a corrupt file shouldn't stop the player from starting
        }
        return lib;
    }

    public RecentItem? Find(string source) =>
        Items.FirstOrDefault(i => string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>Moves (or adds) the item to the top of the list.</summary>
    public RecentItem Touch(string source)
    {
        var item = Find(source);
        if (item == null)
        {
            var isUrl = Player.IsUrl(source);
            item = new RecentItem { Source = source, IsUrl = isUrl, Title = TitleFor(source, isUrl) };
        }
        Items.Remove(item);
        Items.Insert(0, item);
        item.LastPlayedUtc = DateTime.UtcNow;
        while (Items.Count > MaxItems)
            Items.RemoveAt(Items.Count - 1);
        Save();
        return item;
    }

    public static string TitleFor(string source, bool isUrl)
    {
        if (!isUrl)
            return Path.GetFileName(source);
        if (Uri.TryCreate(source, UriKind.Absolute, out var u))
        {
            var last = u.Segments.LastOrDefault()?.Trim('/');
            return string.IsNullOrEmpty(last) ? u.Host : Uri.UnescapeDataString(last);
        }
        return source;
    }

    /// <summary>Applies the resume rules: only between 30 s in and 95% through.</summary>
    public void Record(RecentItem item, long positionMs, long lengthMs)
    {
        if (lengthMs > 0)
            item.LengthMs = lengthMs;
        if (item.IsUrl || item.LengthMs <= 0)
        {
            item.PositionMs = 0;
        }
        else if (positionMs >= item.LengthMs * WatchedFraction)
        {
            item.Watched = true;
            item.PositionMs = 0;
        }
        else if (positionMs >= MinResumeMs)
        {
            item.Watched = false;
            item.PositionMs = positionMs;
        }
        else
        {
            item.PositionMs = 0;
        }
        Save();
    }

    public void Remove(RecentItem item)
    {
        Items.Remove(item);
        Save();
    }

    public List<RecentItem> Clear()
    {
        var old = Items;
        Items = [];
        Save();
        return old;
    }

    public void Restore(List<RecentItem> items)
    {
        Items = items;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items, Json));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception)
        {
            // Losing a resume point is better than crashing mid-playback.
        }
    }
}
