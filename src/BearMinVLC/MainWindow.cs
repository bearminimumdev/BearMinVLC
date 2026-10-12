using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using BearMinVLC.Services;
using BearMinVLC.Views;
using LibVLCSharp.Avalonia;

namespace BearMinVLC;

/// <summary>
/// The player window. It only hosts libVLC's video surface; every control is drawn by
/// <see cref="PlayerView"/> in the transparent overlay window LibVLCSharp keeps on top of it.
/// </summary>
public sealed class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly Library _library = Library.Load();
    readonly Updater _updater = new();
    readonly string[] _args;

    public Player Player { get; }
    public PlayerView View { get; }
    public bool ShuttingDown { get; private set; }

    public MainWindow(string[] args)
    {
        _args = args;
        Title = "BearMinVLC";
        Background = new SolidColorBrush(Color.Parse("#0C0B0A"));
        MinWidth = 480;
        MinHeight = 320;
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.BorderOnly;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BearMinVLC/Assets/BearMinVLC.ico"))); }
        catch (Exception) { }
        RestorePlacement();

        Player = new Player(_settings.EngineArgs(Screens.Primary?.Scaling ?? 1), _settings.HardwareDecoding);
        View = new PlayerView(this, Player, _library, _settings);
        var video = new VideoView { MediaPlayer = Player.Mp, Content = View };
        Player.EngineChanged += () => video.MediaPlayer = Player.Mp; // Settings rebuilt the engine
        Content = video;

        // Keys can land on this window (after Alt+Tab) or on the overlay (after a click); both go to the view.
        AddHandler(KeyDownEvent, (_, e) => View.HandleKey(e), RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, e) => View.OnDragEnter(e));
        AddHandler(DragDrop.DragOverEvent, (_, e) => View.OnDragOver(e));
        AddHandler(DragDrop.DragLeaveEvent, (_, e) => View.OnDragLeave(e));
        AddHandler(DragDrop.DropEvent, (_, e) => View.OnDrop(e));

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => Player.Dispose();
    }

    void OnOpened(object? sender, EventArgs e)
    {
        var shots = Array.IndexOf(_args, "--shots");
        if (shots >= 0)
        {
#if DEBUG
            _ = DevShots.Run(this, _args.Skip(shots + 1).ToArray());
#endif
            return;
        }

        SingleInstance.Listen(path =>
        {
            if (path.Length > 0) View.OpenSource(path);
            BringToFront();
        });
        var file = _args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (file != null) View.OpenSource(file);

        _ = CheckForUpdatesAsync();
    }

    async Task CheckForUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5)); // let startup and the first video settle
        var version = await _updater.CheckAndDownloadAsync();
        if (version == null) return;
        View.ShowUpdateReady(version, () =>
        {
            ShuttingDown = true;
            View.OnClosing();
            SavePlacement();
            _updater.RestartNow();
        });
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var wasTopmost = Topmost;
        Topmost = true;   // Windows won't let a background app steal focus; this nudges it forward
        Topmost = wasTopmost;
        Activate();
    }

    void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (_settings.X is int x && _settings.Y is int y)
        {
            // Only reuse the saved spot if it's still on a connected monitor.
            var rect = new PixelRect(x, y, (int)Width, (int)Height);
            if (Screens.All.Any(s => s.WorkingArea.Intersect(rect).Width >= 120 && s.WorkingArea.Intersect(rect).Height >= 80))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(x, y);
            }
        }
        if (_settings.Maximized)
            WindowState = WindowState.Maximized;
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        ShuttingDown = true;
        View.OnClosing();
        SavePlacement();
        _updater.ApplyOnExit(); // a downloaded update installs quietly once we've exited
    }

    void SavePlacement()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.X = Position.X;
            _settings.Y = Position.Y;
            _settings.Width = Width;
            _settings.Height = Height;
        }
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }
}
