using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinMacCalendar;

public sealed class WidgetInstance
{
    public const double GridUnit = 183;
    public const double GridGap = 29;
    public const double GridStep = GridUnit + GridGap; // 212px: 183*2+29=395, 395*2+29=819

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public Variant Variant { get; set; } = Variant.Month;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Scale { get; set; } = 1.0;
    public bool DetailedLunar { get; set; }
    public bool Locked { get; set; }

    public Size BaseSize => CalendarCard.Sizes[(int)Variant];

    public (int Cols, int Rows) GridSpan => Variant switch
    {
        Variant.Month or Variant.Compact or Variant.Date => (1, 1),
        Variant.Summary => (2, 1),
        Variant.Agenda or Variant.ShortTimeline => (2, 2),
        Variant.Week => (4, 2),
        _ => (1, 1)
    };

    public Size EffectiveSize(bool uniformScale, double globalScale)
    {
        double s = Math.Clamp(uniformScale ? globalScale : Scale, 0.60, 1.30);
        var b = BaseSize;
        return new Size(Math.Round(b.Width * s), Math.Round(b.Height * s));
    }
}

internal sealed class WidgetWindow : Window
{
    public string WidgetId { get; }
    private readonly Func<bool> isLocked;
    private readonly Action<WidgetWindow> onMoved;
    private readonly Action<WidgetWindow> onShowMenu;

    public WidgetWindow(
        string widgetId,
        Func<bool> isLocked,
        Action<WidgetWindow> onMoved,
        Action<WidgetWindow> onShowMenu)
    {
        WidgetId = widgetId;
        this.isLocked = isLocked;
        this.onMoved = onMoved;
        this.onShowMenu = onShowMenu;

        Title = "桌面日历";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        SourceInitialized += (_, _) => Desktop.Attach(this);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Dispatcher.BeginInvoke(() => { WindowState = WindowState.Normal; Desktop.Lower(this); });
        };
        MouseRightButtonUp += (_, e) => { e.Handled = true; this.onShowMenu(this); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!this.isLocked() && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
                this.onMoved(this);
                Desktop.Lower(this);
            }
        };
    }

    public void ApplyContent(UIElement cardRoot, Size baseSize, double scale)
    {
        double s = Math.Clamp(scale, 0.60, 1.30);
        if (cardRoot is FrameworkElement fe)
        {
            fe.Width = baseSize.Width;
            fe.Height = baseSize.Height;
            fe.LayoutTransform = Math.Abs(s - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
        }
        Width = Math.Round(baseSize.Width * s);
        Height = Math.Round(baseSize.Height * s);
        Content = cardRoot;
    }
}
