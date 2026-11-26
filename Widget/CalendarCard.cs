using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Automation;
using Microsoft.Win32;

namespace WinMacCalendar;

public enum Variant { Month, Agenda, Compact, Date, Summary, ShortTimeline, Week }
public enum TravelDisplayMode { TodayOnly, Always, Never }
public enum ThemePreference { System, Light, Dark }

public static class Palette
{
    private static readonly ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string Hex, byte Alpha), SolidColorBrush> AlphaCache = new();

    public static bool SystemDark => Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1), 0);
    public static bool Dark => SystemDark;
    public static bool ResolveDark(ThemePreference pref) => pref switch
    {
        ThemePreference.Light => false,
        ThemePreference.Dark => true,
        _ => SystemDark
    };

    public static Brush Color(string hex) => BrushCache.GetOrAdd(hex, static h =>
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(h)!;
        brush.Freeze();
        return brush;
    });

    public static SolidColorBrush Alpha(string hex, byte alpha) => AlphaCache.GetOrAdd((hex.ToUpperInvariant(), alpha), static key =>
    {
        var c = ((SolidColorBrush)Color(key.Hex)).Color;
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(key.Alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    });
}

public sealed partial class CalendarCard
{
    public static readonly string[] Names = ["月历", "日程列表", "紧凑摘要", "大字日期", "双栏摘要", "双日时间线", "四日时间线"];
    public static readonly Size[] Sizes = [new(183, 183), new(395, 395), new(183, 183), new(183, 183), new(395, 183), new(395, 395), new(819, 395)];

    private readonly Brush paper, ink, muted, subtle, rule, hover, red, borderBrush;
    private readonly bool night;
    private readonly DateTime now;
    private readonly IReadOnlyList<Entry> entries;
    private readonly Action<DateTime> openDay;
    private readonly Action<Entry> openEvent;
    private readonly TravelDisplayMode travelMode;
    private readonly bool showMonthDots;
    private readonly DockPanel body = new();
    public readonly Border Root;

