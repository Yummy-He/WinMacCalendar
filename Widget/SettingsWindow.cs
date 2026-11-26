using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinMacCalendar;

internal sealed class SettingsHost
{
    public required Func<IReadOnlyList<WidgetInstance>> GetWidgets { get; init; }
    public required Func<Variant, WidgetInstance> AddWidget { get; init; }
    public required Action<string> RemoveWidget { get; init; }
    public required Action<string, Action<WidgetInstance>, bool> UpdateWidget { get; init; }
    public required Func<bool> GetUniformScale { get; init; }
    public required Action<bool> SetUniformScale { get; init; }
    public required Func<double> GetGlobalScale { get; init; }
    public required Action<double> SetGlobalScale { get; init; }
    public required Func<bool> GetSnapToGrid { get; init; }
    public required Action<bool> SetSnapToGrid { get; init; }
    public required Func<WidgetInstance, (int Col, int Row)> GetGridCell { get; init; }
    public required Func<double> GetGridStepPixels { get; init; }
    public required Func<double> GetGridGapPixels { get; init; }
    public required Action<string> SnapWidgetToGrid { get; init; }
    public required Action SnapAllToGrid { get; init; }
    public required Action<string, int, int> NudgeWidgetOnGrid { get; init; }
    public required Func<ThemePreference> GetTheme { get; init; }
    public required Action<ThemePreference> SetTheme { get; init; }
    public required Func<TravelDisplayMode> GetTravelMode { get; init; }
    public required Action<TravelDisplayMode> SetTravelMode { get; init; }
    public required Func<bool> GetMonthDots { get; init; }
    public required Action<bool> SetMonthDots { get; init; }
    public required Func<double> GetOpacity { get; init; }
    public required Action<double> SetOpacity { get; init; }
    public required Func<Account?> GetAccount { get; init; }
    public required Func<string> GetSyncText { get; init; }
    public required Func<IReadOnlyDictionary<string, int>> GetReadErrors { get; init; }
    public required Func<IEnumerable<(CalendarSource Source, bool Visible)>> GetSources { get; init; }
    public required Action<CalendarSource, bool> SetSourceVisible { get; init; }
    public required Func<Window, bool> ConnectAccount { get; init; }
    public required Action DisconnectAccount { get; init; }
    public required Func<System.Threading.Tasks.Task> SyncNow { get; init; }
    public required Action ResetAllPositions { get; init; }
    public required Action GoToday { get; init; }
    public required Func<string> GetDataFolder { get; init; }
    public required Func<Window, string, System.Threading.Tasks.Task<bool>> ChangeDataFolder { get; init; }
}

internal sealed class SettingsWindow : Window
{
    private readonly SettingsHost host;
    private readonly StackPanel navPanel = new();
    private readonly ContentControl pageHost = new();
    private readonly Dictionary<string, Action<WidgetInstance>> liveCardUpdaters = new();
    private int activeTab;
    private bool isDark;
    private bool updatingLiveFields;
    private Variant newWidgetVariant = Variant.Summary;

    private static readonly (string Title, string Subtitle)[] VariantMeta =
    [
        ("月历", "紧凑月历网格"),
        ("日程列表", "多日完整日程流"),
        ("紧凑摘要", "今日单栏精选"),
        ("大字日期", "极简大号日历"),
        ("双栏摘要", "今日 + 近期日程"),
        ("双日时间线", "今明两天时间轴"),
        ("四日时间线", "宽屏多日时间轴")
    ];

