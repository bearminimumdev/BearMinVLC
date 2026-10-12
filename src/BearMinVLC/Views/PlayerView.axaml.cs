using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BearMinVLC.Services;
using Path = System.IO.Path;
using PathShape = Avalonia.Controls.Shapes.Path;

namespace BearMinVLC.Views;

public partial class PlayerView : UserControl
{
    internal enum Mode { Start, Loading, Playing, Paused, Ended, Error }

    static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi", ".sbv", ".ttml"];
    static readonly float[] Speeds = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];
    static readonly (string Label, string? Value)[] AspectRatios =
        [("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("1:1", "1:1")];
    static readonly string[] MediaPatterns =
    [
        "*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.wmv", "*.flv", "*.m4v", "*.mpg", "*.mpeg", "*.ts", "*.m2ts",
        "*.mts", "*.3gp", "*.ogv", "*.vob", "*.divx", "*.rmvb", "*.asf", "*.mp3", "*.m4a", "*.aac", "*.flac", "*.wav",
        "*.ogg", "*.opus", "*.wma", "*.mka", "*.m3u", "*.m3u8",
    ];

    readonly MainWindow _win;
    readonly Player _player;
    readonly Library _library;
    readonly AppSettings _settings;

    Mode _mode = Mode.Start;
    RecentItem? _current;
    long _length;
    long _time;
    bool _showRemaining;
    bool _chromeShown = true;
    bool _audioOnly;
    bool _pausedNearEnd;   // we paused on the last frame ourselves
    bool _hardEnded;       // libVLC reached the end and closed the video
    bool _keyboardNav;     // Tab was used, so focus in the bar should keep it visible
    bool _sawProgress;     // the current file actually played (length or time reported)
    bool _currentIsNew;    // the current file wasn't in Recent before this open
    bool _pauseAfterReload; // a settings change restarted a paused video
    bool _fillingSettings;  // populating the panel; ignore change events
    string? _updateReady;   // version downloaded and waiting to install
    bool _tapPending;
    bool _tapDragging;
    PixelPoint _tapStartCursor;
    PixelPoint _tapStartWindow;
    Point _lastPointer;
    Window? _overlay;
    WindowState _stateBeforeFullscreen = WindowState.Normal;
    Action? _toastAction;
    Action? _errorPrimary;
    Action? _errorSecondary;
    bool _contextShowsAspect;
    string? _lastError;

    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _spinnerTimer = new() { Interval = TimeSpan.FromSeconds(0.4) };
    readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromSeconds(0.35) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public PlayerView(MainWindow win, Player player, Library library, AppSettings settings)
    {
        _win = win;
        _player = player;
        _library = library;
        _settings = settings;
        InitializeComponent();
        Focusable = true; // so closing a dialog can pull focus out of the (now hidden) URL box
        WireUi();
        WirePlayer();
        BuildShortcutSheet();
        ShortcutHint.IsVisible = !_settings.SeenShortcutHint;
        VolumeSlider.Value = _settings.Volume;
        UpdateMuteIcon();
        SetMode(Mode.Start);
    }

    /// <summary>Debug tracing hook for the screenshot harness.</summary>
    internal static Action<string>? Trace = null;

    internal Mode CurrentMode => _mode;
    internal bool ChromeShown => _chromeShown;

    static T Res<T>(string key) where T : class => (T)Application.Current!.Resources[key]!;
    static Geometry Icon(string key) => Res<Geometry>(key);

    // ------------------------------------------------------------------ wiring

    void WireUi()
    {
        Root.AddHandler(PointerMovedEvent, OnAnyPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.AddHandler(PointerPressedEvent, (_, _) => _keyboardNav = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.PointerExited += (_, _) => { if (_mode == Mode.Playing) _leaveTimer.Start(); };
        Root.SizeChanged += (_, _) => UpdateHiddenProgress();

        Tap.PointerPressed += OnTapPressed;
        Tap.PointerMoved += OnTapMoved;
        Tap.PointerReleased += OnTapReleased;
        Tap.PointerCaptureLost += (_, _) =>
        {
            Trace?.Invoke($"tap capture lost pending={_tapPending} dragging={_tapDragging}");
            _tapPending = false;
            _tapDragging = false;
        };
        Tap.PointerWheelChanged += (_, e) =>
        {
            if (_mode is Mode.Playing or Mode.Paused or Mode.Ended)
                SetVolume(_settings.Volume + (e.Delta.Y > 0 ? 5 : -5), osd: !_chromeShown);
            e.Handled = true;
        };

        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideChrome(); };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); HideChrome(); };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); Osd.Opacity = 0; };
        _toastTimer.Tick += (_, _) => HideToast();
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerTimer.Stop();
            if (_mode is Mode.Loading or Mode.Playing) Spinner.IsVisible = true;
        };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); CenterFlash.Opacity = 0; };
        _saveTimer.Tick += (_, _) => SaveProgress(final: false);

        // Control bar
        PlayPauseButton.Click += (_, _) => TogglePause();
        BackButton.Click += (_, _) => SeekBy(-SkipMs, osd: false);
        ForwardButton.Click += (_, _) => SeekBy(SkipMs, osd: false);
        MuteButton.Click += (_, _) => ToggleMute(osd: false);
        VolumeSlider.ValueChanged += v => SetVolume(v, osd: false);
        TimeButton.Click += (_, _) => { _showRemaining = !_showRemaining; UpdateTime(); };
        TracksButton.Click += (_, _) => ToggleMenu(TracksMenu);
        SpeedButton.Click += (_, _) => ToggleMenu(SpeedMenu);
        FullscreenButton.Click += (_, _) => ToggleFullscreen();
        Seek.SeekRequested += (ms, _) => SeekTo(ms, osd: false);
        Seek.SeekByRequested += d => SeekBy(d, osd: false);
        BottomBar.GotFocus += (_, _) => { if (_keyboardNav) ShowChrome(); };

        // Title row
        DragArea.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(DragArea).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) ToggleMaximize();
            else if (_win.WindowState != WindowState.FullScreen) _win.BeginMoveDrag(e);
            e.Handled = true;
        };
        MinButton.Click += (_, _) => _win.WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => _win.Close();
        ResizeTop.PointerPressed += (_, e) => _win.BeginResizeDrag(WindowEdge.North, e);
        ResizeTopLeft.PointerPressed += (_, e) => _win.BeginResizeDrag(WindowEdge.NorthWest, e);
        ResizeTopRight.PointerPressed += (_, e) => _win.BeginResizeDrag(WindowEdge.NorthEast, e);
        _win.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) UpdateWindowButtons();
            if (e.Property == Window.TopmostProperty && _overlay != null) _overlay.Topmost = _win.Topmost;
        };

        // Start screen
        OpenFileButton.Click += (_, _) => OpenFileDialog();
        OpenUrlButton.Click += (_, _) => ShowUrlDialog();
        ClearRecentButton.Click += (_, _) => ClearRecent();

        // End & error
        ReplayButton.Click += (_, _) => Replay();
        EndOpenButton.Click += (_, _) => OpenFileDialog();
        ErrorPrimary.Click += (_, _) => _errorPrimary?.Invoke();
        ErrorSecondary.Click += (_, _) => _errorSecondary?.Invoke();
        ErrorDetailsButton.Click += (_, _) => ErrorDetails.IsVisible = !ErrorDetails.IsVisible;

        // Toast
        ToastAction.Click += (_, _) => { var a = _toastAction; HideToast(); a?.Invoke(); };

        // Menus & dialogs: click outside closes
        MenuLayer.PointerPressed += (_, e) => { if (e.Source == MenuLayer) { CloseMenus(); e.Handled = true; } };
        DialogLayer.PointerPressed += (_, e) => { if (e.Source == DialogLayer) { CloseDialogs(); e.Handled = true; } };
        UrlCancel.Click += (_, _) => CloseDialogs();
        UrlOpen.Click += (_, _) => SubmitUrl();
        UrlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SubmitUrl(); e.Handled = true; } };
        KeysClose.Click += (_, _) => CloseDialogs();

        // Settings
        StartSettingsButton.Click += (_, _) => ShowSettings();
        SettingsClose.Click += (_, _) => CloseDialogs();
        SettingsLayer.PointerPressed += (_, e) => { if (e.Source == SettingsLayer) { CloseDialogs(); e.Handled = true; } };
        SettingsLayer.SizeChanged += (_, e) => LayoutSettingsColumns(e.NewSize.Width);
        WireSettings();

        // Drag and drop onto the overlay
        DragDrop.SetAllowDrop(Root, true);
        Root.AddHandler(DragDrop.DragEnterEvent, (_, e) => OnDragEnter(e));
        Root.AddHandler(DragDrop.DragOverEvent, (_, e) => OnDragOver(e));
        Root.AddHandler(DragDrop.DragLeaveEvent, (_, e) => OnDragLeave(e));
        Root.AddHandler(DragDrop.DropEvent, (_, e) => OnDrop(e));
    }

    void WirePlayer()
    {
        _player.Opening += () => { if (_mode is not Mode.Error) StartSpinner(); };
        _player.Playing += OnPlaying;
        _player.Paused += () => { if (_mode is Mode.Playing or Mode.Loading) SetMode(Mode.Paused); };
        _player.EndReached += OnEndReached;
        _player.Error += OnPlayerError;
        _player.TimeChanged += OnTime;
        _player.LengthChanged += l =>
        {
            if (l > 0) _sawProgress = true;
            _length = l;
            Seek.Duration = l;
            UpdateTime();
        };
        _player.Buffering += cache =>
        {
            if (cache >= 100) HideSpinner();
            else if (_mode is Mode.Loading or Mode.Playing) StartSpinner();
        };
        _player.TracksChanged += () =>
        {
            if (TracksMenu.IsVisible) BuildTracksMenu();
        };
    }

    /// <summary>Hooks the overlay window LibVLCSharp creates to host this view.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not Window w || w == _win || w == _overlay)
            return;

        _overlay = w;
        _overlay.Title = "BearMinVLC controls"; // what screen readers announce for the overlay
        _overlay.AddHandler(KeyDownEvent, (_, ke) => HandleKey(ke), RoutingStrategies.Tunnel);
        _overlay.Topmost = _win.Topmost;
        // Alt+F4 while the overlay has focus should close the player, not just the overlay.
        // (When the player itself closes, Avalonia closes the owned overlay first — let that through.)
        _overlay.Closing += (_, ce) =>
        {
            if (_win.ShuttingDown || ce.CloseReason == WindowCloseReason.OwnerWindowClosing) return;
            ce.Cancel = true;
            Dispatcher.UIThread.Post(() => _win.Close());
        };
        // The overlay is a separate window with square corners; round it like the player window.
        Native.SetCorners(_overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, Native.CornerRound);
    }

    // ------------------------------------------------------------------ opening media

    public void OpenSource(string source)
    {
        source = source.Trim().Trim('"');
        if (source.Length == 0) return;

        SaveProgress(final: true);
        CloseMenus();
        CloseDialogs();
        HideToast();

        if (!Player.IsUrl(source) && !File.Exists(source))
        {
            ShowMissing(source, _library.Find(source));
            return;
        }

        _currentIsNew = _library.Find(source) == null;
        _current = _library.Touch(source);
        _length = _current.LengthMs;
        _time = 0;
        _hardEnded = false;
        _pausedNearEnd = false;
        _audioOnly = false;
        _sawProgress = false;
        _lastError = null;
        Seek.Duration = _length;
        Seek.Position = 0;
        SetSpeed(1f, osd: false);

        var start = _settings.Resume && _current.CanResume ? _current.PositionMs : 0;
        SetMode(Mode.Loading);
        StartSpinner();
        _player.Open(source, start, OpenOpts());
        if (start > 0)
            ShowToast($"Resumed at {Fmt.Time(start)}", "Start over", () => SeekTo(0, osd: false));
        _saveTimer.Start();
    }

    internal async void OpenFileDialog()
    {
        CloseMenus();
        var files = await _win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video and audio") { Patterns = MediaPatterns }, FilePickerFileTypes.All],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenSource(path);
    }

    async void AddSubtitleFileDialog()
    {
        CloseMenus();
        var files = await _win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add subtitle file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles") { Patterns = SubtitleExtensions.Select(x => "*" + x).ToArray() },
                FilePickerFileTypes.All,
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) AddSubtitle(path);
    }

    void AddSubtitle(string path)
    {
        _player.AddSubtitleFile(path);
        ShowOsd("IcSubtitles", $"Added {Path.GetFileName(path)}");
    }

    async void LocateMissing(RecentItem item)
    {
        var files = await _win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Locate {item.Title}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Video and audio") { Patterns = MediaPatterns }, FilePickerFileTypes.All],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        item.Source = path;
        item.Title = Path.GetFileName(path);
        _library.Save();
        OpenSource(path);
    }

    internal void CloseMedia()
    {
        SaveProgress(final: true);
        CloseMenus();
        _saveTimer.Stop();
        _player.Stop();
        _current = null;
        _audioOnly = false;
        SetMode(Mode.Start);
    }

    // ------------------------------------------------------------------ player events

    void OnPlaying()
    {
        HideSpinner();
        if (_mode is Mode.Error or Mode.Start) return;
        if (_mode is not Mode.Ended) SetMode(Mode.Playing);
        ApplyVolume(); // libVLC only accepts a volume once the audio output exists
        if (_settings.AudioDevice.Length > 0) _player.SetAudioDevice(_settings.AudioDevice);
        if (_pauseAfterReload)
        {
            _pauseAfterReload = false;
            _player.Pause();
            SetMode(Mode.Paused);
        }
        DispatcherTimer.RunOnce(AfterStart, TimeSpan.FromMilliseconds(500));
    }

    void AfterStart()
    {
        if (_current == null || _mode is not (Mode.Playing or Mode.Paused)) return;

        var audioOnly = _player.VideoTrackCount == 0;
        if (audioOnly != _audioOnly)
        {
            _audioOnly = audioOnly;
            AudioTitle.Text = _current.Title;
            AudioOnlyLayer.IsVisible = audioOnly;
            UpdateChromePolicy();
        }

        // Put back the subtitle and audio tracks chosen last time.
        if (_current.SubtitleId is int s && (s == -1 || _player.SubtitleTracks().Any(t => t.Id == s)) && _player.CurrentSubtitle != s)
            _player.SetSubtitle(s);
        if (_current.AudioId is int a && _player.AudioTracks().Any(t => t.Id == a) && _player.CurrentAudio != a)
            _player.SetAudio(a);
    }

    void OnTime(long t)
    {
        if (_mode is Mode.Start or Mode.Error) return;
        if (t > 0) _sawProgress = true;
        _time = t;
        Seek.Position = t;
        UpdateTime();
        UpdateHiddenProgress();
        if (Spinner.IsVisible && _mode == Mode.Playing) HideSpinner();

        // Stop on the last frame instead of letting libVLC tear the picture down.
        if (_mode == Mode.Playing && !_pausedNearEnd && _length > 0 && t >= _length - 350 && _player.IsSeekable)
        {
            _pausedNearEnd = true;
            _player.Pause();
            ShowEnded();
        }
    }

    void OnEndReached()
    {
        if (_mode is Mode.Start or Mode.Error) return;
        if (!_sawProgress)
        {
            // libVLC "ends" files it can't read at all instead of reporting an error.
            OnPlayerError();
            return;
        }
        _hardEnded = true;
        if (_mode != Mode.Ended) ShowEnded();
    }

    void ShowEnded()
    {
        _time = _length;
        Seek.Position = _length;
        UpdateTime();
        EndTitle.Text = $"Finished · {_current?.Title}";
        SetMode(Mode.Ended);
        if (_current != null) _library.Record(_current, _length, _length);
    }

    void Replay()
    {
        if (_current == null) return;
        _pausedNearEnd = false;
        if (_hardEnded)
        {
            _hardEnded = false;
            SetMode(Mode.Loading);
            _player.Open(_current.Source, 0, OpenOpts());
            return;
        }
        _player.Time = 0;
        _player.Resume();
        SetMode(Mode.Playing);
    }

    void OnPlayerError()
    {
        if (_current == null) return;
        _lastError = TryGetLastError(_player);
        HideSpinner();
        _saveTimer.Stop();
        var item = _current;
        // A file that never played doesn't belong in Recent (unless it was there already).
        if (_currentIsNew && !_sawProgress && !item.IsUrl)
            _library.Remove(item);
        if (item.IsUrl)
            ShowError("Couldn't open this stream",
                "The address didn't respond, or isn't a stream the playback engine understands.",
                "Retry", () => OpenSource(item.Source),
                "Open another", OpenFileDialog);
        else
            ShowError("Can't play this file",
                "The file may be damaged or incomplete, or uses a format the engine can't decode. Other files still play normally.",
                "Open another file", OpenFileDialog,
                "Show in Explorer", () => ShowInExplorer(item.Source));
    }

    static string? TryGetLastError(Player player)
    {
        try { return player.Lib.LastLibVLCError; }
        catch (Exception) { return null; }
    }

    void ShowMissing(string source, RecentItem? item)
    {
        _current = null;
        _lastError = null;
        if (item != null)
            ShowError("File not found", $"“{Path.GetFileName(source)}” was moved or deleted.",
                "Locate…", () => LocateMissing(item),
                "Remove from Recent", () => { _library.Remove(item); SetMode(Mode.Start); });
        else
            ShowError("File not found", $"“{Path.GetFileName(source)}” was moved or deleted.",
                "Open another file", OpenFileDialog,
                "Back", () => SetMode(Mode.Start));
        TitleText.Text = Path.GetFileName(source);
    }

    void ShowError(string title, string body, string primary, Action primaryAction, string secondary, Action secondaryAction)
    {
        ErrorTitle.Text = title;
        ErrorBody.Text = body;
        ErrorPrimary.Content = primary;
        ErrorSecondary.Content = secondary;
        _errorPrimary = primaryAction;
        _errorSecondary = secondaryAction;
        ErrorDetails.Text = string.IsNullOrWhiteSpace(_lastError) ? "The playback engine gave no further detail." : _lastError;
        ErrorDetails.IsVisible = false;
        ErrorDetailsButton.IsVisible = _lastError != null;
        _player.Stop();
        SetMode(Mode.Error);
    }

    // ------------------------------------------------------------------ modes & chrome

    void SetMode(Mode m)
    {
        _mode = m;
        var onVideo = m is Mode.Loading or Mode.Playing or Mode.Paused or Mode.Ended;

        StartLayer.IsVisible = m == Mode.Start;
        ErrorLayer.IsVisible = m == Mode.Error;
        EndLayer.IsVisible = m == Mode.Ended;
        Chrome.IsVisible = onVideo;
        AudioOnlyLayer.IsVisible = onVideo && _audioOnly && m != Mode.Ended;
        HiddenProgress.IsVisible = onVideo;

        TitleRow.Classes.Set("onvideo", onVideo);
        var named = onVideo || m == Mode.Error;
        TitleText.Text = named && _current != null ? _current.Title : "BearMinVLC";
        TitleText.FontSize = named ? 14 : 12;
        TitleText.Foreground = Res<IBrush>(named ? "Text" : "Muted");

        var playing = m == Mode.Playing;
        PlayPauseIcon.Data = Icon(m == Mode.Ended ? "IcBack" : playing || m == Mode.Loading ? "IcPause" : "IcPlay");
        PlayPauseIcon.Classes.Set("fill", m != Mode.Ended);
        AutomationProperties.SetName(PlayPauseButton, m == Mode.Ended ? "Replay" : playing ? "Pause" : "Play");

        Native.KeepAwake(playing);
        if (!onVideo) HideSpinner();
        if (m == Mode.Start) RefreshRecent();
        UpdateWindowButtons();
        UpdateChromePolicy();
    }

    /// <summary>Controls stay up unless a video is actually playing.</summary>
    void UpdateChromePolicy()
    {
        ShowChrome();
        if (_mode != Mode.Playing || _audioOnly) _hideTimer.Stop();
    }

    bool CanHideChrome =>
        _mode == Mode.Playing && !_audioOnly && !MenuLayer.IsVisible && !DialogLayer.IsVisible && !SettingsLayer.IsVisible &&
        !BottomBar.IsPointerOver && !TitleRow.IsPointerOver && !Seek.IsDragging &&
        !(_keyboardNav && BottomBar.IsKeyboardFocusWithin);

    internal void ShowChrome()
    {
        if (!_chromeShown)
        {
            _chromeShown = true;
            Chrome.Opacity = 1;
            Chrome.IsHitTestVisible = true;
            TitleRow.Opacity = 1;
            TitleRow.IsHitTestVisible = true;
            HiddenProgress.Opacity = 0;
            Root.Cursor = Cursor.Default;
        }
        _hideTimer.Stop();
        if (_mode == Mode.Playing && !_audioOnly) _hideTimer.Start();
    }

    internal void HideChrome(bool force = false)
    {
        if (!force && !CanHideChrome) return;
        _chromeShown = false;
        Chrome.Opacity = 0;
        Chrome.IsHitTestVisible = false;
        TitleRow.Opacity = 0;
        TitleRow.IsHitTestVisible = false;
        HiddenProgress.Opacity = _length > 0 ? 1 : 0;
        Root.Cursor = new Cursor(StandardCursorType.None);
    }

    void OnAnyPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(Root);
        if (Math.Abs(p.X - _lastPointer.X) < 2 && Math.Abs(p.Y - _lastPointer.Y) < 2) return;
        _lastPointer = p;
        _leaveTimer.Stop();
        if (_mode is Mode.Start or Mode.Error) return;
        ShowChrome();
    }

    void UpdateTime()
    {
        if (_length <= 0)
        {
            TimeNow.Text = Fmt.Time(_time);
            TimeTotal.Text = _current?.IsUrl == true ? "  ·  Live" : "";
            return;
        }
        TimeNow.Text = _showRemaining ? "-" + Fmt.Time(_length - _time) : Fmt.Time(_time);
        TimeTotal.Text = " / " + Fmt.Time(_length);
        AutomationProperties.SetName(TimeButton, _showRemaining
            ? $"{Fmt.Time(_length - _time)} remaining. Click to show elapsed time"
            : $"{Fmt.Time(_time)} of {Fmt.Time(_length)}. Click to show time remaining");
    }

    void UpdateHiddenProgress()
    {
        var w = HiddenProgressTrack.Bounds.Width;
        HiddenProgressFill.Width = _length > 0 ? w * Math.Clamp((double)_time / _length, 0, 1) : 0;
    }

    void UpdateWindowButtons()
    {
        var state = _win.WindowState;
        var full = state == WindowState.FullScreen;
        MinButton.IsVisible = MaxButton.IsVisible = CloseButton.IsVisible = !full;
        MaxIcon.Data = Icon(state == WindowState.Maximized ? "CapRestore" : "CapMax");
        AutomationProperties.SetName(MaxButton, state == WindowState.Maximized ? "Restore" : "Maximize");
        FullscreenIcon.Data = Icon(full ? "IcExitFullscreen" : "IcFullscreen");
        AutomationProperties.SetName(FullscreenButton, full ? "Exit fullscreen" : "Enter fullscreen");
        ResizeTop.IsVisible = ResizeTopLeft.IsVisible = ResizeTopRight.IsVisible = state == WindowState.Normal;
        Native.SetCorners(_overlay?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
            state == WindowState.Normal ? Native.CornerRound : Native.CornerSquare);
    }

    // ------------------------------------------------------------------ actions

    internal void TogglePause(bool flash = false)
    {
        Trace?.Invoke($"TogglePause from {_mode} flash={flash}");
        switch (_mode)
        {
            case Mode.Playing:
                _player.Pause();
                SetMode(Mode.Paused);
                SaveProgress(final: false);
                break;
            case Mode.Paused:
                _player.Resume();
                SetMode(Mode.Playing);
                break;
            case Mode.Ended:
                Replay();
                return;
            default:
                return;
        }
        if (flash) Flash(_mode == Mode.Paused ? "IcPause" : "IcPlay");
    }

    void Flash(string iconKey)
    {
        CenterFlashIcon.Data = Icon(iconKey);
        CenterFlash.Opacity = 1;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    void OnTapPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Tap).Properties;
        Trace?.Invoke($"tap pressed clicks={e.ClickCount} left={props.IsLeftButtonPressed}");
        if (_mode is not (Mode.Loading or Mode.Playing or Mode.Paused or Mode.Ended)) return;

        if (props.IsRightButtonPressed)
        {
            OpenVideoContextMenu(e.GetPosition(Root));
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            _tapPending = false;
            if (_mode is Mode.Playing or Mode.Paused) TogglePause(); // undo the first click's pause
            ToggleFullscreen();
            return;
        }

        // Decide on release: no movement = click (pause), movement = drag the window.
        _tapPending = true;
        _tapDragging = false;
        _tapStartCursor = Native.CursorPosition();
        _tapStartWindow = _win.Position;
        e.Pointer.Capture(Tap);
    }

    void OnTapMoved(object? sender, PointerEventArgs e)
    {
        if (!_tapPending || _win.WindowState != WindowState.Normal) return;
        var now = Native.CursorPosition();
        var dx = now.X - _tapStartCursor.X;
        var dy = now.Y - _tapStartCursor.Y;
        if (!_tapDragging && Math.Abs(dx) + Math.Abs(dy) < 5) return;
        _tapDragging = true;
        _win.Position = new PixelPoint(_tapStartWindow.X + dx, _tapStartWindow.Y + dy);
    }

    void OnTapReleased(object? sender, PointerReleasedEventArgs e)
    {
        Trace?.Invoke($"tap released pending={_tapPending} dragging={_tapDragging}");
        if (!_tapPending) return;
        var wasDrag = _tapDragging; // read first: releasing capture resets the flags
        _tapPending = false;
        _tapDragging = false;
        e.Pointer.Capture(null);
        if (!wasDrag && _mode != Mode.Ended) TogglePause(flash: true);
    }

    internal void SeekTo(long ms, bool osd)
    {
        if (_current == null || _length <= 0) return;
        ms = Math.Clamp(ms, 0, Math.Max(0, _length - 500));

        if (_hardEnded)
        {
            _hardEnded = false;
            _pausedNearEnd = false;
            SetMode(Mode.Loading);
            _player.Open(_current.Source, ms, OpenOpts());
        }
        else
        {
            if (!_player.IsSeekable) return;
            _player.Time = ms;
            if (_mode == Mode.Ended)
            {
                _pausedNearEnd = false;
                SetMode(Mode.Paused);
            }
        }
        _time = ms;
        Seek.Position = ms;
        UpdateTime();
        UpdateHiddenProgress();
        if (osd && !_chromeShown)
            ShowOsd(ms >= _time ? "IcFwd" : "IcBack", $"{Fmt.Time(ms)} / {Fmt.Time(_length)}", (double)ms / _length);
    }

    void SeekBy(long deltaMs, bool osd)
    {
        if (_mode is not (Mode.Playing or Mode.Paused or Mode.Ended)) return;
        var target = _time + deltaMs;
        SeekTo(target, osd: false);
        if (osd && !_chromeShown && _length > 0)
            ShowOsd(deltaMs >= 0 ? "IcFwd" : "IcBack", $"{Fmt.Time(Math.Clamp(target, 0, _length))} / {Fmt.Time(_length)}",
                Math.Clamp((double)target / _length, 0, 1));
    }

    void ApplyVolume()
    {
        _player.Mp.Volume = _settings.Volume;
        _player.Mp.Mute = _settings.Muted;
    }

    internal void SetVolume(int v, bool osd)
    {
        v = Math.Clamp(v, 0, 100);
        _settings.Volume = v;
        if (v > 0) _settings.Muted = false;
        ApplyVolume();
        VolumeSlider.Value = v;
        UpdateMuteIcon();
        if (osd) ShowVolumeOsd();
    }

    void ToggleMute(bool osd)
    {
        _settings.Muted = !_settings.Muted;
        if (!_settings.Muted && _settings.Volume == 0) _settings.Volume = 30;
        ApplyVolume();
        VolumeSlider.Value = _settings.Volume;
        UpdateMuteIcon();
        if (osd && !_chromeShown) ShowVolumeOsd();
    }

    void ShowVolumeOsd()
    {
        if (_settings.Muted) ShowOsd("IcMuted", "Muted");
        else ShowOsd(VolumeIconKey(), $"Volume {_settings.Volume}%", _settings.Volume / 100.0);
    }

    string VolumeIconKey() => _settings.Muted || _settings.Volume == 0 ? "IcMuted" : _settings.Volume < 50 ? "IcVolumeLow" : "IcVolume";

    void UpdateMuteIcon()
    {
        MuteIcon.Data = Icon(VolumeIconKey());
        AutomationProperties.SetName(MuteButton, _settings.Muted ? "Unmute" : "Mute");
    }

    internal void ToggleFullscreen()
    {
        if (_win.WindowState == WindowState.FullScreen)
            _win.WindowState = _stateBeforeFullscreen == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        else
        {
            _stateBeforeFullscreen = _win.WindowState;
            _win.WindowState = WindowState.FullScreen;
        }
    }

    void ToggleMaximize() =>
        _win.WindowState = _win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void SetSpeed(float rate, bool osd)
    {
        _player.SetRate(rate);
        SpeedText.Text = Fmt.Speed(rate);
        SpeedButton.Classes.Set("tinted", Math.Abs(rate - 1f) > 0.001f);
        AutomationProperties.SetName(SpeedButton, Math.Abs(rate - 1f) < 0.001f ? "Playback speed, normal" : $"Playback speed, {Fmt.Speed(rate)}");
        if (SpeedMenu.IsVisible) BuildSpeedMenu();
        if (osd) ShowOsd("IcFwd", $"Speed {Fmt.Speed(rate)}");
    }

    void StepSpeed(int dir)
    {
        var rate = Math.Clamp(MathF.Round((_player.Rate + dir * 0.25f) * 4) / 4, 0.25f, 4f);
        SetSpeed(rate, osd: true);
    }

    void CycleSubtitle()
    {
        var ids = new List<int> { -1 };
        ids.AddRange(_player.SubtitleTracks().Select(t => t.Id));
        var next = ids[(ids.IndexOf(_player.CurrentSubtitle) + 1) % ids.Count];
        SetSubtitle(next, osd: true);
    }

    void SetSubtitle(int id, bool osd)
    {
        _player.SetSubtitle(id);
        if (_current != null) _current.SubtitleId = id;
        if (TracksMenu.IsVisible) BuildTracksMenu(selectedSub: id);
        if (osd)
        {
            var t = _player.SubtitleTracks().FirstOrDefault(t => t.Id == id);
            ShowOsd("IcSubtitles", t == null ? "Subtitles off" : $"Subtitles: {t.Label}");
        }
    }

    void CycleAudio()
    {
        var tracks = _player.AudioTracks();
        if (tracks.Count == 0) return;
        var i = tracks.ToList().FindIndex(t => t.Id == _player.CurrentAudio);
        SetAudio(tracks[(i + 1) % tracks.Count].Id, osd: true);
    }

    void SetAudio(int id, bool osd)
    {
        _player.SetAudio(id);
        if (_current != null) _current.AudioId = id;
        if (TracksMenu.IsVisible) BuildTracksMenu(selectedAudio: id);
        if (osd)
        {
            var t = _player.AudioTracks().FirstOrDefault(t => t.Id == id);
            ShowOsd("IcVolume", t == null ? "Audio" : $"Audio: {t.Label}{(t.Detail != null ? " · " + t.Detail : "")}");
        }
    }

    void AdjustSubtitleDelay(long deltaMs)
    {
        _player.SubtitleDelayMs += deltaMs;
        ShowOsd("IcSubtitles", $"Subtitle delay {_player.SubtitleDelayMs:+#;-#;0} ms");
    }

    void AdjustAudioDelay(long deltaMs)
    {
        _player.AudioDelayMs += deltaMs;
        ShowOsd("IcVolume", $"Audio delay {_player.AudioDelayMs:+#;-#;0} ms");
    }

    void ToggleTopmost()
    {
        _win.Topmost = !_win.Topmost;
        ShowOsd("IcFullscreen", _win.Topmost ? "Always on top" : "Always on top: off");
    }

    void TakeScreenshot()
    {
        if (_current == null || _mode is not (Mode.Playing or Mode.Paused) || _audioOnly) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BearMinVLC");
        Directory.CreateDirectory(dir);
        var name = $"{Path.GetFileNameWithoutExtension(_current.Title)} {Fmt.Time(_time).Replace(':', '-')}.png";
        var path = Path.Combine(dir, string.Concat(name.Split(Path.GetInvalidFileNameChars())));
        Task.Run(() => _player.TakeSnapshot(path)).ContinueWith(t =>
            Dispatcher.UIThread.Post(() => ShowOsd("IcCheck", t.Result ? "Screenshot saved to Pictures\\BearMinVLC" : "Couldn't take a screenshot")));
    }

    static void ShowInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception) { }
    }

    void JumpToPercent(int tenths)
    {
        if (_length > 0) SeekTo(_length * tenths / 10, osd: !_chromeShown);
    }

    // ------------------------------------------------------------------ keyboard

    public void HandleKey(KeyEventArgs e)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var typing = focused is TextBox;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (e.Key == Key.Tab) { _keyboardNav = true; if (_mode is not (Mode.Start or Mode.Error)) ShowChrome(); return; }

        if (e.Key == Key.Escape)
        {
            if (MenuLayer.IsVisible) CloseMenus();
            else if (DialogLayer.IsVisible || SettingsLayer.IsVisible) CloseDialogs();
            else if (_win.WindowState == WindowState.FullScreen) ToggleFullscreen();
            else return;
            e.Handled = true;
            return;
        }
        if (typing) return;
        if (SettingsLayer.IsVisible)
        {
            if (e.Key == Key.OemComma && ctrl) { CloseDialogs(); e.Handled = true; }
            return; // let arrows/space work the controls inside the panel
        }
        if (DialogLayer.IsVisible)
        {
            if (e.KeySymbol == "?" || e.Key == Key.F1) { CloseDialogs(); e.Handled = true; }
            return;
        }

        var media = _mode is Mode.Playing or Mode.Paused or Mode.Ended;
        var handled = true;
        switch (e.Key)
        {
            case Key.O when ctrl: OpenFileDialog(); break;
            case Key.U when ctrl: ShowUrlDialog(); break;
            case Key.W when ctrl: if (_mode != Mode.Start) CloseMedia(); break;
            case Key.OemComma when ctrl: ShowSettings(); break;
            case Key.Space or Key.MediaPlayPause when media: TogglePause(flash: true); break;
            case Key.Left when media: SeekBy(ctrl ? -60_000 : shift ? -3_000 : -SkipMs, osd: true); break;
            case Key.Right when media: SeekBy(ctrl ? 60_000 : shift ? 3_000 : SkipMs, osd: true); break;
            case Key.Up: SetVolume(_settings.Volume + 5, osd: !_chromeShown); break;
            case Key.Down: SetVolume(_settings.Volume - 5, osd: !_chromeShown); break;
            case Key.M when !ctrl: ToggleMute(osd: true); break;
            case Key.F or Key.F11 when !ctrl: ToggleFullscreen(); break;
            case Key.Enter when alt: ToggleFullscreen(); break;
            case Key.V when media && !ctrl: CycleSubtitle(); break;
            case Key.B when media && !ctrl: CycleAudio(); break;
            case Key.G when media && !ctrl: AdjustSubtitleDelay(-50); break;
            case Key.H when media && !ctrl: AdjustSubtitleDelay(50); break;
            case Key.J when media && !ctrl: AdjustAudioDelay(-50); break;
            case Key.K when media && !ctrl: AdjustAudioDelay(50); break;
            case Key.T when !ctrl: ToggleTopmost(); break;
            case Key.S when media && !ctrl: TakeScreenshot(); break;
            case Key.Back when media: SetSpeed(1f, osd: true); break;
            case Key.F1: ToggleShortcuts(); break;
            case >= Key.D0 and <= Key.D9 when media && !ctrl && !shift && !alt: JumpToPercent(e.Key - Key.D0); break;
            case >= Key.NumPad0 and <= Key.NumPad9 when media: JumpToPercent(e.Key - Key.NumPad0); break;
            default: handled = false; break;
        }
        if (!handled)
        {
            handled = true;
            switch (e.KeySymbol)
            {
                case "?": ToggleShortcuts(); break;
                case "[" when media: StepSpeed(-1); break;
                case "]" when media: StepSpeed(1); break;
                case "." when _mode == Mode.Paused: _player.NextFrame(); break;
                case "." when _mode == Mode.Playing: _player.Pause(); SetMode(Mode.Paused); _player.NextFrame(); break;
                default: handled = false; break;
            }
        }
        if (handled) e.Handled = true;
    }

    // ------------------------------------------------------------------ OSD & toast

    void ShowOsd(string iconKey, string text, double? fraction = null)
    {
        OsdIcon.Data = Icon(iconKey);
        OsdText.Text = text;
        OsdBar.IsVisible = fraction.HasValue;
        if (fraction is double f) OsdBarFill.Width = 120 * Math.Clamp(f, 0, 1);
        Osd.Opacity = 1;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    void ShowToast(string text, string action, Action onAction, int seconds = 5)
    {
        ToastText.Text = text;
        ToastAction.Content = action;
        _toastAction = onAction;
        Toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    /// <summary>A newer version has been downloaded; it installs on close unless they restart now.</summary>
    public void ShowUpdateReady(string version, Action restartNow)
    {
        _updateReady = version;
        UpdateVersionText();
        ShowToast($"BearMinVLC {version} is ready — installs when you close", "Restart now", restartNow, seconds: 12);
    }

    void UpdateVersionText()
    {
        var v = typeof(PlayerView).Assembly.GetName().Version;
        var current = v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        VersionText.Text = _updateReady == null
            ? $"BearMinVLC {current}"
            : $"BearMinVLC {current} · {_updateReady} installs when you close the app";
    }

    void HideToast()
    {
        _toastTimer.Stop();
        Toast.IsVisible = false;
        _toastAction = null;
    }

    void StartSpinner()
    {
        if (!Spinner.IsVisible && !_spinnerTimer.IsEnabled) _spinnerTimer.Start();
    }

    void HideSpinner()
    {
        _spinnerTimer.Stop();
        Spinner.IsVisible = false;
    }

    // ------------------------------------------------------------------ menus

    void ToggleMenu(Border menu)
    {
        if (menu.IsVisible) { CloseMenus(); return; }
        CloseMenus();
        if (menu == TracksMenu) { BuildTracksMenu(); TracksButton.Classes.Add("on"); }
        if (menu == SpeedMenu) { BuildSpeedMenu(); SpeedButton.Classes.Add("on"); }
        MenuLayer.IsVisible = true;
        menu.IsVisible = true;
        ShowChrome();
    }

    internal void CloseMenus()
    {
        MenuLayer.IsVisible = false;
        TracksMenu.IsVisible = SpeedMenu.IsVisible = ContextMenuPanel.IsVisible = false;
        TracksButton.Classes.Remove("on");
        SpeedButton.Classes.Remove("on");
        if (_mode is not (Mode.Start or Mode.Error)) ShowChrome();
    }

    internal void OpenTracksMenu() => ToggleMenu(TracksMenu);
    internal void OpenSpeedMenu() => ToggleMenu(SpeedMenu);

    void BuildTracksMenu(int? selectedSub = null, int? selectedAudio = null)
    {
        TracksList.Children.Clear();
        var curSub = selectedSub ?? _player.CurrentSubtitle;
        var curAudio = selectedAudio ?? _player.CurrentAudio;

        TracksList.Children.Add(MenuHeader("Subtitles"));
        TracksList.Children.Add(MenuRow("Off", null, check: curSub < 0, () => SetSubtitle(-1, osd: false)));
        foreach (var t in _player.SubtitleTracks())
            TracksList.Children.Add(MenuRow(t.Label, t.Detail, check: t.Id == curSub, () => SetSubtitle(t.Id, osd: false)));
        TracksList.Children.Add(MenuRow("Add subtitle file…", null, check: false, AddSubtitleFileDialog, leadingIcon: "IcPlus"));

        var audio = _player.AudioTracks();
        if (audio.Count > 1)
        {
            TracksList.Children.Add(new Separator { Classes = { "menu" } });
            TracksList.Children.Add(MenuHeader("Audio"));
            foreach (var t in audio)
                TracksList.Children.Add(MenuRow(t.Label, t.Detail, check: t.Id == curAudio, () => SetAudio(t.Id, osd: false)));
        }
    }

    void BuildSpeedMenu()
    {
        SpeedList.Children.Clear();
        SpeedList.Children.Add(MenuHeader("Speed"));
        foreach (var s in Speeds)
        {
            var rate = s;
            SpeedList.Children.Add(MenuRow(Fmt.Speed(s), Math.Abs(s - 1f) < 0.001f ? "Normal" : null,
                check: Math.Abs(_player.Rate - s) < 0.001f, () => SetSpeed(rate, osd: false)));
        }
    }

    void OpenVideoContextMenu(Point at)
    {
        _contextShowsAspect = false;
        BuildVideoContextMenu();
        ShowContextMenu(at);
    }

    void BuildVideoContextMenu()
    {
        ContextList.Children.Clear();
        if (_contextShowsAspect)
        {
            ContextList.Children.Add(MenuRow("Aspect ratio", null, check: false, () => { _contextShowsAspect = false; BuildVideoContextMenu(); }, leadingIcon: "IcChevronLeft"));
            ContextList.Children.Add(new Separator { Classes = { "menu" } });
            foreach (var (label, value) in AspectRatios)
            {
                var v = value;
                ContextList.Children.Add(MenuRow(label, null, check: _player.AspectRatio == v, () => { _player.AspectRatio = v; CloseMenus(); ShowOsd("IcFullscreen", $"Aspect ratio: {label}"); }));
            }
            return;
        }
        ContextList.Children.Add(PlainRow("Open file…", "Ctrl O", () => OpenFileDialog()));
        ContextList.Children.Add(PlainRow("Open URL…", "Ctrl U", () => { CloseMenus(); ShowUrlDialog(); }));
        ContextList.Children.Add(new Separator { Classes = { "menu" } });
        ContextList.Children.Add(PlainRow("Always on top", "T", () => { CloseMenus(); ToggleTopmost(); }, trailingCheck: _win.Topmost));
        if (!_audioOnly)
        {
            var current = AspectRatios.FirstOrDefault(a => a.Value == _player.AspectRatio).Label ?? _player.AspectRatio ?? "Default";
            ContextList.Children.Add(PlainRow("Aspect ratio", current, () => { _contextShowsAspect = true; BuildVideoContextMenu(); }, chevron: true));
            ContextList.Children.Add(PlainRow("Take screenshot", "S", () => { CloseMenus(); TakeScreenshot(); }));
        }
        ContextList.Children.Add(new Separator { Classes = { "menu" } });
        if (_current is { IsUrl: false } cur)
            ContextList.Children.Add(PlainRow("Show in Explorer", null, () => { CloseMenus(); ShowInExplorer(cur.Source); }));
        ContextList.Children.Add(PlainRow("Close video", "Ctrl W", () => { CloseMenus(); CloseMedia(); }));
        ContextList.Children.Add(new Separator { Classes = { "menu" } });
        ContextList.Children.Add(PlainRow("Settings…", "Ctrl ,", () => { CloseMenus(); ShowSettings(); }));
        ContextList.Children.Add(PlainRow("Keyboard shortcuts", "?", () => { CloseMenus(); ToggleShortcuts(); }));
    }

    void OpenRecentContextMenu(RecentItem item, bool missing, Point at)
    {
        ContextList.Children.Clear();
        if (missing)
        {
            ContextList.Children.Add(PlainRow("Locate…", null, () => { CloseMenus(); LocateMissing(item); }));
        }
        else
        {
            ContextList.Children.Add(PlainRow(item.CanResume ? $"Resume at {Fmt.Time(item.PositionMs)}" : "Play", null, () => { CloseMenus(); OpenSource(item.Source); }));
            if (item.CanResume || item.Watched)
                ContextList.Children.Add(PlainRow("Play from start", null, () =>
                {
                    CloseMenus();
                    item.PositionMs = 0;
                    item.Watched = false;
                    OpenSource(item.Source);
                }));
            if (!item.IsUrl)
                ContextList.Children.Add(PlainRow("Show in Explorer", null, () => { CloseMenus(); ShowInExplorer(item.Source); }));
        }
        ContextList.Children.Add(new Separator { Classes = { "menu" } });
        ContextList.Children.Add(PlainRow("Remove from Recent", null, () => { CloseMenus(); _library.Remove(item); RefreshRecent(); }));
        ShowContextMenu(at);
    }

    void ShowContextMenu(Point at)
    {
        CloseMenus();
        MenuLayer.IsVisible = true;
        ContextMenuPanel.IsVisible = true;
        ContextMenuPanel.Measure(Size.Infinity);
        var size = ContextMenuPanel.DesiredSize;
        var bounds = Root.Bounds;
        var x = Math.Clamp(at.X, 8, Math.Max(8, bounds.Width - size.Width - 8));
        var y = at.Y + size.Height > bounds.Height - 8 ? Math.Max(8, at.Y - size.Height) : at.Y;
        ContextMenuPanel.Margin = new Thickness(x, y, 0, 0);
        if (_mode is not (Mode.Start or Mode.Error)) ShowChrome();
    }

    internal void OpenContextMenuForTest(Point at) => OpenVideoContextMenu(at);

    static Control MenuHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.Medium,
        Foreground = Res<IBrush>("Muted"),
        Margin = new Thickness(38, 6, 10, 4),
    };

    /// <summary>A row with a leading 16 px column for the check mark (radio menus).</summary>
    static Button MenuRow(string label, string? detail, bool check, Action onClick, string? leadingIcon = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,12,*") };
        if (check || leadingIcon != null)
        {
            var icon = new PathShape
            {
                Data = Icon(check ? "IcCheck" : leadingIcon!),
                Stroke = check ? Res<IBrush>("Accent") : Res<IBrush>("Text"),
                StrokeThickness = check ? 2.25 : 2,
                Classes = { "icon" },
            };
            grid.Children.Add(new Viewbox { Width = 16, Height = 16, Child = icon, VerticalAlignment = VerticalAlignment.Center });
        }

        var text = new TextBlock
        {
            FontWeight = check ? FontWeight.Medium : FontWeight.Normal,
            Foreground = check ? Brushes.White : Res<IBrush>("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Inlines!.Add(new Run(label));
        if (detail != null)
            text.Inlines.Add(new Run(" · " + detail) { Foreground = Res<IBrush>("Muted"), FontWeight = FontWeight.Normal });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var button = new Button { Classes = { "row" }, Content = grid };
        AutomationProperties.SetName(button, detail == null ? label : $"{label}, {detail}");
        if (check) AutomationProperties.SetHelpText(button, "Selected");
        ToolTip.SetTip(button, detail == null ? label : $"{label} · {detail}");
        ToolTip.SetShowDelay(button, 800);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A plain row with a shortcut hint on the right (right-click menu).</summary>
    static Button PlainRow(string label, string? hint, Action onClick, bool trailingCheck = false, bool chevron = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (trailingCheck)
            right.Children.Add(new Viewbox
            {
                Width = 14, Height = 14,
                Child = new PathShape {Classes = { "icon" }, Data = Icon("IcCheck"), Stroke = Res<IBrush>("Accent"), StrokeThickness = 2.25 },
            });
        if (hint != null)
            right.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Res<IBrush>("Muted"), VerticalAlignment = VerticalAlignment.Center });
        if (chevron)
            right.Children.Add(new Viewbox
            {
                Width = 14, Height = 14,
                Child = new PathShape {Classes = { "icon" }, Data = Icon("IcChevronRight"), Stroke = Res<IBrush>("Muted"), StrokeThickness = 2 },
            });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var button = new Button { Classes = { "row" }, Content = grid };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ------------------------------------------------------------------ dialogs

    internal async void ShowUrlDialog()
    {
        CloseMenus();
        DialogLayer.IsVisible = true;
        UrlDialog.IsVisible = true;
        KeysSheet.IsVisible = false;
        UrlBox.Text = "";
        UrlHint.Text = "HTTP, HLS, RTSP and most other streams work.";
        ShowChrome();

        string? clip = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                clip = (await clipboard.TryGetTextAsync())?.Trim();
        }
        catch (Exception) { }
        if (clip != null && Player.IsUrl(clip) && !clip.Contains('\n'))
        {
            UrlBox.Text = clip;
            UrlHint.Text = "Pasted from your clipboard. HTTP, HLS, RTSP and most other streams work.";
        }
        _overlay?.Activate();
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    void SubmitUrl()
    {
        var text = UrlBox.Text?.Trim() ?? "";
        if (!Player.IsUrl(text))
        {
            UrlHint.Text = "That doesn't look like an address. Try something starting with https:// or rtsp://";
            return;
        }
        CloseDialogs();
        OpenSource(text);
    }

    internal void ToggleShortcuts()
    {
        if (KeysSheet.IsVisible) { CloseDialogs(); return; }
        CloseMenus();
        DialogLayer.IsVisible = true;
        KeysSheet.IsVisible = true;
        UrlDialog.IsVisible = false;
        if (!_settings.SeenShortcutHint)
        {
            _settings.SeenShortcutHint = true;
            ShortcutHint.IsVisible = false;
        }
        ShowChrome();
    }

    internal void CloseDialogs()
    {
        DialogLayer.IsVisible = false;
        UrlDialog.IsVisible = false;
        KeysSheet.IsVisible = false;
        SettingsLayer.IsVisible = false;
        Focus();
        if (_mode is not (Mode.Start or Mode.Error)) ShowChrome();
    }

    void BuildShortcutSheet()
    {
        KeysGrid.Children.Clear();
        (string Title, (string Label, string[] Keys)[] Rows)[] groups =
        [
            ("Playback",
            [
                ("Play / pause", ["Space"]),
                ($"Skip {_settings.SkipSeconds} s", ["←", "→"]),
                ("Skip 3 s", ["Shift", "← →"]),
                ("Skip 1 min", ["Ctrl", "← →"]),
                ("Jump to 0–90%", ["0 – 9"]),
                ("Next frame", ["."]),
            ]),
            ("Sound & subtitles",
            [
                ("Volume", ["↑", "↓"]),
                ("Mute", ["M"]),
                ("Next subtitle", ["V"]),
                ("Next audio track", ["B"]),
                ("Subtitle delay", ["G", "H"]),
                ("Audio delay", ["J", "K"]),
            ]),
            ("Speed & window",
            [
                ("Slower / faster", ["[", "]"]),
                ("Normal speed", ["Backspace"]),
                ("Fullscreen", ["F"]),
                ("Always on top", ["T"]),
                ("Screenshot", ["S"]),
                ("Open file / URL", ["Ctrl O", "Ctrl U"]),
                ("Settings", ["Ctrl ,"]),
            ]),
        ];

        for (var g = 0; g < groups.Length; g++)
        {
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new TextBlock
            {
                Text = groups[g].Title, FontSize = 12, FontWeight = FontWeight.Medium,
                Foreground = Res<IBrush>("Muted"), Margin = new Thickness(0, 0, 0, 6),
            });
            foreach (var (label, keys) in groups[g].Rows)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 32 };
                row.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                foreach (var k in keys)
                    chips.Children.Add(new Border { Classes = { "kbd" }, Child = new TextBlock { Text = k } });
                Grid.SetColumn(chips, 1);
                row.Children.Add(chips);
                col.Children.Add(row);
            }
            Grid.SetColumn(col, g * 2);
            KeysGrid.Children.Add(col);
        }
    }

    // ------------------------------------------------------------------ settings

    static readonly (string Name, string Code)[] Languages =
    [
        ("Automatic", ""), ("English", "en"), ("Spanish", "es"), ("French", "fr"), ("German", "de"),
        ("Italian", "it"), ("Portuguese", "pt"), ("Dutch", "nl"), ("Swedish", "sv"), ("Danish", "da"),
        ("Norwegian", "no"), ("Finnish", "fi"), ("Polish", "pl"), ("Czech", "cs"), ("Russian", "ru"),
        ("Ukrainian", "uk"), ("Turkish", "tr"), ("Greek", "el"), ("Arabic", "ar"), ("Hebrew", "he"),
        ("Hindi", "hi"), ("Japanese", "ja"), ("Korean", "ko"), ("Chinese", "zh"),
    ];

    List<(string Id, string Name)> _audioDevices = [];

    long SkipMs => _settings.SkipSeconds * 1000L;

    OpenOptions OpenOpts() => new(_settings.SubtitleLanguage, _settings.AudioLanguage);

    void WireSettings()
    {
        ResumeToggle.IsCheckedChanged += (_, _) =>
        {
            if (_fillingSettings) return;
            _settings.Resume = ResumeToggle.IsChecked == true;
            _settings.Save();
        };
        HwToggle.IsCheckedChanged += (_, _) =>
        {
            if (_fillingSettings) return;
            _settings.HardwareDecoding = HwToggle.IsChecked == true;
            RebuildEngine();
        };

        SubLanguageBox.ItemsSource = Languages.Select(l => l.Name).ToList();
        AudioLanguageBox.ItemsSource = Languages.Select(l => l.Name).ToList();
        SubLanguageBox.SelectionChanged += (_, _) =>
        {
            if (_fillingSettings || SubLanguageBox.SelectedIndex < 0) return;
            _settings.SubtitleLanguage = Languages[SubLanguageBox.SelectedIndex].Code;
            _settings.Save();
        };
        AudioLanguageBox.SelectionChanged += (_, _) =>
        {
            if (_fillingSettings || AudioLanguageBox.SelectedIndex < 0) return;
            _settings.AudioLanguage = Languages[AudioLanguageBox.SelectedIndex].Code;
            _settings.Save();
        };
        AudioDeviceBox.SelectionChanged += (_, _) =>
        {
            if (_fillingSettings || AudioDeviceBox.SelectedIndex < 0) return;
            var id = AudioDeviceBox.SelectedIndex == 0 ? "" : _audioDevices[AudioDeviceBox.SelectedIndex - 1].Id;
            _settings.AudioDevice = id;
            _settings.Save();
            _player.SetAudioDevice(id);
        };
        UpdateSkipLabels();
    }

    internal void ShowSettings()
    {
        CloseMenus();
        DialogLayer.IsVisible = false;
        FillSettings();
        UpdateVersionText();
        SettingsLayer.IsVisible = true;
        LayoutSettingsColumns(Root.Bounds.Width);
        ShowChrome();
    }

    void FillSettings()
    {
        _fillingSettings = true;
        try
        {
            ResumeToggle.IsChecked = _settings.Resume;
            HwToggle.IsChecked = _settings.HardwareDecoding;
            BuildChoices<int>(SkipChoices, [("5 s", 5), ("10 s", 10), ("15 s", 15), ("30 s", 30)], _settings.SkipSeconds, v =>
            {
                _settings.SkipSeconds = v;
                _settings.Save();
                UpdateSkipLabels();
                BuildShortcutSheet();
            });
            BuildChoices<string>(DeinterlaceChoices, [("Auto", "auto"), ("On", "on"), ("Off", "off")], _settings.Deinterlace, v =>
            {
                _settings.Deinterlace = v;
                RebuildEngine();
            });
            BuildChoices<int>(SubSizeChoices, [("Small", 75), ("Normal", 100), ("Large", 130), ("Larger", 160)], _settings.SubtitleScale, v =>
            {
                _settings.SubtitleScale = v;
                RebuildEngine();
            });
            BuildChoices<bool>(SubPositionChoices, [("Bottom", false), ("Above controls", true)], _settings.SubtitlesAboveControls, v =>
            {
                _settings.SubtitlesAboveControls = v;
                RebuildEngine();
            });
            SubLanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == _settings.SubtitleLanguage));
            AudioLanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == _settings.AudioLanguage));

            _audioDevices = _player.AudioDevices().ToList();
            AudioDeviceBox.ItemsSource = new[] { "Windows default" }.Concat(_audioDevices.Select(d => d.Name)).ToList();
            var device = _audioDevices.FindIndex(d => d.Id == _settings.AudioDevice);
            AudioDeviceBox.SelectedIndex = device >= 0 ? device + 1 : 0;
        }
        finally
        {
            _fillingSettings = false;
        }
    }

    static void BuildChoices<T>(StackPanel host, (string Label, T Value)[] options, T current, Action<T> onPick)
    {
        host.Children.Clear();
        foreach (var (label, value) in options)
        {
            var button = new Button { Classes = { "seg" }, Content = label };
            if (EqualityComparer<T>.Default.Equals(value, current)) button.Classes.Add("on");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) =>
            {
                if (button.Classes.Contains("on")) return;
                foreach (var child in host.Children) child.Classes.Remove("on");
                button.Classes.Add("on");
                onPick(value);
            };
            host.Children.Add(button);
        }
    }

    /// <summary>Two columns when there's room, one stacked column in a narrow window.</summary>
    void LayoutSettingsColumns(double width)
    {
        var narrow = width < 820;
        Grid.SetColumn(SettingsRight, narrow ? 0 : 2);
        Grid.SetRow(SettingsRight, narrow ? 1 : 0);
        SettingsColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 40);
        SettingsColumns.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    void UpdateSkipLabels()
    {
        var s = _settings.SkipSeconds;
        BackLabel.Text = ForwardLabel.Text = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutomationProperties.SetName(BackButton, $"Back {s} seconds");
        AutomationProperties.SetName(ForwardButton, $"Forward {s} seconds");
    }

    /// <summary>
    /// Subtitle rendering, deinterlacing and hardware decoding are fixed when libVLC starts, so changing
    /// them restarts the engine and reopens the current video at the same spot.
    /// </summary>
    void RebuildEngine()
    {
        _settings.Save();
        var reopen = _current != null && _mode is Mode.Playing or Mode.Paused or Mode.Loading;
        var at = _time;
        var paused = _mode == Mode.Paused;
        var rate = _player.Rate;
        var aspect = _player.AspectRatio;
        if (_current != null && _mode is Mode.Playing or Mode.Paused)
        {
            if (_player.SubtitleTracks().Count > 0) _current.SubtitleId = _player.CurrentSubtitle;
            if (_player.CurrentAudio >= 0) _current.AudioId = _player.CurrentAudio;
        }

        _player.Rebuild(_settings.EngineArgs(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1), _settings.HardwareDecoding);

        if (reopen)
        {
            _pauseAfterReload = paused;
            _hardEnded = false;
            _pausedNearEnd = false;
            SetMode(Mode.Loading);
            _player.Open(_current!.Source, at, OpenOpts());
            SetSpeed(rate, osd: false);
            _player.AspectRatio = aspect;
        }
        else if (_mode == Mode.Ended)
        {
            _hardEnded = true; // the old engine took the last frame with it; Replay reopens the file
        }
    }

    internal void SetSubtitlesAboveForTest(bool above)
    {
        _settings.SubtitlesAboveControls = above;
        RebuildEngine();
    }

    internal void OpenLanguageDropDownForTest(bool open) => SubLanguageBox.IsDropDownOpen = open;

    // ------------------------------------------------------------------ start screen

    void RefreshRecent()
    {
        RecentList.Children.Clear();
        var items = _library.Items.Take(6).ToList();
        RecentSection.IsVisible = items.Count > 0;
        foreach (var item in items)
            RecentList.Children.Add(RecentRow(item));
    }

    Control RecentRow(RecentItem item)
    {
        var missing = !item.IsUrl && !File.Exists(item.Source);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,14,*,14,Auto") };

        var thumb = new Border
        {
            Width = 64, Height = 36, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = Res<IBrush>("Surface"),
        };
        if (missing)
        {
            thumb.Background = null;
            thumb.BorderBrush = new SolidColorBrush(Color.Parse("#1AFFFFFF"));
            thumb.BorderThickness = new Thickness(1);
            thumb.Child = new Viewbox
            {
                Width = 16, Height = 16,
                Child = new PathShape {Classes = { "icon" }, Data = Icon("IcFileMissing"), Stroke = Res<IBrush>("Muted") },
            };
        }
        else if (item.Thumb != null && File.Exists(item.Thumb))
        {
            try { thumb.Child = new Image { Source = new Bitmap(item.Thumb), Stretch = Stretch.UniformToFill }; }
            catch (Exception) { }
        }
        grid.Children.Add(thumb);

        var title = new TextBlock
        {
            Text = item.Title, FontSize = 14, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res<IBrush>(missing ? "Muted" : "Text"),
        };
        var meta = new TextBlock
        {
            Text = RecentMeta(item, missing), FontSize = 12, Foreground = Res<IBrush>("Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis, FontFeatures = FontFeatureCollection.Parse("+tnum"),
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, meta } };
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        if (!missing && item.LengthMs > 0 && (item.CanResume || item.Watched))
        {
            var fraction = item.Watched ? 1.0 : Math.Clamp((double)item.PositionMs / item.LengthMs, 0, 1);
            var bar = new Border
            {
                Width = 56, Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#24FFFFFF")),
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left, Width = 56 * fraction, CornerRadius = new CornerRadius(2),
                    Background = item.Watched ? new SolidColorBrush(Color.Parse("#59FFFFFF")) : Res<IBrush>("Accent"),
                },
            };
            Grid.SetColumn(bar, 4);
            grid.Children.Add(bar);
        }

        var button = new Button { Classes = { "recent" }, Content = grid };
        AutomationProperties.SetName(button, $"{item.Title}, {RecentMeta(item, missing)}");
        button.Click += (_, _) => { if (missing) LocateMissing(item); else OpenSource(item.Source); };
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsRightButtonPressed) return;
            OpenRecentContextMenu(item, missing, e.GetPosition(Root));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        return button;
    }

    static string RecentMeta(RecentItem item, bool missing) =>
        missing ? "File not found — right-click to locate or remove"
        : item.IsUrl ? "Stream"
        : item.Watched ? $"Watched · {Fmt.Time(item.LengthMs)}"
        : item.CanResume ? $"Resume at {Fmt.Time(item.PositionMs)} · {Fmt.Time(item.LengthMs)}"
        : item.LengthMs > 0 ? Fmt.Time(item.LengthMs)
        : Path.GetFileName(Path.GetDirectoryName(item.Source)) ?? "";

    void ClearRecent()
    {
        var old = _library.Clear();
        RefreshRecent();
        ShowToast("Recent list cleared", "Undo", () => { _library.Restore(old); RefreshRecent(); });
    }

    // ------------------------------------------------------------------ drag & drop

    static List<string> DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .Where(File.Exists)
            .ToList() ?? [];

    static bool IsSubtitle(string path) =>
        SubtitleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void OnDragEnter(DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Count == 0) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DragDropEffects.Copy;

        var playing = _current != null && _mode is Mode.Playing or Mode.Paused or Mode.Ended;
        if (files.All(IsSubtitle))
        {
            DropTitle.Text = playing ? "Add subtitles" : "Open a video first";
            DropBody.Text = playing ? $"Adds {Path.GetFileName(files[0])} to {_current!.Title}." : "Subtitle files are added to the video that's playing.";
            e.DragEffects = playing ? DragDropEffects.Copy : DragDropEffects.None;
        }
        else
        {
            DropTitle.Text = "Drop to play";
            DropBody.Text = files.Count > 1
                ? $"{files.Count} files — the first one plays. Playlists are coming later."
                : playing ? $"Replaces {_current!.Title} — your place in it is saved. Subtitle files are added to the current video instead."
                : "";
        }
        DropBody.IsVisible = DropBody.Text.Length > 0;
        DropLayer.IsVisible = true;
    }

    public void OnDragOver(DragEventArgs e)
    {
        if (!DropLayer.IsVisible) OnDragEnter(e);
        else e.DragEffects = DroppedFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    public void OnDragLeave(DragEventArgs e) => DropLayer.IsVisible = false;

    public void OnDrop(DragEventArgs e)
    {
        DropLayer.IsVisible = false;
        var files = DroppedFiles(e);
        if (files.Count == 0) return;

        var subtitles = files.Where(IsSubtitle).ToList();
        var media = files.Where(f => !IsSubtitle(f)).ToList();
        if (media.Count == 0)
        {
            if (_current != null && _mode is Mode.Playing or Mode.Paused or Mode.Ended)
                foreach (var s in subtitles) AddSubtitle(s);
            return;
        }
        media.Sort(Native.CompareNatural);
        OpenSource(media[0]);
        if (media.Count > 1)
            ShowOsd("IcPlus", $"Playing {Path.GetFileName(media[0])} — playlists are coming later");
    }

    internal void ShowDropForTest(string title, string body)
    {
        DropTitle.Text = title;
        DropBody.Text = body;
        DropLayer.IsVisible = true;
    }

    internal void HideDropForTest() => DropLayer.IsVisible = false;

    // ------------------------------------------------------------------ persistence

    void SaveProgress(bool final)
    {
        // Nothing played yet (still opening): keep whatever resume point was saved before.
        if (_current == null || _mode is Mode.Start or Mode.Error || !_sawProgress) return;
        if (_mode is Mode.Playing or Mode.Paused)
        {
            var sub = _player.CurrentSubtitle;
            var audio = _player.CurrentAudio;
            if (_player.SubtitleTracks().Count > 0) _current.SubtitleId = sub;
            if (audio >= 0) _current.AudioId = audio;
            if (final && !_current.IsUrl && !_audioOnly) SaveThumbnail(_current);
        }
        _library.Record(_current, _mode == Mode.Ended ? _length : _time, _length);
    }

    void SaveThumbnail(RecentItem item)
    {
        try
        {
            Directory.CreateDirectory(Library.ThumbDir);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(item.Source.ToLowerInvariant())))[..16] + ".png";
            var path = Path.Combine(Library.ThumbDir, name);
            if (_player.TakeSnapshot(path, 160)) item.Thumb = path;
        }
        catch (Exception) { }
    }

    public void OnClosing()
    {
        SaveProgress(final: true);
        _saveTimer.Stop();
        Native.KeepAwake(false);
    }
}