    public CalendarCard(
        Variant variant,
        DateTime month,
        DateTime now,
        IReadOnlyList<Entry> entries,
        Action<DateTime> openDay,
        Action<Entry> openEvent,
        Action<int> moveMonth,
        Action showMenu,
        bool? dark = null,
        bool detailedLunar = false,
        TravelDisplayMode travelMode = TravelDisplayMode.TodayOnly,
        double opacity = 1.0,
        bool showMonthDots = false)
    {
        this.now = now;
        this.entries = entries;
        this.openDay = openDay;
        this.openEvent = openEvent;
        this.travelMode = travelMode;
        this.showMonthDots = showMonthDots;
        night = dark ?? Palette.Dark;

        byte surfaceAlpha = (byte)Math.Round(Math.Clamp(opacity, 0.65, 1.0) * 255);
        paper = Palette.Alpha(night ? "#242426" : "#FFFFFF", surfaceAlpha);
        ink = Paint("#1C1C1E", "#F5F5F7");
        muted = Paint("#86868B", "#98989D");
        subtle = Paint("#AEAEB2", "#636366");
        rule = Paint("#D7D7D7", "#505054");
        hover = Paint("#F2F2F7", "#323236");
        red = Paint("#FF3B30", "#FF453A");
        borderBrush = night ? Palette.Alpha("#FFFFFF", 28) : Palette.Alpha("#000000", 20);

        var surface = new Grid();
        surface.Children.Add(body);

        Root = new Border
        {
            Background = paper,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = variant == Variant.Month
                ? (detailedLunar ? new Thickness(11, 12, 11, 10) : new Thickness(13, 14, 13, 12))
                : new Thickness(18, 15, 18, 16),
            Child = surface
        };
        TextOptions.SetTextFormattingMode(Root, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(Root, TextRenderingMode.ClearType);

        switch (variant)
        {
            case Variant.Month: Month(month, detailedLunar); break;
            case Variant.Date: BigDate(); break;
            case Variant.Compact: Summary(false); break;
            case Variant.Summary: Summary(true); break;
            case Variant.Agenda: Agenda(); break;
            default: Timeline(variant == Variant.Week); break;
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var toolsChrome = new Border
        {
            Background = Palette.Alpha(night ? "#2C2C2E" : "#FFFFFF", 235),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            Margin = new Thickness(0, -4, -6, 0),
            Child = tools
        };

        if (variant == Variant.Month)
        {
            tools.Children.Add(Icon("‹", "上个月", () => moveMonth(-1)));
            tools.Children.Add(Icon("›", "下个月", () => moveMonth(1)));
        }
        tools.Children.Add(Icon("⋯", "日历菜单", showMenu));

        void Reveal() => toolsChrome.Opacity = toolsChrome.IsMouseOver || toolsChrome.IsKeyboardFocusWithin ? 1 : 0;
        toolsChrome.MouseEnter += (_, _) => Reveal();
        toolsChrome.MouseLeave += (_, _) => Reveal();
        toolsChrome.IsKeyboardFocusWithinChanged += (_, _) => Reveal();
        surface.Children.Add(toolsChrome);
    }

    private static readonly System.Windows.Markup.XmlLanguage ZhCn = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");
    private static readonly ControlTemplate SharedToolTipTemplate = CreateToolTipTemplate();
    private static readonly Dictionary<(double Radius, bool Highlight, bool Night), ControlTemplate> TapTemplateCache = new();

    private static ControlTemplate CreateToolTipTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var tpl = new ControlTemplate(typeof(ToolTip)) { VisualTree = presenter };
        tpl.Seal();
        return tpl;
    }

    private ControlTemplate GetTapTemplate(double radius, bool highlightBackground)
    {
        var key = (radius, highlightBackground, night);
        if (TapTemplateCache.TryGetValue(key, out var cached)) return cached;
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        frame.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        if (highlightBackground)
        {
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Control.BackgroundProperty, hover));
            template.Triggers.Add(over);
        }
        template.Seal();
        TapTemplateCache[key] = template;
        return template;
    }

    private Brush Paint(string light, string dark) => Palette.Color(night ? dark : light);

    private ToolTip MakeToolTip(string text)
    {
        var border = new Border
        {
            Background = Paint("#FFFFFF", "#2C2C2E"),
            BorderBrush = night ? Palette.Alpha("#FFFFFF", 36) : Palette.Alpha("#000000", 24),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 5, 9, 5),
            Child = Text(text, 12, ink)
        };
        return new ToolTip
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = border,
            Template = SharedToolTipTemplate
        };
    }

    private TextBlock Text(string value, double size, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = value,
        FontFamily = size >= 24 ? WidgetFonts.Display : WidgetFonts.Text,
        FontSize = size,
        Foreground = color ?? ink,
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Language = ZhCn
    };

    private Button Tap(UIElement child, string name, Action click, bool highlightBackground = true, double radius = 5)
    {
        var button = new Button
        {
            Content = child,
            ToolTip = MakeToolTip(name),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = GetTapTemplate(radius, highlightBackground)
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private Button Icon(string glyph, string name, Action act)
    {
        var text = Text(glyph, 15, muted);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var button = Tap(text, name, act, radius: 5);
        button.Width = 20;
        button.Height = 20;
        return button;
    }

    private static void Dock(DockPanel parent, UIElement child, Dock edge = System.Windows.Controls.Dock.Top)
    {
        DockPanel.SetDock(child, edge);
        parent.Children.Add(child);
    }

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal };
    private IEnumerable<Entry> Day(DateTime day) => entries.Where(e => e.On(day)).OrderByDescending(e => e.AllDay).ThenBy(e => e.Start);
    private string DayName(DateTime date) => date == now.Date.AddDays(1) ? "明天" : date.ToString("M月d日 dddd", Dates.Chinese);

    private StackPanel Caption(string title, string lunar, double size, bool current)
    {
        var row = Row();
        row.Children.Add(Text(title, size, current ? red : muted, current ? FontWeights.SemiBold : FontWeights.Medium));
        row.Children.Add(new Border
        {
            Width = 0.75,
            Height = size * 0.9,
            Background = rule,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(Text(lunar, size, muted));
        return row;
    }

    private void Month(DateTime month, bool detail)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var anchor = month.Year == now.Year && month.Month == now.Month ? now : first;
        var caption = Caption(month.ToString("M月"), Dates.LunarDate(anchor), detail ? 10.5 : 11, true);
        caption.Margin = new Thickness(detail ? 3 : 4, 0, 0, 0);
        Dock(body, caption);

        var weekdays = new UniformGrid { Columns = 7, Rows = 1, Margin = new Thickness(0, detail ? 3.5 : 6, 0, detail ? 2 : 3.5) };
        foreach (char d in "一二三四五六日")
        {
            var t = Text(d.ToString(), detail ? 9 : 9.5, d is '六' or '日' ? muted : ink, FontWeights.SemiBold);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            weekdays.Children.Add(t);
        }
        Dock(body, weekdays);

        var firstCell = Dates.GridStart(month);
        int count = (first - firstCell).Days + DateTime.DaysInMonth(month.Year, month.Month);
        int weeks = detail ? 6 : Math.Max(5, (count + 6) / 7);
        var grid = new UniformGrid
        {
            Columns = 7,
            Rows = weeks,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        for (int i = 0; i < weeks * 7; i++)
        {
            var date = firstCell.AddDays(i);
            if (!detail && date.Month != month.Month) { grid.Children.Add(new Border()); continue; }
            var (lm, ld) = Dates.Parts(date);
            bool today = date == now.Date;
            bool weekend = i % 7 >= 5;
            var cell = new Grid();

            if (detail)
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var num = Text(date.Day.ToString(), 9.5, today ? Brushes.White : weekend ? muted : ink, FontWeights.SemiBold);
                num.HorizontalAlignment = HorizontalAlignment.Center;
                num.Margin = new Thickness(0, -0.5, 0, 0);
                TightLines(num, 11);

                var circle = new Grid { Width = 16.5, Height = 12.5, HorizontalAlignment = HorizontalAlignment.Center };
                if (today) circle.Children.Add(new Border { Background = red, CornerRadius = new CornerRadius(6.25) });
                circle.Children.Add(num);
                stack.Children.Add(circle);

                var lunarLabel = date.Day == 1 ? lm : ld;
                var lunar = Text(lunarLabel, 6.8, ld == "初一" ? red : muted, ld == "初一" ? FontWeights.Medium : FontWeights.Normal);
                lunar.HorizontalAlignment = HorizontalAlignment.Center;
                TightLines(lunar, 8);
                stack.Children.Add(lunar);
                cell.Children.Add(stack);
            }
            else
            {
                double circleSize = weeks == 6 ? 18.5 : 19.5;
                double numSize = weeks == 6 ? 10.5 : 11;
                var num = Text(date.Day.ToString(), numSize, today ? Brushes.White : weekend ? muted : ink, FontWeights.SemiBold);
                num.HorizontalAlignment = HorizontalAlignment.Center;
                num.VerticalAlignment = VerticalAlignment.Center;
                num.Margin = new Thickness(0, -0.5, 0, 0);

                var circle = new Grid { Width = circleSize, Height = circleSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (today) circle.Children.Add(new Ellipse { Fill = red });
                circle.Children.Add(num);
                if (ld == "初一" && !today)
                {
                    circle.Children.Add(new Border
                    {
                        Background = red,
                        Height = 1.2,
                        Width = 11.5,
                        CornerRadius = new CornerRadius(0.6),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 0.5)
                    });
                }
                cell.Children.Add(circle);
            }

            var calendars = Day(date).Select(e => e.Source).DistinctBy(s => s.Scope).ToList();
            if (showMonthDots && calendars.Count > 0 && !detail)
            {
                var dots = Row();
                dots.HorizontalAlignment = HorizontalAlignment.Center;
                dots.VerticalAlignment = VerticalAlignment.Bottom;
                foreach (var source in calendars.Take(3))
                    dots.Children.Add(new Ellipse { Width = 2.6, Height = 2.6, Fill = ResolveAccentBrush(source.Accent), Margin = new Thickness(0.5, 0, 0.5, 0) });
                cell.Children.Add(dots);
            }

            string names = string.Join("、", calendars.Select(s => s.Name));
            var button = Tap(cell, date.ToString("M月d日") + " " + Dates.LunarDate(date) + (calendars.Count > 0 ? " · " + names : ""), () => openDay(date), radius: 5);
            button.Opacity = date.Month == month.Month ? 1 : 0.36;
            grid.Children.Add(button);
        }
        body.Children.Add(grid);
    }

    private static void TightLines(TextBlock label, double height)
    {
        label.LineHeight = height;
        label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    private StackPanel DateMark()
    {
        var row = Row();
        row.Height = 38;
        var num = Text(now.Day.ToString(), 36, ink, FontWeights.Regular);
        num.Margin = new Thickness(-1, -3, 1, 0);
        row.Children.Add(num);

        var (_, day) = Dates.Parts(now);
        foreach (var (s, brush) in new[] { (day, muted), (Dates.Weekday(now), red) })
        {
            var glyphs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (char c in s)
            {
                var glyph = Text(c.ToString(), 10.5, brush, FontWeights.Medium);
                glyph.TextTrimming = TextTrimming.None;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyphs.Children.Add(new Grid { Height = 13, Children = { glyph } });
            }
            row.Children.Add(new Border
            {
                BorderBrush = rule,
                BorderThickness = new Thickness(0.7, 0, 0, 0),
                Padding = new Thickness(5, 0, 0, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = glyphs
            });
        }
        return row;
    }

    private void BigDate()
    {
        var group = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = Row();
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Children.Add(Text(Dates.Weekday(now), 21, red, FontWeights.SemiBold));
        heading.Children.Add(Text(" " + now.ToString("M月"), 21, muted, FontWeights.SemiBold));
        group.Children.Add(heading);

        var line = Row();
        line.Margin = new Thickness(-1, 12, 0, 0);
        var numeral = new FormattedText(now.Day.ToString(), Dates.Chinese, FlowDirection.LeftToRight,
            new Typeface(WidgetFonts.Display, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            100, ink, 1);
        var outline = numeral.BuildGeometry(new Point(0, 0));
        const double capHeight = 72;
        var digits = new Path
        {
            Data = outline,
            Fill = ink,
            Stroke = ink,
            StrokeThickness = 2.4,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Fill,
            Width = 0.97 * capHeight * outline.Bounds.Width / outline.Bounds.Height,
            Height = capHeight,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(0, -5)
        };
        AutomationProperties.SetName(digits, now.Day.ToString());
        line.Height = 86;
        line.Children.Add(digits);

        var lunar = Text(string.Join("\n", Dates.LunarDate(now).ToCharArray()), 17.5, ink, FontWeights.SemiBold);
        TightLines(lunar, Dates.LunarDate(now).Length > 4 ? 15 : 18);
        lunar.Margin = new Thickness(4, 0, 0, 0);
        lunar.RenderTransform = new TranslateTransform(0, -2.5);
        line.Children.Add(lunar);
        group.Children.Add(line);
        body.Children.Add(group);
    }
}