    public SettingsWindow(Window? owner, SettingsHost host)
    {
        this.host = host;
        // Do NOT bind Owner to a desktop-bottom widget window, otherwise Windows keeps the
        // settings window trapped behind other apps alongside the desktop widget.
        Title = "桌面日历设置";
        Width = 710;
        Height = 560;
        MinWidth = 650;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        SnapsToDevicePixels = true;
        try
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/WinMacCalendar;component/Resources/Calendar.ico", UriKind.Absolute));
        }
        catch { }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };

        BuildShell();
        ApplyThemeAndRefresh();
    }

    public void NotifyWidgetsChanged()
    {
        if (activeTab != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            var list = host.GetWidgets();
            if (list.Count != liveCardUpdaters.Count || list.Any(w => !liveCardUpdaters.ContainsKey(w.Id)))
            {
                RenderActivePage();
                return;
            }
            updatingLiveFields = true;
            try
            {
                foreach (var w in list)
                    if (liveCardUpdaters.TryGetValue(w.Id, out var updater))
                        updater(w);
            }
            finally { updatingLiveFields = false; }
        });
    }

    private void BuildShell()
    {
        SetResourceReference(BackgroundProperty, "BgWindowBrush");
        SetResourceReference(ForegroundProperty, "FgPrimaryBrush");

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "BgWindowBrush");
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Sidebar
        var sidebar = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(12, 16, 12, 16) };
        sidebar.SetResourceReference(Border.BackgroundProperty, "BgSidebarBrush");
        sidebar.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

        var sideDock = new DockPanel();
        var appHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 18) };
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 9, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        badge.Child = new TextBlock
        {
            Text = DateTime.Today.Day.ToString(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        appHead.Children.Add(badge);

        var titleCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleCol.Children.Add(MakeText("桌面日历", 14, "FgPrimaryBrush", FontWeights.SemiBold));
        titleCol.Children.Add(MakeText("偏好设置", 11, "FgMutedBrush"));
        appHead.Children.Add(titleCol);
        DockPanel.SetDock(appHead, Dock.Top);
        sideDock.Children.Add(appHead);

        sideDock.Children.Add(navPanel);
        sidebar.Child = sideDock;
        root.Children.Add(sidebar);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 18, 24, 22),
            Content = pageHost
        };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);

        Content = root;
    }

    private void ApplyThemeAndRefresh()
    {
        isDark = Palette.ResolveDark(host.GetTheme());
        ThemeResources.Apply(this, isDark);
        Desktop.StyleDialog(this, isDark, ThemeResources.WindowCaptionColor(isDark));
        RenderNav();
        RenderActivePage();
    }

    private void RenderNav()
    {
        navPanel.Children.Clear();
        string[] tabs = ["小组件与布局", "外观与显示", "iCloud 与日历", "常规"];
        for (int i = 0; i < tabs.Length; i++)
        {
            int idx = i;
            navPanel.Children.Add(NavButton(tabs[i], idx == activeTab, () =>
            {
                activeTab = idx;
                Dispatcher.BeginInvoke(() =>
                {
                    RenderNav();
                    RenderActivePage();
                });
            }));
        }
    }

    private Button NavButton(string title, bool selected, Action click)
    {
        var btn = new Button
        {
            Content = title,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 4),
            FontSize = 13.5,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = Cursors.Hand
        };
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.Name = "bd";
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        frame.SetValue(Border.BackgroundProperty, selected
            ? new DynamicResourceExtension("AccentSoftBrush")
            : Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(cp);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        if (!selected)
        {
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush"), "bd"));
            tpl.Triggers.Add(hover);
        }
        btn.Template = tpl;
        btn.SetResourceReference(ForegroundProperty, selected ? "AccentBrush" : "FgPrimaryBrush");
        btn.Click += (_, _) => click();
        return btn;
    }

    private void RenderActivePage()
    {
        liveCardUpdaters.Clear();
        pageHost.Content = activeTab switch
        {
            0 => BuildWidgetsPage(),
            1 => BuildAppearancePage(),
            2 => BuildCloudPage(),
            _ => BuildGeneralPage()
        };
    }

    private TextBlock MakeText(string text, double size, string brushKey = "FgPrimaryBrush", FontWeight? weight = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    private Border SectionCard(UIElement child, Thickness? padding = null, Thickness? margin = null)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(15, 12, 15, 12),
            Margin = margin ?? new Thickness(0, 6, 0, 14),
            Child = child
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        return card;
    }

    private Border Divider(Thickness? margin = null)
    {
        var line = new Border { Height = 1, Margin = margin ?? new Thickness(0, 10, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderSubtleBrush");
        return line;
    }

    private UIElement SegmentedControl<T>((string Label, T Value)[] options, T current, Action<T> onSelect)
    {
        var outer = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
        outer.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
        var grid = new UniformGrid { Rows = 1, Columns = options.Length };
        foreach (var (label, value) in options)
        {
            bool active = EqualityComparer<T>.Default.Equals(value, current);
            var b = new Button
            {
                Content = label,
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 12,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = Cursors.Hand
            };
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            bd.SetValue(Border.BackgroundProperty, active ? new DynamicResourceExtension("BgCardBrush") : Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            b.SetResourceReference(ForegroundProperty, active ? "FgPrimaryBrush" : "FgMutedBrush");
            var val = value;
            b.Click += (_, _) => onSelect(val);
            grid.Children.Add(b);
        }
        outer.Child = grid;
        return outer;
    }

    private DockPanel SettingRow(string title, string? desc, UIElement control)
    {
        var row = new DockPanel();
        control.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(MakeText(title, 13.5, "FgPrimaryBrush", FontWeights.Medium));
        if (!string.IsNullOrEmpty(desc))
        {
            var sub = MakeText(desc, 11.5, "FgMutedBrush");
            sub.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(sub);
        }
        row.Children.Add(left);
        return row;
    }

    private Button SmallChipButton(string text, Action click, bool danger = false)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(8, 4, 8, 4),
            MinHeight = 26,
            FontSize = 11.5,
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)FindResource(danger ? "DangerButton" : "ModernButton")
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    // ==================== Tab 0: 小组件与布局 ====================
    private UIElement BuildWidgetsPage()
    {
        var page = new StackPanel();
        var list = host.GetWidgets();
        bool uniform = host.GetUniformScale();
        double globalScale = host.GetGlobalScale();

        page.Children.Add(MakeText("小组件与布局", 18, "FgPrimaryBrush", FontWeights.SemiBold));
        var intro = MakeText("同时管理多个桌面小组件，自由调节缩放尺寸，查看并精确微调坐标对齐。", 12.5, "FgMutedBrush");
        intro.Margin = new Thickness(0, 3, 0, 14);
        page.Children.Add(intro);

        // Card 1: Scale & Apple Grid control
        page.Children.Add(MakeText("尺寸与网格对齐", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var scaleStack = new StackPanel();

        var uniformSwitch = new CheckBox
        {
            IsChecked = uniform,
            Style = (Style)FindResource("SwitchCheckBox")
        };
        uniformSwitch.Checked += (_, _) =>
        {
            host.SetUniformScale(true);
            Dispatcher.BeginInvoke(RenderActivePage);
        };
        uniformSwitch.Unchecked += (_, _) =>
        {
            host.SetUniformScale(false);
            Dispatcher.BeginInvoke(RenderActivePage);
        };
        scaleStack.Children.Add(SettingRow("所有小组件统一大小", "开启后所有组件共享同一缩放比例，关闭可为每个组件单独调整大小", uniformSwitch));

        if (uniform)
        {
            scaleStack.Children.Add(Divider());
            var globalCtrl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider
            {
                Minimum = 60,
                Maximum = 130,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = Math.Round(globalScale * 100),
                Width = 148
            };
            var pctLabel = MakeText($"{(int)slider.Value}%", 12.5, "FgPrimaryBrush", FontWeights.SemiBold);
            pctLabel.Width = 40;
            pctLabel.TextAlignment = TextAlignment.Right;
            slider.ValueChanged += (_, e) =>
            {
                int pct = (int)Math.Round(e.NewValue);
                pctLabel.Text = $"{pct}%";
                host.SetGlobalScale(pct / 100.0);
            };
            globalCtrl.Children.Add(slider);
            globalCtrl.Children.Add(pctLabel);

            scaleStack.Children.Add(SettingRow("统一缩放比例", "调节桌面小组件的整体显示尺寸（建议 75% – 90%）", globalCtrl));

            var presets = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            foreach (int p in new[] { 70, 80, 85, 90, 100 })
            {
                int target = p;
                presets.Children.Add(SmallChipButton($"{p}%", () => { slider.Value = target; }));
            }
            scaleStack.Children.Add(presets);
        }

        // Apple Grid Snapping switch anchored at #1
        scaleStack.Children.Add(Divider());
        bool snap = host.GetSnapToGrid();
        var snapRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (snap && list.Count > 1)
        {
            var snapAllBtn = SmallChipButton("全部吸附对齐", () =>
            {
                host.SnapAllToGrid();
                Dispatcher.BeginInvoke(RenderActivePage);
            });
            snapAllBtn.Margin = new Thickness(0, 0, 10, 0);
            snapRight.Children.Add(snapAllBtn);
        }
        var snapSwitch = new CheckBox
        {
            IsChecked = snap,
            Style = (Style)FindResource("SwitchCheckBox")
        };
        snapSwitch.Checked += (_, _) =>
        {
            host.SetSnapToGrid(true);
            Dispatcher.BeginInvoke(RenderActivePage);
        };
        snapSwitch.Unchecked += (_, _) =>
        {
            host.SetSnapToGrid(false);
            Dispatcher.BeginInvoke(RenderActivePage);
        };
        snapRight.Children.Add(snapSwitch);
        string gridDesc = snap
            ? $"以 #1 为原点建立 Apple 网格（步距 {(int)host.GetGridStepPixels()} px，间距 {(int)host.GetGridGapPixels()} px），拖动松手自动吸附，缩放自动保持对齐"
            : "关闭后可在桌面任意像素位置自由拖动摆放";
        scaleStack.Children.Add(SettingRow("对齐到网格（以 #1 为基准）", gridDesc, snapRight));

        page.Children.Add(SectionCard(scaleStack));

        // Header for widget list + Add widget bar
        var listHeader = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var addBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var addCombo = new ComboBox { Width = 122, MinHeight = 30, FontSize = 12.5, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var v in Enum.GetValues<Variant>())
            addCombo.Items.Add(new ComboBoxItem { Content = CalendarCard.Names[(int)v], Tag = v });
        addCombo.SelectedIndex = (int)newWidgetVariant;
        addCombo.SelectionChanged += (_, _) =>
        {
            if (addCombo.SelectedItem is ComboBoxItem { Tag: Variant chosen })
                newWidgetVariant = chosen;
        };
        var addBtn = new Button
        {
            Content = "+ 添加小组件",
            Style = (Style)FindResource("PrimaryButton"),
            MinHeight = 30,
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12.5
        };
        addBtn.Click += (_, _) =>
        {
            host.AddWidget(newWidgetVariant);
            Dispatcher.BeginInvoke(RenderActivePage);
        };
        addBar.Children.Add(addCombo);
        addBar.Children.Add(addBtn);
        DockPanel.SetDock(addBar, Dock.Right);
        listHeader.Children.Add(addBar);

        listHeader.Children.Add(MakeText($"桌面上的小组件（{list.Count}）", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        page.Children.Add(listHeader);

        // Render each widget card with live size/position & alignment helpers
        var primary = list[0];
        for (int i = 0; i < list.Count; i++)
        {
            page.Children.Add(BuildWidgetInstanceCard(list[i], i, primary, list.Count, uniform, globalScale));
        }

        return page;
    }

    private Border BuildWidgetInstanceCard(WidgetInstance w, int index, WidgetInstance primary, int totalCount, bool uniform, double globalScale)
    {
        var stack = new StackPanel();
        string id = w.Id;

        // Row 1: Index badge + Variant dropdown + Live size badge + Remove button
        var topRow = new DockPanel();
        var rightOps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var sizeBadge = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3.5, 8, 3.5), VerticalAlignment = VerticalAlignment.Center };
        sizeBadge.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
        var sizeText = MakeText("", 11.5, "FgSecondaryBrush", FontWeights.Medium);
        sizeBadge.Child = sizeText;
        rightOps.Children.Add(sizeBadge);

        if (totalCount > 1)
        {
            rightOps.Children.Add(SmallChipButton("移除", () =>
            {
                host.RemoveWidget(id);
                Dispatcher.BeginInvoke(RenderActivePage);
            }, danger: true));
        }
        DockPanel.SetDock(rightOps, Dock.Right);
        topRow.Children.Add(rightOps);

        var leftTop = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var numBadge = new Border
        {
            MinWidth = 24,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        numBadge.SetResourceReference(Border.BackgroundProperty, index == 0 ? "AccentSoftBrush" : "BgHoverBrush");
        var numText = MakeText($"#{index + 1}", 11.5, index == 0 ? "AccentBrush" : "FgMutedBrush", FontWeights.SemiBold);
        numText.HorizontalAlignment = HorizontalAlignment.Center;
        numBadge.Child = numText;
        leftTop.Children.Add(numBadge);

        var variantPicker = new ComboBox { Width = 114, MinHeight = 30, FontSize = 12.5 };
        foreach (var v in Enum.GetValues<Variant>())
            variantPicker.Items.Add(new ComboBoxItem { Content = CalendarCard.Names[(int)v], Tag = v });
        variantPicker.SelectedIndex = (int)w.Variant;
        variantPicker.SelectionChanged += (_, _) =>
        {
            if (updatingLiveFields) return;
            if (variantPicker.SelectedItem is ComboBoxItem { Tag: Variant next } && next != w.Variant)
            {
                host.UpdateWidget(id, item => item.Variant = next, true);
                Dispatcher.BeginInvoke(RenderActivePage);
            }
        };
        leftTop.Children.Add(variantPicker);

        var lockBtn = SmallChipButton(w.Locked ? "已锁定" : "未锁定", () =>
        {
            host.UpdateWidget(id, item => item.Locked = !item.Locked, false);
            Dispatcher.BeginInvoke(RenderActivePage);
        });
        lockBtn.Margin = new Thickness(6, 0, 0, 0);
        leftTop.Children.Add(lockBtn);

        topRow.Children.Add(leftTop);
        stack.Children.Add(topRow);

        // Row 2: Live X / Y coordinate editor + quick alignment buttons
        var posRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };

        var coords = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        coords.Children.Add(MakeText("X", 12, "FgMutedBrush", FontWeights.SemiBold));
        var xBox = new TextBox
        {
            Width = 58,
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(5, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        coords.Children.Add(xBox);

        coords.Children.Add(MakeText("Y", 12, "FgMutedBrush", FontWeights.SemiBold));
        var yBox = new TextBox
        {
            Width = 58,
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(5, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        coords.Children.Add(yBox);

        void CommitCoords()
        {
            if (updatingLiveFields) return;
            if (double.TryParse(xBox.Text.Trim(), CultureInfo.InvariantCulture, out var nx)
                && double.TryParse(yBox.Text.Trim(), CultureInfo.InvariantCulture, out var ny)
                && double.IsFinite(nx) && double.IsFinite(ny))
            {
                host.UpdateWidget(id, item => { item.Left = Math.Round(nx); item.Top = Math.Round(ny); }, true);
            }
        }
        xBox.LostFocus += (_, _) => CommitCoords();
        yBox.LostFocus += (_, _) => CommitCoords();
        xBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitCoords(); Keyboard.ClearFocus(); } };
        yBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitCoords(); Keyboard.ClearFocus(); } };

        DockPanel.SetDock(coords, Dock.Left);
        posRow.Children.Add(coords);

        bool snap = host.GetSnapToGrid();
        if (index > 0)
        {
            var alignRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (snap)
            {
                var gridCap = MakeText("按网格移位", 11.5, "FgMutedBrush");
                gridCap.Margin = new Thickness(0, 0, 4, 0);
                alignRow.Children.Add(gridCap);
                alignRow.Children.Add(SmallChipButton("←", () => host.NudgeWidgetOnGrid(id, -1, 0)));
                alignRow.Children.Add(SmallChipButton("↑", () => host.NudgeWidgetOnGrid(id, 0, -1)));
                alignRow.Children.Add(SmallChipButton("↓", () => host.NudgeWidgetOnGrid(id, 0, 1)));
                alignRow.Children.Add(SmallChipButton("→", () => host.NudgeWidgetOnGrid(id, 1, 0)));
                alignRow.Children.Add(SmallChipButton("吸附", () => host.SnapWidgetToGrid(id)));
            }
            else
            {
                var alignCap = MakeText("参考 #1", 11.5, "FgMutedBrush");
                alignCap.Margin = new Thickness(0, 0, 4, 0);
                alignRow.Children.Add(alignCap);
                alignRow.Children.Add(SmallChipButton("左齐", () =>
                {
                    var p = host.GetWidgets()[0];
                    host.UpdateWidget(id, item => item.Left = p.Left, true);
                }));
                alignRow.Children.Add(SmallChipButton("顶齐", () =>
                {
                    var p = host.GetWidgets()[0];
                    host.UpdateWidget(id, item => item.Top = p.Top, true);
                }));
            }
            posRow.Children.Add(alignRow);
        }
        else
        {
            var hint = MakeText(snap ? "网格基准原点 (0, 0)" : "拖动组件可实时更新坐标", 11.5, "FgMutedBrush");
            hint.HorizontalAlignment = HorizontalAlignment.Right;
            posRow.Children.Add(hint);
        }
        stack.Children.Add(posRow);

        // Row 3: Per-widget scale slider when Uniform Scale is OFF
        Slider? individualSlider = null;
        TextBlock? individualPct = null;
        if (!uniform)
        {
            var sRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            individualPct = MakeText($"{(int)Math.Round(w.Scale * 100)}%", 12, "FgSecondaryBrush", FontWeights.Medium);
            individualPct.Width = 40;
            individualPct.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(individualPct, Dock.Right);
            sRow.Children.Add(individualPct);

            var cap = MakeText("独立大小", 12, "FgMutedBrush");
            cap.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(cap, Dock.Left);
            sRow.Children.Add(cap);

            individualSlider = new Slider
            {
                Minimum = 60,
                Maximum = 130,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = Math.Round(w.Scale * 100)
            };
            individualSlider.ValueChanged += (_, e) =>
            {
                if (updatingLiveFields) return;
                int pct = (int)Math.Round(e.NewValue);
                individualPct.Text = $"{pct}%";
                host.UpdateWidget(id, item => item.Scale = pct / 100.0, true);
            };
            sRow.Children.Add(individualSlider);
            stack.Children.Add(sRow);
        }

        // Row 4: Month-specific detailed lunar toggle
        if (w.Variant == Variant.Month)
        {
            stack.Children.Add(Divider(new Thickness(0, 9, 0, 8)));
            var lunarSwitch = new CheckBox
            {
                IsChecked = w.DetailedLunar,
                Style = (Style)FindResource("SwitchCheckBox")
            };
            lunarSwitch.Checked += (_, _) => host.UpdateWidget(id, item => item.DetailedLunar = true, true);
            lunarSwitch.Unchecked += (_, _) => host.UpdateWidget(id, item => item.DetailedLunar = false, true);
            stack.Children.Add(SettingRow("逐日显示农历", "展开此月历每天格子的农历日数", lunarSwitch));
        }

        void RefreshLive(WidgetInstance cur)
        {
            bool u = host.GetUniformScale();
            double gs = host.GetGlobalScale();
            double effScale = Math.Clamp(u ? gs : cur.Scale, 0.60, 1.30);
            var effSize = cur.EffectiveSize(u, gs);
            string gridPrefix = host.GetSnapToGrid()
                ? $"({host.GetGridCell(cur).Col},{host.GetGridCell(cur).Row}) · "
                : "";
            sizeText.Text = $"{gridPrefix}{(int)effSize.Width}×{(int)effSize.Height} · {(int)Math.Round(effScale * 100)}%";
            if (!xBox.IsKeyboardFocused && double.IsFinite(cur.Left))
                xBox.Text = ((int)Math.Round(cur.Left)).ToString(CultureInfo.InvariantCulture);
            if (!yBox.IsKeyboardFocused && double.IsFinite(cur.Top))
                yBox.Text = ((int)Math.Round(cur.Top)).ToString(CultureInfo.InvariantCulture);
            if (individualSlider != null && individualPct != null && !individualSlider.IsMouseCaptureWithin)
            {
                int pct = (int)Math.Round(cur.Scale * 100);
                individualSlider.Value = pct;
                individualPct.Text = $"{pct}%";
            }
        }

        RefreshLive(w);
        liveCardUpdaters[id] = RefreshLive;
        return SectionCard(stack, new Thickness(14, 11, 14, 11), new Thickness(0, 0, 0, 10));
    }

    // ==================== Tab 1: 外观与显示 ====================
    private UIElement BuildAppearancePage()
    {
        var page = new StackPanel();
        page.Children.Add(MakeText("外观与显示", 18, "FgPrimaryBrush", FontWeights.SemiBold));
        var intro = MakeText("全局主题、背景通透度与日程细节展示规则。", 12.5, "FgMutedBrush");
        intro.Margin = new Thickness(0, 3, 0, 14);
        page.Children.Add(intro);

        page.Children.Add(MakeText("显示偏好", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var prefStack = new StackPanel();

        var themeSeg = SegmentedControl(
            [("跟随系统", ThemePreference.System), ("浅色", ThemePreference.Light), ("深色", ThemePreference.Dark)],
            host.GetTheme(),
            pref =>
            {
                host.SetTheme(pref);
                Dispatcher.BeginInvoke(ApplyThemeAndRefresh);
            });
        prefStack.Children.Add(SettingRow("外观主题", "控制所有小组件与设置窗口的浅色 / 深色模式", themeSeg));
        prefStack.Children.Add(Divider());

        var travelSeg = SegmentedControl(
            [("智能", TravelDisplayMode.TodayOnly), ("始终", TravelDisplayMode.Always), ("关闭", TravelDisplayMode.Never)],
            host.GetTravelMode(),
            mode =>
            {
                host.SetTravelMode(mode);
                Dispatcher.BeginInvoke(RenderActivePage);
            });
        prefStack.Children.Add(SettingRow("行程时间提示", "智能模式：今日摘要与日程列表显示，未来摘要精简隐藏", travelSeg));
        prefStack.Children.Add(Divider());

        var opacityPanel = new StackPanel { Orientation = Orientation.Horizontal, Width = 172 };
        var opacitySlider = new Slider
        {
            Minimum = 75,
            Maximum = 100,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = Math.Round(host.GetOpacity() * 100),
            Width = 128
        };
        var opacityLabel = MakeText($"{(int)opacitySlider.Value}%", 12.5, "FgMutedBrush", FontWeights.Medium);
        opacityLabel.Width = 38;
        opacityLabel.TextAlignment = TextAlignment.Right;
        opacitySlider.ValueChanged += (_, e) =>
        {
            int pct = (int)Math.Round(e.NewValue);
            opacityLabel.Text = $"{pct}%";
            host.SetOpacity(pct / 100.0);
        };
        opacityPanel.Children.Add(opacitySlider);
        opacityPanel.Children.Add(opacityLabel);
        prefStack.Children.Add(SettingRow("卡片不透明度", "调节桌面日历背景的通透程度", opacityPanel));
        prefStack.Children.Add(Divider());

        var dotsSwitch = new CheckBox
        {
            IsChecked = host.GetMonthDots(),
            Style = (Style)FindResource("SwitchCheckBox")
        };
        dotsSwitch.Checked += (_, _) => host.SetMonthDots(true);
        dotsSwitch.Unchecked += (_, _) => host.SetMonthDots(false);
        prefStack.Children.Add(SettingRow("月历显示日程标记点", "默认与 Apple 原版一致不显示圆点，开启后以彩色小点标注有日程的日期", dotsSwitch));

        page.Children.Add(SectionCard(prefStack));

        // Visual gallery for reference & quick-add
        page.Children.Add(MakeText("快速预览样式（点击新增一个该样式组件）", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(-4, 6, -4, 10) };
        foreach (var v in Enum.GetValues<Variant>())
        {
            var (name, desc) = VariantMeta[(int)v];
            var cardContent = new StackPanel();
            cardContent.Children.Add(BuildMiniPreview(v));
            var title = MakeText(name, 12, "FgPrimaryBrush", FontWeights.SemiBold);
            title.Margin = new Thickness(0, 6, 0, 1);
            cardContent.Children.Add(title);
            cardContent.Children.Add(MakeText(desc, 10.5, "FgMutedBrush"));

            var btn = new Button
            {
                Content = cardContent,
                Margin = new Thickness(3.5),
                Padding = new Thickness(8, 7, 8, 7),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = $"点击在桌面添加「{name}」小组件"
            };
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "vbd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("BorderSubtleBrush"));
            bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("BgCardBrush"));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            bd.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentBrush"), "vbd"));
            tpl.Triggers.Add(hover);
            btn.Template = tpl;
            var chosen = v;
            btn.Click += (_, _) =>
            {
                host.AddWidget(chosen);
                activeTab = 0;
                Dispatcher.BeginInvoke(() =>
                {
                    RenderNav();
                    RenderActivePage();
                });
            };
            grid.Children.Add(btn);
        }
        page.Children.Add(grid);
        return page;
    }

    private Border BuildMiniPreview(Variant v)
    {
        var box = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 5, 6, 5)
        };
        box.SetResourceReference(Border.BackgroundProperty, "BgWindowBrush");

        Brush accent = (Brush)FindResource("AccentBrush");
        Brush muted = (Brush)FindResource("BorderControlBrush");
        Brush soft = (Brush)FindResource("BorderSubtleBrush");

        Border Bar(double h, double w, Brush fill, Thickness? m = null, double? r = null) =>
            new() { Height = h, Width = w, CornerRadius = new CornerRadius(r ?? Math.Min(h / 2, 3)), Background = fill, HorizontalAlignment = HorizontalAlignment.Left, Margin = m ?? new Thickness(0) };

        switch (v)
        {
            case Variant.Month:
                var mg = new UniformGrid { Columns = 7, Rows = 3, VerticalAlignment = VerticalAlignment.Center };
                for (int i = 0; i < 21; i++)
                    mg.Children.Add(new Ellipse { Width = 3.8, Height = 3.8, Fill = i == 9 ? accent : muted, Margin = new Thickness(1) });
                box.Child = mg;
                break;
            case Variant.Date:
                var dg = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                dg.Children.Add(Bar(3, 24, accent, new Thickness(0, 0, 0, 3)));
                dg.Children.Add(Bar(13, 22, muted, r: 3));
                box.Child = dg;
                break;
            case Variant.Compact:
                var cg = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                cg.Children.Add(Bar(7, 16, muted, new Thickness(0, 0, 0, 4), 2.5));
                cg.Children.Add(Bar(9, 58, soft, r: 2.5));
                box.Child = cg;
                break;
            case Variant.Summary:
                var sg = new Grid();
                sg.ColumnDefinitions.Add(new ColumnDefinition());
                sg.ColumnDefinitions.Add(new ColumnDefinition());
                var sl = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sl.Children.Add(Bar(8, 16, muted, new Thickness(0, 0, 0, 4), 2.5));
                sl.Children.Add(Bar(3.5, 28, soft));
                var sr = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sr.Children.Add(Bar(2.5, 16, muted, new Thickness(0, 0, 0, 3)));
                sr.Children.Add(Bar(6.5, 36, soft, new Thickness(0, 0, 0, 3), 2));
                sr.Children.Add(Bar(6.5, 36, soft, r: 2));
                Grid.SetColumn(sr, 1);
                sg.Children.Add(sl);
                sg.Children.Add(sr);
                box.Child = sg;
                break;
            case Variant.Agenda:
                var ag = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                ag.Children.Add(Bar(3, 30, accent, new Thickness(0, 0, 0, 4)));
                ag.Children.Add(Bar(7, 68, soft, new Thickness(0, 0, 0, 3.5), 2));
                ag.Children.Add(Bar(7, 56, soft, r: 2));
                box.Child = ag;
                break;
            default:
                int cols = v == Variant.Week ? 4 : 2;
                var tg = new UniformGrid { Rows = 1, Columns = cols };
                for (int i = 0; i < cols; i++)
                {
                    var col = new StackPanel { Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
                    col.Children.Add(Bar(2, 12, i == 0 ? accent : muted, new Thickness(0, 0, 0, 3)));
                    col.Children.Add(Bar(14, cols == 4 ? 14 : 26, i == 1 ? accent : soft, r: 3));
                    tg.Children.Add(col);
                }
                box.Child = tg;
                break;
        }
        return box;
    }

    // ==================== Tab 2: iCloud 与日历 ====================
    private UIElement BuildCloudPage()
    {
        var page = new StackPanel();
        page.Children.Add(MakeText("iCloud 与日历", 18, "FgPrimaryBrush", FontWeights.SemiBold));
        var intro = MakeText("管理 Apple iCloud 双向同步账户，并自由切换桌面显示的日历。", 12.5, "FgMutedBrush");
        intro.Margin = new Thickness(0, 3, 0, 14);
        page.Children.Add(intro);

        page.Children.Add(MakeText("iCloud 账户", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var account = host.GetAccount();
        var accStack = new StackPanel();
        if (account == null)
        {
            accStack.Children.Add(MakeText("尚未连接 iCloud 日历", 14, "FgPrimaryBrush", FontWeights.SemiBold));
            var desc = MakeText("连接后可通过 CalDAV 每 60 秒自动增量同步 Apple 日历，支持离线缓存与双向编辑。", 12, "FgMutedBrush");
            desc.Margin = new Thickness(0, 4, 0, 12);
            accStack.Children.Add(desc);

            var connectBtn = new Button { Content = "连接 iCloud 账户…", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
            connectBtn.Click += (_, _) =>
            {
                if (host.ConnectAccount(this)) Dispatcher.BeginInvoke(RenderActivePage);
            };
            accStack.Children.Add(connectBtn);
        }
        else
        {
            var top = new DockPanel();
            var statusDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = host.GetSyncText().StartsWith("同步失败") ? Palette.Color("#FF3B30") : Palette.Color("#34C759"),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusRow.Children.Add(statusDot);
            statusRow.Children.Add(MakeText(host.GetSyncText(), 12, "FgMutedBrush"));
            DockPanel.SetDock(statusRow, Dock.Right);
            top.Children.Add(statusRow);

            var mailCol = new StackPanel();
            mailCol.Children.Add(MakeText(account.Email, 14.5, "FgPrimaryBrush", FontWeights.SemiBold));
            mailCol.Children.Add(MakeText($"已同步 {account.Calendars.Count} 本云端日历", 12, "FgMutedBrush"));
            top.Children.Add(mailCol);
            accStack.Children.Add(top);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var syncBtn = new Button { Content = "立即同步", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0) };
            syncBtn.Click += async (_, _) =>
            {
                syncBtn.IsEnabled = false;
                syncBtn.Content = "同步中…";
                try { await host.SyncNow(); }
                finally { _ = Dispatcher.BeginInvoke(RenderActivePage); }
            };
            var manageBtn = new Button { Content = "管理日历列表…", Style = (Style)FindResource("ModernButton"), Margin = new Thickness(0, 0, 8, 0) };
            manageBtn.Click += (_, _) =>
            {
                if (host.ConnectAccount(this)) Dispatcher.BeginInvoke(RenderActivePage);
            };
            var disconnectBtn = new Button { Content = "断开连接", Style = (Style)FindResource("DangerButton") };
            disconnectBtn.Click += (_, _) =>
            {
                host.DisconnectAccount();
                Dispatcher.BeginInvoke(RenderActivePage);
            };
            btns.Children.Add(syncBtn);
            btns.Children.Add(manageBtn);
            btns.Children.Add(disconnectBtn);
            accStack.Children.Add(btns);

            var readErrors = host.GetReadErrors();
            if (readErrors.Count > 0)
            {
                var errBanner = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 12, 0, 0) };
                errBanner.SetResourceReference(Border.BackgroundProperty, "DangerBgBrush");
                errBanner.Child = MakeText($"{readErrors.Values.Sum()} 个日程文件未能解析：" + string.Join("；", readErrors.Select(p => $"{p.Value} 个 · {p.Key}")), 12, "DangerBrush");
                accStack.Children.Add(errBanner);
            }
        }
        page.Children.Add(SectionCard(accStack));

        page.Children.Add(MakeText("显示日历", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var calStack = new StackPanel();
        var sources = host.GetSources().ToList();
        for (int i = 0; i < sources.Count; i++)
        {
            var (source, visible) = sources[i];
            if (i > 0) calStack.Children.Add(Divider());
            var toggle = new CheckBox
            {
                IsChecked = visible,
                Style = (Style)FindResource("SwitchCheckBox")
            };
            var captured = source;
            toggle.Checked += (_, _) => host.SetSourceVisible(captured, true);
            toggle.Unchecked += (_, _) => host.SetSourceVisible(captured, false);

            var row = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Dialogs.SourceLabel(source.Name, source.Accent, isDark));
            if (!source.Writable)
            {
                var tag = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1.5, 6, 1.5), Margin = new Thickness(8, 0, 0, 0) };
                tag.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                tag.Child = MakeText("只读", 11, "FgMutedBrush");
                left.Children.Add(tag);
            }
            row.Children.Add(left);
            calStack.Children.Add(row);
        }
        page.Children.Add(SectionCard(calStack));

        return page;
    }

    // ==================== Tab 3: 常规 ====================
    private UIElement BuildGeneralPage()
    {
        var page = new StackPanel();
        page.Children.Add(MakeText("常规", 18, "FgPrimaryBrush", FontWeights.SemiBold));
        var intro = MakeText("系统启动行为、多组件自动归位与本地缓存信息。", 12.5, "FgMutedBrush");
        intro.Margin = new Thickness(0, 3, 0, 14);
        page.Children.Add(intro);

        page.Children.Add(MakeText("系统与窗口", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var sysStack = new StackPanel();

        var startupSwitch = new CheckBox
        {
            IsChecked = StartupManager.IsEnabled(),
            Style = (Style)FindResource("SwitchCheckBox")
        };
        startupSwitch.Checked += (_, _) => { try { StartupManager.SetEnabled(true); } catch { } };
        startupSwitch.Unchecked += (_, _) => { try { StartupManager.SetEnabled(false); } catch { } };
        sysStack.Children.Add(SettingRow("开机自动启动", "登录 Windows 时自动在桌面启动日历小组件", startupSwitch));
        sysStack.Children.Add(Divider());

        var resetBtn = new Button { Content = "全部移回主屏幕", Style = (Style)FindResource("ModernButton") };
        resetBtn.Click += (_, _) => host.ResetAllPositions();
        sysStack.Children.Add(SettingRow("整齐归位全部小组件", "将所有桌面小组件自动排列回主屏幕右侧", resetBtn));
        sysStack.Children.Add(Divider());

        var todayBtn = new Button { Content = "回到今天", Style = (Style)FindResource("ModernButton") };
        todayBtn.Click += (_, _) => host.GoToday();
        sysStack.Children.Add(SettingRow("跳回当前日期", "月历翻页后快速回到当前月份", todayBtn));

        page.Children.Add(SectionCard(sysStack));

        page.Children.Add(MakeText("数据与隐私", 12.5, "FgMutedBrush", FontWeights.SemiBold));
        var dataStack = new StackPanel();
        string currentFolder = host.GetDataFolder();

        dataStack.Children.Add(MakeText("本地缓存位置", 13.5, "FgPrimaryBrush", FontWeights.Medium));
        var pathBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 6, 0, 10)
        };
        pathBox.SetResourceReference(Border.BackgroundProperty, "BgWindowBrush");
        pathBox.Child = MakeText(currentFolder, 12, "FgSecondaryBrush");
        dataStack.Children.Add(pathBox);

        var folderBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var changeBtn = new Button { Content = "更改位置…", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        changeBtn.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择日历本地缓存文件夹",
                InitialDirectory = Directory.Exists(currentFolder) ? currentFolder : StorageLocation.DefaultFolder
            };
            if (picker.ShowDialog(this) != true || string.IsNullOrWhiteSpace(picker.FolderName)) return;
            changeBtn.IsEnabled = false;
            try
            {
                if (await host.ChangeDataFolder(this, picker.FolderName))
                    _ = Dispatcher.BeginInvoke(ApplyThemeAndRefresh);
            }
            finally { changeBtn.IsEnabled = true; }
        };
        folderBtns.Children.Add(changeBtn);

        var openBtn = new Button { Content = "打开目录", Style = (Style)FindResource("ModernButton"), Margin = new Thickness(0, 0, 8, 0) };
        openBtn.Click += (_, _) =>
        {
            try
            {
                string dir = host.GetDataFolder();
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch { }
        };
        folderBtns.Children.Add(openBtn);

        if (!StorageLocation.IsDefault(currentFolder))
        {
            var resetDirBtn = new Button { Content = "恢复默认", Style = (Style)FindResource("ModernButton") };
            resetDirBtn.Click += async (_, _) =>
            {
                resetDirBtn.IsEnabled = false;
                try
                {
                    if (await host.ChangeDataFolder(this, StorageLocation.DefaultFolder))
                        _ = Dispatcher.BeginInvoke(ApplyThemeAndRefresh);
                }
                finally { resetDirBtn.IsEnabled = true; }
            };
            folderBtns.Children.Add(resetDirBtn);
        }
        dataStack.Children.Add(folderBtns);

        dataStack.Children.Add(Divider());
        dataStack.Children.Add(MakeText("所有日历数据缓存在该目录的 calendar.db 中（仅直连 iCloud，不经过任何第三方服务器）。迁移时会自动安全复制并清理旧文件。", 12, "FgMutedBrush"));
        page.Children.Add(SectionCard(dataStack));

        return page;
    }
}
