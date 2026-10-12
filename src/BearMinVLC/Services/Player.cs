using System.Globalization;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace BearMinVLC.Services;

public sealed record TrackItem(int Id, string Label, string? Detail);

/// <summary>Per-file options passed when a media is opened.</summary>
public sealed record OpenOptions(string SubtitleLanguage, string AudioLanguage);

/// <summary>
/// Thin wrapper over libVLC. All events are raised on the UI thread.
/// The engine can be rebuilt with new start-up options (<see cref="Rebuild"/>); subscribers keep working.
/// </summary>
public sealed class Player : IDisposable
{
    static readonly string[] BaseArgs =
    [
        "--no-video-title-show",   // no filename flash over the video
        "--no-osd",                // we draw our own OSD
        "--no-snapshot-preview",   // screenshots shouldn't pop a thumbnail over the video
        "--no-stats",
        "--quiet",
    ];

    public LibVLC Lib { get; private set; }
    public MediaPlayer Mp { get; private set; }

    /// <summary>Raised after <see cref="Rebuild"/> swaps in a new MediaPlayer (the video surface must re-attach).</summary>
    public event Action? EngineChanged;

    public event Action? Opening;
    public event Action? Playing;
    public event Action? Paused;
    public event Action? Stopped;
    public event Action? EndReached;
    public event Action? Error;
    public event Action? TracksChanged;
    public event Action<long>? TimeChanged;
    public event Action<long>? LengthChanged;
    public event Action<float>? Buffering;

    public Player(string[] engineArgs, bool hardwareDecoding)
    {
        Core.Initialize();
        (Lib, Mp) = CreateEngine(engineArgs, hardwareDecoding);
    }

    (LibVLC, MediaPlayer) CreateEngine(string[] engineArgs, bool hardwareDecoding)
    {
        var lib = new LibVLC([.. BaseArgs, .. engineArgs]);
        var mp = new MediaPlayer(lib)
        {
            EnableHardwareDecoding = hardwareDecoding,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };

        mp.Opening += (_, _) => Ui(mp, () => Opening?.Invoke());
        mp.Playing += (_, _) => Ui(mp, () => Playing?.Invoke());
        mp.Paused += (_, _) => Ui(mp, () => Paused?.Invoke());
        mp.Stopped += (_, _) => Ui(mp, () => Stopped?.Invoke());
        mp.EndReached += (_, _) => Ui(mp, () => EndReached?.Invoke());
        mp.EncounteredError += (_, _) => Ui(mp, () => Error?.Invoke());
        mp.TimeChanged += (_, e) => { var t = e.Time; Ui(mp, () => TimeChanged?.Invoke(t)); };
        mp.LengthChanged += (_, e) => { var l = e.Length; Ui(mp, () => LengthChanged?.Invoke(l)); };
        mp.Buffering += (_, e) => { var c = e.Cache; Ui(mp, () => Buffering?.Invoke(c)); };
        mp.ESAdded += (_, _) => Ui(mp, () => TracksChanged?.Invoke());
        mp.ESDeleted += (_, _) => Ui(mp, () => TracksChanged?.Invoke());
        mp.ESSelected += (_, _) => Ui(mp, () => TracksChanged?.Invoke());
        return (lib, mp);
    }

    /// <summary>
    /// Replaces the engine with one started with new options. Stops whatever is playing;
    /// the caller reopens it. The old engine is torn down off the UI thread.
    /// </summary>
    public void Rebuild(string[] engineArgs, bool hardwareDecoding)
    {
        var (oldLib, oldMp) = (Lib, Mp);
        oldMp.Stop();
        Interlocked.Increment(ref _generation);
        (Lib, Mp) = CreateEngine(engineArgs, hardwareDecoding);
        EngineChanged?.Invoke();
        Task.Run(() =>
        {
            oldMp.Dispose();
            oldLib.Dispose();
        });
    }

    // Bumped after each Open (and Rebuild) so events still queued from the previous file are dropped.
    int _generation;

    void Ui(MediaPlayer source, Action a)
    {
        var gen = Volatile.Read(ref _generation);
        Dispatcher.UIThread.Post(() =>
        {
            if (gen == _generation && ReferenceEquals(source, Mp)) a();
        });
    }

    public static bool IsUrl(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var u) && !u.IsFile && u.Scheme.Length > 1;

    public void Open(string source, long startMs, OpenOptions options)
    {
        using var media = IsUrl(source)
            ? new Media(Lib, new Uri(source))
            : new Media(Lib, source, FromType.FromPath);
        if (startMs > 0)
            media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        if (options.SubtitleLanguage.Length > 0)
            media.AddOption(":sub-language=" + options.SubtitleLanguage);
        if (options.AudioLanguage.Length > 0)
            media.AddOption(":audio-language=" + options.AudioLanguage);
        Mp.Play(media);
        Interlocked.Increment(ref _generation);
    }

