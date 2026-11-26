using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinMacCalendar;

internal static class Dialogs
{
    internal sealed record Edit(
        string Title,
        DateTime Start,
        DateTime End,
        bool AllDay,
        bool Delete = false,
        CalendarSource? Destination = null,
        string? Location = null,
        string? Description = null);

    public static void Alert(Window owner, string title, string message, bool dark = false)
    {
        var panel = new StackPanel();
        var window = Shell(owner, title, 360, panel, dark);
        var body = Label(message, 13.5, "FgPrimaryBrush");
        body.Margin = new Thickness(2, 4, 2, 18);
        panel.Children.Add(body);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = ActionButton(window, "好", window.Close, "PrimaryButton");
        ok.IsDefault = true;
        bar.Children.Add(ok);
        panel.Children.Add(bar);
        window.ShowDialog();
    }

    public static bool Confirm(Window owner, string title, string message, string confirmLabel = "确定", bool danger = false, bool dark = false)
    {
        bool confirmed = false;
        var panel = new StackPanel();
        var window = Shell(owner, title, 360, panel, dark);
        var body = Label(message, 13.5, "FgPrimaryBrush");
        body.Margin = new Thickness(2, 4, 2, 18);
        panel.Children.Add(body);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(ActionButton(window, "取消", window.Close, "ModernButton"));
        var yes = ActionButton(window, confirmLabel, () => { confirmed = true; window.DialogResult = true; }, danger ? "DangerButton" : "PrimaryButton");
        bar.Children.Add(yes);
        panel.Children.Add(bar);
        window.ShowDialog();
        return confirmed;
    }

    public static int Choice(Window owner, string title, string message, string option1, string option2, bool dark = false)
    {
        int picked = 0;
        var panel = new StackPanel();
        var window = Shell(owner, title, 400, panel, dark);
        var body = Label(message, 13.5, "FgPrimaryBrush");
        body.Margin = new Thickness(2, 4, 2, 18);
        panel.Children.Add(body);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(ActionButton(window, "取消", window.Close, "ModernButton"));
        bar.Children.Add(ActionButton(window, option2, () => { picked = 2; window.DialogResult = true; }, "ModernButton"));
        bar.Children.Add(ActionButton(window, option1, () => { picked = 1; window.DialogResult = true; }, "PrimaryButton"));
        panel.Children.Add(bar);
        window.ShowDialog();
        return picked;
    }

    private static Brush ResolveDotBrush(string hex, bool dark)
    {
        var c = ((SolidColorBrush)Palette.Color(hex)).Color;
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        if (dark && lum < 0.20) return Palette.Color("#A1A1A6");
        return Palette.Color(hex);
    }

