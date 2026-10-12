#if DEBUG
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using BearMinVLC.Views;
using LibVLCSharp.Shared;
using Point = Avalonia.Point;

namespace BearMinVLC;

/// <summary>
/// Debug-only harness: <c>BearMinVLC --shots &lt;dir&gt; [--gen | media]</c> walks through every screen,
/// screenshots each one, runs a few real mouse/keyboard checks, and writes log.txt.
/// </summary>
static class DevShots
{
    static string _dir = "";
    static StreamWriter? _log;

    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        _log?.WriteLine(line);
        _log?.Flush();
    }

    public static async Task Run(MainWindow win, string[] args)
    {
        _dir = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "shots");
        Directory.CreateDirectory(_dir);
        _log = new StreamWriter(Path.Combine(_dir, "log.txt"), append: false);
        PlayerView.Trace = s => Log("  · " + s);
        var v = win.View;
        if (args.Contains("--splash"))
        {
            MakeSplash(Path.Combine(_dir, "splash.png"));
            _log.Dispose();
            win.Close();
            return;
        }
        if (args.Contains("--icon"))
        {
            MakeIcon(Path.Combine(_dir, "BearMinVLC.ico"));
            _log.Dispose();
            win.Close();
            return;
        }
        try
        {
            string? media = args.Contains("--gen") ? await GenerateMedia() : args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            Log($"media = {media}");
            win.Topmost = true;
            win.Width = 1280;
            win.Height = 720;
            await Task.Delay(1500);

            await Shot(win, "01-start");
            v.ToggleShortcuts();
            await Shot(win, "02-shortcuts");
            v.CloseDialogs();
            v.ShowSettings();
            await Shot(win, "02b-settings");
            v.OpenLanguageDropDownForTest(true);
            await Shot(win, "02c-settings-dropdown");
            v.OpenLanguageDropDownForTest(false);
            v.CloseDialogs();
            v.ShowUrlDialog();
            await Shot(win, "03-url");
            v.CloseDialogs();

            if (media != null)
            {
                v.OpenSource(media);
                await WaitFor(() => v.CurrentMode == PlayerView.Mode.Playing, 10_000);
                Log($"after open: mode={v.CurrentMode} length={win.Player.Length} tracks: subs={win.Player.SubtitleTracks().Count} audio={win.Player.AudioTracks().Count}");
                await Task.Delay(1500);
                v.ShowChrome();
                await Shot(win, "04-playing");

                // Settings that rebuild the engine must bring the video back at the same spot.
                var before = win.Player.Time;
                v.SetSubtitlesAboveForTest(true);
                await WaitFor(() => v.CurrentMode == PlayerView.Mode.Playing, 8000);
                await Task.Delay(1200);
                Log($"rebuild: mode={v.CurrentMode} time {before} -> {win.Player.Time} (expect close to before)");
                v.ShowChrome();
                await Shot(win, "04b-subs-above-controls");
                v.SetSubtitlesAboveForTest(false);
                await WaitFor(() => v.CurrentMode == PlayerView.Mode.Playing, 8000);
                await Task.Delay(800);
                v.OpenTracksMenu();
                await Shot(win, "05-tracks");
                v.CloseMenus();
                v.OpenSpeedMenu();
                await Shot(win, "06-speed");
                v.CloseMenus();
                v.HideChrome(force: true);
                v.SetVolume(70, osd: true);
                await Shot(win, "07-hidden-volume");
                v.TogglePause();
                v.OpenContextMenuForTest(new Point(520, 200));
                await Shot(win, "08-paused-menu");
                v.CloseMenus();
                v.ShowDropForTest("Drop to play", "Replaces Night drive.mp4 — your place in it is saved. Subtitle files are added to the current video instead.");
                await Shot(win, "09-drop");
                v.HideDropForTest();
                v.TogglePause();

                await InputChecks(win, v);

                v.ToggleFullscreen();
                await Task.Delay(1000);
                v.ShowChrome();
                await Shot(win, "10-fullscreen");
                v.ToggleFullscreen();
                await Task.Delay(800);

                var len = win.Player.Length;
                v.SeekTo(len - 2500, osd: false);
                if (v.CurrentMode == PlayerView.Mode.Paused) v.TogglePause();
                await WaitFor(() => v.CurrentMode == PlayerView.Mode.Ended, 8000);
                Log($"end: mode={v.CurrentMode} playerTime={win.Player.Time} len={len}");
                await Task.Delay(400);
                await Shot(win, "11-end");

                // Resume: stop at ~80% (past the 30 s minimum), reopen, expect the toast.
                v.SeekTo((long)(len * 0.8), osd: false);
                await Task.Delay(800);
                v.OpenSource(media);
                await WaitFor(() => v.CurrentMode == PlayerView.Mode.Playing, 10_000);
                await Task.Delay(700);
                Log($"resume: time={win.Player.Time}");
                v.ShowChrome();
                await Shot(win, "12-resumed");
            }

            v.OpenSource(Path.Combine(_dir, "does-not-exist.mkv"));
            await Shot(win, "13-missing");
            if (media != null)
            {
                v.OpenSource(Path.Combine(_dir, "broken.mp4"));
                await WaitFor(() => v.CurrentMode == PlayerView.Mode.Error, 8000);
                await Shot(win, "14-broken");
                v.CloseMedia();
                await Shot(win, "15-recent");
            }
            Log("done");
        }
        catch (Exception ex)
        {
            Log("ERROR " + ex);
        }
        finally
        {
            _log?.Dispose();
            win.Close();
        }
    }

    static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
            await Task.Delay(100);
    }

    static async Task Shot(MainWindow win, string name)
    {
        await Task.Delay(600);
        var scale = win.RenderScaling;
        const int pad = 12;
        int x = win.Position.X - pad, y = win.Position.Y - pad;
        int w = (int)(win.ClientSize.Width * scale) + pad * 2, h = (int)(win.ClientSize.Height * scale) + pad * 2;
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
        Log($"shot {name} mode={win.View.CurrentMode} state={win.WindowState}");
    }

    // ---------------------------------------------------------------- real input

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    const uint LeftDown = 0x0002, LeftUp = 0x0004;

    static POINT Center(MainWindow win) => new()
    {
        X = win.Position.X + (int)(win.ClientSize.Width * win.RenderScaling / 2),
        Y = win.Position.Y + (int)(win.ClientSize.Height * win.RenderScaling * 0.45),
    };

    static bool OursAt(POINT p)
    {
        GetWindowThreadProcessId(WindowFromPoint(p), out var pid);
        return pid == Environment.ProcessId;
    }

    static Task Click(POINT p) => Task.Run(() =>
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(60);
        mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
    });

    static Task DoubleClick(POINT p) => Task.Run(() =>
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(60);
        for (var i = 0; i < 2; i++)
        {
            mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(60);
        }
    });

    static Task Drag(POINT p, int dx, int dy) => Task.Run(() =>
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(60);
        mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(120);
        for (var i = 1; i <= 20; i++)
        {
            SetCursorPos(p.X + dx * i / 20, p.Y + dy * i / 20);
            Thread.Sleep(10);
        }
        Thread.Sleep(60);
        mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
    });

    static Task Key(byte vk) => Task.Run(() =>
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(30);
        keybd_event(vk, 0, 2, IntPtr.Zero);
    });

    static async Task InputChecks(MainWindow win, PlayerView v)
    {
        GetCursorPos(out var saved);
        try
        {
            var c = Center(win);
            if (!OursAt(c))
            {
                Log("input checks skipped: another window covers the player");
                return;
            }

            var m0 = v.CurrentMode;
            await Click(c);
            await Task.Delay(600);
            Log($"click on video: {m0} -> {v.CurrentMode} (expect Paused)");
            await Click(c);
            await Task.Delay(600);
            Log($"second click: -> {v.CurrentMode} (expect Playing)");

            var p0 = win.Position;
            var m1 = v.CurrentMode;
            await Drag(c, 60, 40);
            await Task.Delay(600);
            Log($"drag: window moved by ({win.Position.X - p0.X},{win.Position.Y - p0.Y}) (expect ~60,40); mode {m1} -> {v.CurrentMode} (expect unchanged)");

            c = Center(win);
            var m2 = v.CurrentMode;
            await DoubleClick(c);
            await Task.Delay(1200);
            Log($"double-click: state={win.WindowState} (expect FullScreen); mode {m2} -> {v.CurrentMode} (expect unchanged)");
            await Key(0x1B); // Esc
            await Task.Delay(1000);
            Log($"Esc: state={win.WindowState} (expect Normal)");

            var t0 = win.Player.Time;
            var m3 = v.CurrentMode;
            await Key(0x20); // Space
            await Task.Delay(500);
            Log($"Space: {m3} -> {v.CurrentMode}");
            await Key(0x20);
            await Task.Delay(500);
            await Key(0x27); // Right
            await Task.Delay(600);
            Log($"Right arrow: time {t0} -> {win.Player.Time} (expect about +10 s)");
            Log($"chrome shown after keys: {v.ChromeShown}");
        }
        finally
        {
            SetCursorPos(saved.X, saved.Y);
        }
    }

    // ---------------------------------------------------------------- app icon

    /// <summary>Renders logo A at the Windows icon sizes and packs them as PNG entries in one .ico.</summary>
    static void MakeIcon(string path)
    {
        var head = (Avalonia.Media.Geometry)Avalonia.Application.Current!.Resources["LogoHead"]!;
        var play = (Avalonia.Media.Geometry)Avalonia.Application.Current!.Resources["LogoPlay"]!;
        var amber = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5A524"));
        var ground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0C0B0A"));
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(size, size), new Avalonia.Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushTransform(Avalonia.Matrix.CreateScale(size / 32.0, size / 32.0)))
            {
                ctx.DrawGeometry(amber, null, head);
                ctx.DrawGeometry(ground, null, play);
            }
            using var ms = new MemoryStream();