    public bool IsPlaying => Mp.IsPlaying;
    public bool IsSeekable => Mp.IsSeekable;
    public long Length => Mp.Length;

    public long Time
    {
        get => Mp.Time;
        set => Mp.Time = Math.Max(0, value);
    }

    public void Stop() => Mp.Stop();
    public void Pause() => Mp.SetPause(true);
    public void Resume() => Mp.SetPause(false);
    public void NextFrame() => Mp.NextFrame();

    public float Rate => Mp.Rate;
    public void SetRate(float rate) => Mp.SetRate(rate);

    public int VideoTrackCount => Mp.VideoTrackCount;

    public string? AspectRatio
    {
        get => Mp.AspectRatio;
        set => Mp.AspectRatio = value;
    }

    // Delays are microseconds in libVLC.
    public long SubtitleDelayMs { get => Mp.SpuDelay / 1000; set => Mp.SetSpuDelay(value * 1000); }
    public long AudioDelayMs { get => Mp.AudioDelay / 1000; set => Mp.SetAudioDelay(value * 1000); }

    public bool TakeSnapshot(string path, uint width = 0) => Mp.TakeSnapshot(0, path, width, 0);

    // ---- Audio output devices ----

    /// <summary>Speakers, headphones, HDMI… as (id, name). Empty id = the Windows default.</summary>
    public IReadOnlyList<(string Id, string Name)> AudioDevices()
    {
        var devices = Mp.AudioOutputDeviceEnum;
        if (devices.Length == 0)
            devices = Lib.AudioOutputDevices("mmdevice");
        return devices
            .Where(d => !string.IsNullOrEmpty(d.DeviceIdentifier))
            .Select(d => (d.DeviceIdentifier!, string.IsNullOrWhiteSpace(d.Description) ? d.DeviceIdentifier! : d.Description!))
            .ToList();
    }

    public void SetAudioDevice(string id)
    {
        try { Mp.SetOutputDevice(id); }
        catch (Exception) { }
    }

    public void AddSubtitleFile(string path) =>
        Mp.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, select: true);

    // ---- Tracks ----

    public int CurrentSubtitle => Mp.Spu;
    public int CurrentAudio => Mp.AudioTrack;
    public void SetSubtitle(int id) => Mp.SetSpu(id);
    public void SetAudio(int id) => Mp.SetAudioTrack(id);

    public IReadOnlyList<TrackItem> SubtitleTracks() => Describe(Mp.SpuDescription, audio: false);
    public IReadOnlyList<TrackItem> AudioTracks() => Describe(Mp.AudioTrackDescription, audio: true);

    IReadOnlyList<TrackItem> Describe(LibVLCSharp.Shared.Structures.TrackDescription[] descriptions, bool audio)
    {
        var details = new Dictionary<int, MediaTrack>();
        using (var media = Mp.Media)
        {
            if (media != null)
                foreach (var t in media.Tracks)
                    details[t.Id] = t;
        }

        var list = new List<TrackItem>();
        int n = 0;
        foreach (var d in descriptions)
        {
            if (d.Id < 0)
                continue; // "Disable" — the menus add their own "Off"
            n++;
            details.TryGetValue(d.Id, out var t);
            var lang = Lang.Name(t.Language) ?? Lang.FromVlcName(d.Name);
            var desc = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description.Trim();
            var channels = audio && t.TrackType == TrackType.Audio ? Channels(t.Data.Audio.Channels) : null;

            string label;
            var detail = new List<string>();
            if (desc != null && !string.Equals(desc, lang, StringComparison.OrdinalIgnoreCase))
            {
                label = desc;
                if (lang != null) detail.Add(lang);
            }
            else if (lang != null)
            {
                label = lang;
            }
            else
            {
                label = $"Track {n}";
                detail.Add("und");
            }
            if (channels != null) detail.Add(channels);
            list.Add(new TrackItem(d.Id, label, detail.Count > 0 ? string.Join(" · ", detail) : null));
        }

        // Two tracks that read the same ("English · Stereo") get their number so they can be told apart.
        var dupes = list.GroupBy(t => (t.Label, t.Detail)).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        return list.Select((t, i) => dupes.Contains(t)
            ? t with { Detail = (t.Detail != null ? t.Detail + " · " : "") + $"Track {i + 1}" }
            : t).ToList();
    }

    static string? Channels(uint n) => n switch
    {
        0 => null,
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{n} ch",
    };

    public void Dispose()
    {
        Mp.Stop();
        Mp.Dispose();
        Lib.Dispose();
    }
}
