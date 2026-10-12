using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BearMinVLC.Services;

namespace BearMinVLC.Controls;

/// <summary>
/// The seek bar: 4 px track that grows to 6 px under the pointer, amber progress, white thumb,
/// and a time tooltip above the hover point. Drawn by hand so it stays crisp and cheap over video.
/// </summary>
public sealed class SeekBar : Control
{
    static readonly IBrush Track = new SolidColorBrush(Color.Parse("#47FFFFFF"));
    static readonly IPen TrackOutline = new Pen(new SolidColorBrush(Color.Parse("#40000000")), 1);
    static readonly IBrush Tick = new SolidColorBrush(Color.Parse("#D9FFFFFF"));
    static readonly IBrush TipBackground = new SolidColorBrush(Color.Parse("#EB1A1917"));
    static readonly IPen TipBorder = new Pen(new SolidColorBrush(Color.Parse("#14FFFFFF")), 1);
    static readonly BoxShadows ThumbShadow = BoxShadows.Parse("0 1 4 0 #80000000");

    long _duration;
    long _position;
    double? _hoverX;
    bool _dragging;
    DateTime _lastDragSeek;

    public IBrush Fill { get; set; } = new SolidColorBrush(Color.Parse("#F5A524"));

    /// <summary>Raised while dragging (final = false) and on release (final = true).</summary>
    public event Action<long, bool>? SeekRequested;
    public event Action<long>? SeekByRequested;

    public bool IsDragging => _dragging;

    public SeekBar()
    {
        Focusable = true;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, "Seek");
    }

    public long Duration
    {
        get => _duration;
        set { _duration = Math.Max(0, value); UpdateA11y(); InvalidateVisual(); }
    }

    public long Position
    {
        get => _position;
        set
        {
            if (_dragging) return; // the pointer owns the thumb while dragging
            _position = Math.Clamp(value, 0, Math.Max(_duration, 0));
            UpdateA11y();
            InvalidateVisual();
        }
    }

    void UpdateA11y() =>
        AutomationProperties.SetHelpText(this, $"{Fmt.Time(_position)} of {Fmt.Time(_duration)}");

    double Fraction(long ms) => _duration > 0 ? Math.Clamp((double)ms / _duration, 0, 1) : 0;
    long MsAt(double x) => _duration <= 0 ? 0 : (long)(Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * _duration);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0) return;

        // 6 px at rest, 8 px while the pointer is on it; the whole control height is the hit area.
        var big = _hoverX.HasValue || _dragging;
        var th = big ? 8.0 : 6.0;
        var y = (h - th) / 2;
        var track = new Rect(0, y, w, th);
        ctx.DrawRectangle(Track, TrackOutline, track, th / 2, th / 2);
        if (_duration <= 0) return;

        var fx = Fraction(_position) * w;
        if (fx > 0)
            ctx.DrawRectangle(Fill, null, new Rect(0, y, fx, th), th / 2, th / 2);

        if (_hoverX is double hx && !_dragging)
            ctx.DrawRectangle(Tick, null, new Rect(hx - 1, y - 3, 2, th + 6), 1, 1);

        // Thumb (grows a little while grabbed)
        var r = _dragging ? 9.0 : big ? 8.0 : 7.0;
        ctx.DrawRectangle(Brushes.White, null, new RoundedRect(new Rect(fx - r, h / 2 - r, r * 2, r * 2), r), ThumbShadow);

        // Tooltip with the time under the pointer
        if (_hoverX.HasValue || _dragging)
        {
            var tipMs = _dragging ? _position : MsAt(_hoverX!.Value);
            var text = new FormattedText(Fmt.Time(tipMs), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Parse("avares://BearMinVLC/Assets/Fonts#Geist"), FontStyle.Normal, FontWeight.Medium),
                13, Brushes.White);
            var tw = text.Width + 16;
            var tx = Math.Clamp((_dragging ? fx : _hoverX!.Value) - tw / 2, 0, Math.Max(0, w - tw));
            var tip = new Rect(tx, y - 10 - 24, tw, 24);
            ctx.DrawRectangle(TipBackground, TipBorder, tip, 6, 6);
            ctx.DrawText(text, new Point(tx + 8, tip.Y + (24 - text.Height) / 2));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        _hoverX = Math.Clamp(x, 0, Bounds.Width);
        if (_dragging)
        {
            _position = MsAt(x);
            if ((DateTime.UtcNow - _lastDragSeek).TotalMilliseconds > 60)
            {
                _lastDragSeek = DateTime.UtcNow;
                SeekRequested?.Invoke(_position, false);
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        _position = MsAt(e.GetPosition(this).X);
        _lastDragSeek = DateTime.UtcNow;
        SeekRequested?.Invoke(_position, false);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        SeekRequested?.Invoke(_position, true);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            SeekRequested?.Invoke(_position, true);
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_duration <= 0) return;
        SeekByRequested?.Invoke(e.Delta.Y > 0 ? 5000 : -5000);
        e.Handled = true;
    }
}