#pragma warning disable CS0618 // the replacement overload isn't worth it for a dev-only tool
            rtb.Save(ms);
#pragma warning restore CS0618
            images.Add(ms.ToArray());
        }

        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
        Log($"icon written: {path}");
    }

    /// <summary>The picture the installer shows while it works: logo and name on the app's dark ground.</summary>
    static void MakeSplash(string path)
    {
        var head = (Avalonia.Media.Geometry)Avalonia.Application.Current!.Resources["LogoHead"]!;
        var play = (Avalonia.Media.Geometry)Avalonia.Application.Current!.Resources["LogoPlay"]!;
        var ground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0C0B0A"));
        const int w = 520, h = 300;
        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(ground, new Avalonia.Rect(0, 0, w, h));
            using (ctx.PushTransform(Avalonia.Matrix.CreateScale(2.5, 2.5) * Avalonia.Matrix.CreateTranslation(w / 2.0 - 40, 70)))
            {
                ctx.DrawGeometry(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5A524")), null, head);
                ctx.DrawGeometry(ground, null, play);
            }
            var text = new Avalonia.Media.FormattedText("BearMinVLC", System.Globalization.CultureInfo.InvariantCulture,
                Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Parse("avares://BearMinVLC/Assets/Fonts#Geist"),
                    Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.SemiBold),
                26, new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#EDEBE7")));
            ctx.DrawText(text, new Avalonia.Point((w - text.Width) / 2, 170));
        }
        using var file = File.Create(path);
#pragma warning disable CS0618
        rtb.Save(file);
#pragma warning restore CS0618
        Log($"splash written: {path}");
    }

    // ---------------------------------------------------------------- test media

    static async Task<string> GenerateMedia()
    {
        var mp4 = Path.Combine(_dir, "Night drive.mp4");
        File.WriteAllText(Path.Combine(_dir, "broken.mp4"), "this is not a video");
        if (File.Exists(mp4) && new FileInfo(mp4).Length > 10_000)
            return mp4;

        var frame = Path.Combine(_dir, "frame.png");
        DrawFrame(frame);
        var a1 = Path.Combine(_dir, "tone-a.wav");
        var a2 = Path.Combine(_dir, "tone-b.wav");
        WriteTone(a1, 220, 50);
        WriteTone(a2, 330, 50);

        File.WriteAllText(Path.Combine(_dir, "Night drive.en.srt"),
            "1\n00:00:01,000 --> 00:00:20,000\nThis is an English subtitle.\n\n2\n00:00:20,500 --> 00:00:48,000\nStill going.\n");
        File.WriteAllText(Path.Combine(_dir, "Night drive.es.srt"),
            "1\n00:00:01,000 --> 00:00:20,000\nEsto es un subtítulo en español.\n\n2\n00:00:20,500 --> 00:00:48,000\nSigue.\n");

        await Task.Run(async () =>
        {
            Core.Initialize();
            // Not disposed on purpose: disposing a LibVLC right after transcoding can deadlock.
            var lib = new LibVLC("--no-video-title-show", "--quiet");
            await GenerateWith(lib, frame, a1, a2, mp4);
            var probe = new Media(lib, mp4, FromType.FromPath);
            await probe.Parse(MediaParseOptions.ParseLocal);
            foreach (var t in probe.Tracks)
                Log($"probe track {t.Id}: {t.TrackType} codec={t.Codec} lang={t.Language}");
        });
        Log($"generated {mp4}: {(File.Exists(mp4) ? new FileInfo(mp4).Length : 0)} bytes");
        return mp4;
    }

    static Task GenerateWith(LibVLC lib, string frame, string a1, string a2, string mp4)
    {
        return Task.Run(() =>
        {
            using var media = new Media(lib, frame, FromType.FromPath);
            media.AddOption(":image-duration=50");
            media.AddOption(":image-fps=25");
            media.AddOption(":input-slave=" + new Uri(a1).AbsoluteUri + "#" + new Uri(a2).AbsoluteUri);
            // The LGPL libVLC package has no x264, so the test clip uses MPEG-4 Part 2 video.
            media.AddOption(":sout=#transcode{vcodec=mp4v,vb=1500,fps=25,acodec=mp4a,ab=96,channels=2,samplerate=44100}:std{access=file,mux=mp4,dst='" + mp4 + "'}");
            media.AddOption(":sout-all");
            media.AddOption(":no-sout-display");
            using var mp = new MediaPlayer(media);
            var done = new ManualResetEventSlim();
            mp.EndReached += (_, _) => done.Set();
            mp.EncounteredError += (_, _) => done.Set();
            mp.Play();
            done.Wait(TimeSpan.FromSeconds(120));
            mp.Stop(); // safe here: this is a worker thread, not a libVLC callback
        });
    }

    static void DrawFrame(string path)
    {
        using var bmp = new Bitmap(1280, 720);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), Color.FromArgb(14, 19, 29), Color.FromArgb(36, 28, 23), 90f))
            g.FillRectangle(bg, 0, 0, 1280, 720);
        (int x, int y, int r, Color c)[] lights =
        [
            (160, 420, 60, Color.FromArgb(110, 245, 165, 36)), (300, 460, 26, Color.FromArgb(150, 255, 214, 150)),
            (500, 400, 80, Color.FromArgb(70, 255, 90, 60)), (710, 450, 20, Color.FromArgb(160, 255, 240, 220)),
            (820, 380, 45, Color.FromArgb(90, 245, 165, 36)), (1010, 430, 70, Color.FromArgb(60, 120, 170, 255)),
            (1120, 410, 16, Color.FromArgb(150, 255, 230, 200)),
        ];
        foreach (var (x, y, r, c) in lights)
            for (var k = 4; k >= 1; k--)
            {
                using var b = new SolidBrush(Color.FromArgb(c.A / (k + 1), c));
                var rr = r + k * 10;
                g.FillEllipse(b, x - rr, y - rr, rr * 2, rr * 2);
            }
        bmp.Save(path, ImageFormat.Png);
    }

    static void WriteTone(string path, double hz, int seconds)
    {
        const int rate = 44100;
        var samples = rate * seconds;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 3000));
    }
}
#endif