    private static Window Shell(Window owner, string title, double width, UIElement content, bool dark)
    {
        bool ownerIsDesktopWidget = owner is MainWindow or WidgetWindow;
        var window = new Window
        {
            Owner = ownerIsDesktopWidget ? null : owner,
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = WidgetFonts.Ui,
            FontSize = 13.5,
            SnapsToDevicePixels = true
        };
        try
        {
            window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/WinMacCalendar;component/Resources/Calendar.ico", UriKind.Absolute));
        }
        catch { }
        ThemeResources.Apply(window, dark);
        window.SetResourceReference(Control.BackgroundProperty, "BgWindowBrush");
        window.SetResourceReference(Control.ForegroundProperty, "FgPrimaryBrush");
        var contentWrap = new Border { Padding = new Thickness(20, 14, 20, 20), Child = content };
        contentWrap.SetResourceReference(Border.BackgroundProperty, "BgWindowBrush");
        window.Content = contentWrap;
        Desktop.StyleDialog(window, dark, ThemeResources.WindowCaptionColor(dark));
        window.Loaded += (_, _) =>
        {
            window.Activate();
            Desktop.BringToForeground(window);
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; window.Close(); }
        };
        return window;
    }

    private static Button ActionButton(Window host, string caption, Action action, string styleKey = "ModernButton")
    {
        var b = new Button { Content = caption, Margin = new Thickness(8, 0, 0, 0), MinWidth = 74 };
        b.Style = (Style)host.FindResource(styleKey);
        b.Click += (_, _) => action();
        return b;
    }

    private static Border Card(Window host, UIElement child, Thickness? padding = null, Thickness? margin = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(14, 10, 14, 10),
            Margin = margin ?? new Thickness(0, 0, 0, 12),
            Child = child
        };
        border.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        return border;
    }

    private static Border Divider(Window host, Thickness? margin = null)
    {
        var line = new Border { Height = 1, Margin = margin ?? new Thickness(0, 9, 0, 9) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderSubtleBrush");
        return line;
    }

    private static TextBlock Label(string text, double size = 12, string brushKey = "FgMutedBrush", FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    internal static StackPanel SourceLabel(string text, string accent, bool dark = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = ResolveDotBrush(accent, dark),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static FrameworkElement InfoRow(string caption, string primary, string? secondary = null, bool copyable = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = Label(caption, 12.5, "FgMutedBrush", FontWeights.Medium);
        left.VerticalAlignment = VerticalAlignment.Top;
        left.Margin = new Thickness(0, 1, 8, 0);
        grid.Children.Add(left);

        var right = new StackPanel();
        var main = Label(primary, 13.5, "FgPrimaryBrush", FontWeights.Medium);
        right.Children.Add(main);
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            var sub = Label(secondary, 12, "FgMutedBrush");
            sub.Margin = new Thickness(0, 2, 0, 0);
            right.Children.Add(sub);
        }
        if (copyable)
        {
            right.Cursor = Cursors.Hand;
            right.ToolTip = "点击复制";
            right.MouseLeftButtonUp += (_, _) =>
            {
                try { Clipboard.SetText(primary); } catch { }
            };
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static string FormatInspectorDate(Entry ev)
    {
        if (ev.AllDay)
        {
            var last = ev.End.Date.AddDays(-1);
            if (last <= ev.Start.Date)
                return $"{ev.Start:yyyy年M月d日 dddd} · {Dates.LunarDate(ev.Start)}";
            return $"{ev.Start:yyyy年M月d日} 至 {last:M月d日}";
        }
        if (ev.Start.Date == ev.End.Date)
            return $"{ev.Start:yyyy年M月d日 dddd} · {Dates.LunarDate(ev.Start)}";
        return $"{ev.Start:M月d日 HH:mm} – {ev.End:M月d日 HH:mm}";
    }

    public static Edit? Event(Window owner, DateTime day, Entry? ev, IReadOnlyList<CalendarSource> destinations, bool dark = false)
    {
        bool canWrite = ev == null ? destinations.Count > 0 : ev.Editable && ev.Source.Writable;
        if (ev != null && !canWrite)
        {
            ShowReadOnlyEvent(owner, ev, dark);
            return null;
        }
        return ShowEditableEvent(owner, day, ev, destinations, dark);
    }

    private static void ShowReadOnlyEvent(Window owner, Entry ev, bool dark)
    {
        BuildReadOnlyEventWindow(owner, ev, dark).ShowDialog();
    }

    internal static Window BuildReadOnlyEventWindow(Window owner, Entry ev, bool dark)
    {
        var panel = new StackPanel();
        var window = Shell(owner, "日程详情", 386, panel, dark);

        // Top badge + calendar chip
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 9, 3),
            BorderThickness = new Thickness(1)
        };
        chip.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        var chipContent = SourceLabel(ev.Source.Name, ev.Source.Accent, dark);
        chip.Child = chipContent;
        headerRow.Children.Add(chip);

        var badgeText = ev.Source.Writable ? "重复日程 · 只读" : "只读日历";
        var roBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0)
        };
        roBadge.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
        roBadge.Child = Label(badgeText, 11.5, "FgMutedBrush", FontWeights.Medium);
        headerRow.Children.Add(roBadge);
        panel.Children.Add(headerRow);

        // Hero title
        var titleBlock = Label(ev.Title, 19, "FgPrimaryBrush", FontWeights.SemiBold);
        titleBlock.Margin = new Thickness(2, 0, 2, 14);
        panel.Children.Add(titleBlock);

        // Details card
        var details = new StackPanel();
        var dateLine = FormatInspectorDate(ev);
        string? timeSub = ev.AllDay
            ? "全天日程"
            : ev.Start.Date == ev.End.Date
                ? $"{ev.Start:HH:mm} – {ev.End:HH:mm}"
                : null;
        details.Children.Add(InfoRow("时间", dateLine, timeSub));

        if (!ev.AllDay && ev.Travel > TimeSpan.Zero)
        {
            details.Children.Add(Divider(window));
            details.Children.Add(InfoRow("行程", $"行程时间 {(int)ev.Travel.TotalMinutes} 分钟", $"建议 {(ev.Start - ev.Travel):HH:mm} 出发"));
        }

        if (!string.IsNullOrWhiteSpace(ev.RecurrenceSummary))
        {
            details.Children.Add(Divider(window));
            details.Children.Add(InfoRow("重复", ev.RecurrenceSummary));
        }

        if (!string.IsNullOrWhiteSpace(ev.Location))
        {
            details.Children.Add(Divider(window));
            details.Children.Add(InfoRow("地点", ev.Location, copyable: true));
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            details.Children.Add(Divider(window));
            var descScroll = new ScrollViewer { MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            descScroll.Content = Label(ev.Description, 12.5, "FgSecondaryBrush");
            var descRow = new Grid();
            descRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            descRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var descCap = Label("备注", 12.5, "FgMutedBrush", FontWeights.Medium);
            descCap.VerticalAlignment = VerticalAlignment.Top;
            descRow.Children.Add(descCap);
            Grid.SetColumn(descScroll, 1);
            descRow.Children.Add(descScroll);
            details.Children.Add(descRow);
        }

        panel.Children.Add(Card(window, details, new Thickness(14, 12, 14, 12), new Thickness(0, 0, 0, 14)));

        // Footer
        var footer = new DockPanel();
        var closeBtn = ActionButton(window, "关闭", window.Close, "PrimaryButton");
        closeBtn.IsDefault = true;
        DockPanel.SetDock(closeBtn, Dock.Right);
        footer.Children.Add(closeBtn);

        var hint = Label(ev.Source.Writable ? "重复日程请在 Apple 日历中修改" : "订阅或共享日历不支持在此编辑", 11.5, "FgMutedBrush");
        hint.Margin = new Thickness(2, 0, 12, 0);
        footer.Children.Add(hint);
        panel.Children.Add(footer);

        return window;
    }

    private static Edit? ShowEditableEvent(Window owner, DateTime day, Entry? ev, IReadOnlyList<CalendarSource> destinations, bool dark)
    {
        var panel = new StackPanel();
        var window = Shell(owner, ev == null ? "新建日程" : "编辑日程", 408, panel, dark);
        Edit? result = null;

        // Section 1: Title & Location
        var title = new TextBox { Text = ev?.Title ?? "", MaxLength = 1000, FontSize = 14.5, FontWeight = FontWeights.Medium };
        var location = new TextBox { Text = ev?.Location ?? "", MaxLength = 500, Margin = new Thickness(0, 8, 0, 0) };

        var basicStack = new StackPanel();
        basicStack.Children.Add(Label("标题", 12, "FgMutedBrush", FontWeights.Medium));
        title.Margin = new Thickness(0, 5, 0, 0);
        basicStack.Children.Add(title);
        var locCap = Label("地点（选填）", 12, "FgMutedBrush", FontWeights.Medium);
        locCap.Margin = new Thickness(0, 10, 0, 0);
        basicStack.Children.Add(locCap);
        location.Margin = new Thickness(0, 5, 0, 0);
        basicStack.Children.Add(location);
        panel.Children.Add(Card(window, basicStack, new Thickness(14, 12, 14, 12)));

        // Section 2: Date & Time
        DateTime start = ev?.Start ?? day.AddHours(day == DateTime.Today ? Math.Min(23, DateTime.Now.Hour + 1) : 9);
        DateTime end = ev?.End ?? start.AddHours(1);

        var timeStack = new StackPanel();
        var all = new CheckBox
        {
            Content = "全天日程",
            IsChecked = ev?.AllDay ?? false,
            Style = (Style)window.FindResource("SwitchCheckBox")
        };
        timeStack.Children.Add(all);
        timeStack.Children.Add(Divider(window, new Thickness(0, 10, 0, 10)));

        var starts = new DatePicker { SelectedDate = start.Date };
        var ends = new DatePicker { SelectedDate = all.IsChecked == true ? end.Date.AddDays(-1) : end.Date };
        var st = new TextBox { Text = start.ToString("HH:mm"), Width = 84, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var et = new TextBox { Text = end.ToString("HH:mm"), Width = 84, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };

        Grid TimeRow(string caption, DatePicker picker, TextBox clock)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Label(caption, 13, "FgSecondaryBrush", FontWeights.Medium));
            Grid.SetColumn(picker, 1);
            row.Children.Add(picker);
            Grid.SetColumn(clock, 2);
            row.Children.Add(clock);
            return row;
        }

        timeStack.Children.Add(TimeRow("开始", starts, st));
        var endRow = TimeRow("结束", ends, et);
        endRow.Margin = new Thickness(0, 8, 0, 0);
        timeStack.Children.Add(endRow);
        panel.Children.Add(Card(window, timeStack, new Thickness(14, 11, 14, 12)));

        void Toggle() => st.Visibility = et.Visibility = all.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        all.Checked += (_, _) => Toggle();
        all.Unchecked += (_, _) => Toggle();
        Toggle();

        // Section 3: Calendar & Description
        var metaStack = new StackPanel();
        var destination = new ComboBox();
        if (ev != null)
        {
            var calRow = new DockPanel();
            var cap = Label("所属日历", 13, "FgSecondaryBrush", FontWeights.Medium);
            DockPanel.SetDock(cap, Dock.Left);
            calRow.Children.Add(cap);
            var srcLabel = SourceLabel(ev.Source.Name, ev.Source.Accent, dark);
            srcLabel.HorizontalAlignment = HorizontalAlignment.Right;
            calRow.Children.Add(srcLabel);
            metaStack.Children.Add(calRow);
        }
        else
        {
            foreach (var source in destinations)
                destination.Items.Add(new ComboBoxItem { Content = SourceLabel(source.Name, source.Accent, dark), Tag = source });
            destination.SelectedIndex = 0;
            metaStack.Children.Add(Label("保存到日历", 12, "FgMutedBrush", FontWeights.Medium));
            destination.Margin = new Thickness(0, 5, 0, 0);
            metaStack.Children.Add(destination);
        }

        metaStack.Children.Add(Divider(window, new Thickness(0, 10, 0, 10)));
        metaStack.Children.Add(Label("备注（选填）", 12, "FgMutedBrush", FontWeights.Medium));
        var desc = new TextBox
        {
            Text = ev?.Description ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0),
            MaxLength = 4000
        };
        metaStack.Children.Add(desc);
        panel.Children.Add(Card(window, metaStack, new Thickness(14, 11, 14, 12)));

        // Validation error banner
        var errorBanner = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed
        };
        errorBanner.SetResourceReference(Border.BackgroundProperty, "DangerBgBrush");
        var errorText = Label("", 12.5, "DangerBrush", FontWeights.Medium);
        errorBanner.Child = errorText;
        panel.Children.Add(errorBanner);

        void ShowError(string msg)
        {
            errorText.Text = msg;
            errorBanner.Visibility = Visibility.Visible;
        }

        // Action Bar
        var bar = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (ev != null)
        {
            var del = ActionButton(window, "删除日程", () =>
            {
                if (!Confirm(window, "删除日程", "确定删除「" + ev.Title + "」？", "删除", danger: true, dark: dark))
                    return;
                result = new(ev.Title, ev.Start, ev.End, ev.AllDay, Delete: true);
                window.DialogResult = true;
            }, "DangerButton");
            del.Margin = new Thickness(0);
            DockPanel.SetDock(del, Dock.Left);
            bar.Children.Add(del);
        }

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBtns.Children.Add(ActionButton(window, "取消", window.Close, "ModernButton"));
        var saveBtn = ActionButton(window, "保存", () =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || starts.SelectedDate is not { } a || ends.SelectedDate is not { } b)
            {
                ShowError("请填写日程标题、开始日期和结束日期。");
                return;
            }
            bool allDay = all.IsChecked == true;
            if (allDay) b = b.Date.AddDays(1);
            else
            {
                if (!TimeOnly.TryParseExact(st.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)
                    || !TimeOnly.TryParseExact(et.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var e))
                {
                    ShowError("时间格式请使用 24 小时制，例如 09:30。");
                    return;
                }
                a = a.Date + s.ToTimeSpan();
                b = b.Date + e.ToTimeSpan();
            }
            if (b <= a) { ShowError("结束时间必须晚于开始时间。"); return; }
            var target = ev?.Source ?? (destination.SelectedItem as ComboBoxItem)?.Tag as CalendarSource;
            if (target == null) { ShowError("请选择保存日历。"); return; }
            result = new(title.Text.Trim(), a, b, allDay, Destination: target, Location: location.Text, Description: desc.Text);
            window.DialogResult = true;
        }, "PrimaryButton");
        rightBtns.Children.Add(saveBtn);
        bar.Children.Add(rightBtns);
        panel.Children.Add(bar);

        window.Loaded += (_, _) => title.Focus();
        window.ShowDialog();
        return result;
    }

    public static void Day(Window owner, DateTime date, IReadOnlyList<Entry> events, bool writable, Action<Entry?> select, bool dark = false)
    {
        var panel = new StackPanel();
        var window = Shell(owner, date.ToString("M月d日 dddd", Dates.Chinese), 380, panel, dark);

        // Header
        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 12) };
        var countChip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        countChip.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
        countChip.Child = Label(events.Count == 0 ? "无日程" : $"{events.Count} 个日程", 11.5, "FgMutedBrush", FontWeights.Medium);
        DockPanel.SetDock(countChip, Dock.Right);
        header.Children.Add(countChip);

        var titleStack = new StackPanel();
        titleStack.Children.Add(Label(date.ToString("M月d日 dddd", Dates.Chinese), 17, "FgPrimaryBrush", FontWeights.SemiBold));
        var lunar = Label(Dates.LunarDate(date), 12.5, "FgMutedBrush");
        lunar.Margin = new Thickness(0, 2, 0, 0);
        titleStack.Children.Add(lunar);
        header.Children.Add(titleStack);
        panel.Children.Add(header);

        // Event list
        var list = new StackPanel();
        if (events.Count == 0)
        {
            var emptyBox = new Border { Padding = new Thickness(0, 28, 0, 28) };
            var emptyText = Label("当天暂无安排的日程", 13.5, "FgMutedBrush");
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyBox.Child = emptyText;
            list.Children.Add(Card(window, emptyBox, new Thickness(12), new Thickness(0, 0, 0, 12)));
        }
        else
        {
            var itemsPanel = new StackPanel();
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var bar = new Border
                {
                    Width = 3.5,
                    CornerRadius = new CornerRadius(2),
                    Background = ResolveDotBrush(ev.Source.Accent, dark),
                    Margin = new Thickness(0, 2, 10, 2)
                };
                row.Children.Add(bar);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(Label(ev.Title, 13.5, "FgPrimaryBrush", FontWeights.SemiBold));
                string meta = ev.TimeRange;
                if (!ev.AllDay && ev.Travel > TimeSpan.Zero)
                    meta += $" · 行程 {(int)ev.Travel.TotalMinutes} 分钟";
                if (!string.IsNullOrWhiteSpace(ev.Location))
                    meta += " · " + ev.Location;
                var metaText = Label(meta, 12, "FgMutedBrush");
                metaText.Margin = new Thickness(0, 2, 0, 0);
                info.Children.Add(metaText);
                Grid.SetColumn(info, 1);
                row.Children.Add(info);

                var srcTag = Label(ev.Source.Name, 11.5, "FgMutedBrush");
                srcTag.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(srcTag, 2);
                row.Children.Add(srcTag);

                var btn = new Button
                {
                    Content = row,
                    ToolTip = ev.Source.Name,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, i == events.Count - 1 ? 0 : 7),
                    Padding = new Thickness(11, 9, 12, 9),
                    Cursor = Cursors.Hand
                };
                var frame = new FrameworkElementFactory(typeof(Border));
                frame.Name = "itemBd";
                frame.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgCardBrush"));
                frame.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderSubtleBrush"));
                frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
                frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                frame.AppendChild(presenter);
                var tpl = new ControlTemplate(typeof(Button)) { VisualTree = frame };
                var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush"), "itemBd"));
                tpl.Triggers.Add(hoverTrigger);
                btn.Template = tpl;
                btn.Click += (_, _) => { window.Close(); select(ev); };
                itemsPanel.Children.Add(btn);
            }
            list.Children.Add(new ScrollViewer
            {
                Content = itemsPanel,
                MaxHeight = 330,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 12)
            });
        }
        panel.Children.Add(list);

        // Footer
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(ActionButton(window, "关闭", window.Close, "ModernButton"));
        if (writable)
            footer.Children.Add(ActionButton(window, "新建日程", () => { window.Close(); select(null); }, "PrimaryButton"));
        panel.Children.Add(footer);

        window.ShowDialog();
    }

    public static Account? Connect(Window owner, Account? account, bool dark = false)
    {
        var panel = new StackPanel();
        var window = Shell(owner, account == null ? "连接 iCloud 日历" : "iCloud 账户与日历", 420, panel, dark);

        var intro = Label("使用 Apple 账户邮箱与 App 专用密码通过 CalDAV 直连 iCloud（密码仅由 Windows DPAPI 本机加密存储）。", 12, "FgMutedBrush");
        intro.Margin = new Thickness(2, 0, 2, 12);
        panel.Children.Add(intro);

        var credStack = new StackPanel();
        credStack.Children.Add(Label("Apple 账户邮箱", 12, "FgMutedBrush", FontWeights.Medium));
        var email = new TextBox { Text = account?.Email ?? "", Margin = new Thickness(0, 5, 0, 10) };
        credStack.Children.Add(email);

        credStack.Children.Add(Label(account == null ? "App 专用密码（可在 account.apple.com 生成）" : "App 专用密码（留空则沿用已保存密码）", 12, "FgMutedBrush", FontWeights.Medium));
        var password = new PasswordBox { Margin = new Thickness(0, 5, 0, 0) };
        credStack.Children.Add(password);
        panel.Children.Add(Card(window, credStack, new Thickness(14, 12, 14, 12)));

        var calCardStack = new StackPanel();
        calCardStack.Children.Add(Label("同步日历（可多选）", 12, "FgMutedBrush", FontWeights.Medium));
        var choices = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        calCardStack.Children.Add(new ScrollViewer
        {
            Content = choices,
            MaxHeight = 195,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        panel.Children.Add(Card(window, calCardStack, new Thickness(14, 11, 14, 11)));

        var feedback = Label("", 12.5, "FgMutedBrush");
        feedback.Margin = new Thickness(2, 0, 2, 12);
        panel.Children.Add(feedback);

        using var cancel = new CancellationTokenSource();
        Account? result = null;
        string? verifiedSecret = null;

        var bar = new DockPanel();
        var verify = ActionButton(window, account == null ? "验证账户" : "刷新日历列表", () => { }, "ModernButton");
        verify.Margin = new Thickness(0);
        DockPanel.SetDock(verify, Dock.Left);
        bar.Children.Add(verify);

        var rightBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBar.Children.Add(ActionButton(window, "取消", window.Close, "ModernButton"));
        var save = ActionButton(window, "保存选择", () =>
        {
            var selected = choices.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (CloudCalendar)b.Tag).ToList();
            if (verifiedSecret == null || selected.Count == 0) return;
            result = new(email.Text.Trim(), verifiedSecret, selected);
            window.DialogResult = true;
        }, "PrimaryButton");
        rightBar.Children.Add(save);
        bar.Children.Add(rightBar);
        panel.Children.Add(bar);

        void UpdateSave() => save.IsEnabled = verifiedSecret != null && choices.Children.OfType<CheckBox>().Any(b => b.IsChecked == true);

        void Populate(IEnumerable<CloudCalendar> found, HashSet<string> selected)
        {
            choices.Children.Clear();
            foreach (var calendar in found.DistinctBy(c => c.Url))
            {
                var box = new CheckBox
                {
                    Tag = calendar,
                    Content = SourceLabel(calendar.ToString(), CalendarSource.ColorOf(calendar), dark),
                    IsChecked = selected.Contains(calendar.Url),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                box.Checked += (_, _) => UpdateSave();
                box.Unchecked += (_, _) => UpdateSave();
                choices.Children.Add(box);
            }
            UpdateSave();
        }

        void Invalidate()
        {
            verifiedSecret = null;
            choices.Children.Clear();
            UpdateSave();
            feedback.Text = "验证账户后可勾选需要同步的 iCloud 日历。";
        }

        Invalidate();
        email.TextChanged += (_, _) => Invalidate();
        password.PasswordChanged += (_, _) => Invalidate();

        async System.Threading.Tasks.Task Discover()
        {
            var user = email.Text.Trim();
            bool same = account != null && string.Equals(user, account.Email, StringComparison.OrdinalIgnoreCase);
            var selected = choices.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => ((CloudCalendar)b.Tag).Url).ToHashSet();
            if (choices.Children.Count == 0 && same) selected = account!.Calendars.Select(c => c.Url).ToHashSet();
            if (user == "" || (!same && password.Password.Trim() == ""))
            {
                feedback.Text = "请输入 Apple 账户邮箱和 App 专用密码。";
                return;
            }
            email.IsEnabled = password.IsEnabled = verify.IsEnabled = choices.IsEnabled = save.IsEnabled = false;
            feedback.Text = "正在连接 iCloud 并读取日历列表…";
            try
            {
                var secret = password.Password.Trim();
                if (secret == "" && same) secret = account!.Password();
                using var cloud = new ICloud(user, secret);
                var found = await cloud.Discover(cancel.Token);
                if (cancel.IsCancellationRequested) return;
                verifiedSecret = Account.Protect(secret);
                if (!same && choices.Children.Count == 0 && found.Count > 0) selected.Add(found[0].Url);
                Populate(found, selected);
                feedback.Text = found.Count == 0 ? "此账户下没有可用的日历。" : "请勾选要显示的日历，然后点击「保存选择」。";
            }
            catch (Exception ex) when (!cancel.IsCancellationRequested)
            {
                feedback.Text = "连接失败 · " + ex.Message;
            }
            catch (OperationCanceledException) { }
            finally
            {
                email.IsEnabled = password.IsEnabled = verify.IsEnabled = choices.IsEnabled = true;
                UpdateSave();
            }
        }

        if (account != null)
        {
            verifiedSecret = account.Secret;
            Populate(account.Calendars, account.Calendars.Select(c => c.Url).ToHashSet());
            window.Loaded += async (_, _) => await Discover();
        }
        verify.Click += async (_, _) => await Discover();
        window.Closed += (_, _) => cancel.Cancel();
        window.ShowDialog();
        return result;
    }
}
