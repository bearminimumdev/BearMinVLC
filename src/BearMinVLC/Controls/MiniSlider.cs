using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BearMinVLC.Controls;

/// <summary>The 88 px volume slider: 4 px track, white fill, 0–100.</summary>
public sealed class MiniSlider : Control
{
    static readonly IBrush Track = new SolidColorBrush(Color.Parse("#47FFFFFF"));
    static readonly IBrush FillBrush = new SolidColorBrush(Color.Parse("#EDEBE7"));
    static readonly BoxShadows ThumbShadow = BoxShadows.Parse("0 1 3 0 #80000000");

    int _value;
    bool _dragging;
    bool _hover;

    public event Action<int>? ValueChanged;

    public MiniSlider()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, "Volume");
    }

    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 100);
            AutomationProperties.SetHelpText(this, $"{_value}%");
            InvalidateVisual();
        }
    }

    public bool Dimmed { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var y = (h - 4) / 2;
        ctx.DrawRectangle(Track, null, new Rect(0, y, w, 4), 2, 2);
        var fx = w * _value / 100.0;
        if (fx > 0)
            ctx.DrawRectangle(FillBrush, null, new Rect(0, y, fx, 4), 2, 2);
        if (_hover || _dragging)
            ctx.DrawRectangle(Brushes.White, null, new RoundedRect(new Rect(fx - 6, h / 2 - 6, 12, 12), 6), ThumbShadow);
    }

    void SetFromX(double x)
    {
        var v = (int)Math.Round(Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * 100);
        if (v == _value) return;
        Value = v;
        ValueChanged?.Invoke(v);
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); _hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = false; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetFromX(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Value = _value + (e.Delta.Y > 0 ? 5 : -5);
        ValueChanged?.Invoke(_value);
        e.Handled = true;
    }
}
