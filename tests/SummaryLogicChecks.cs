using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinMacCalendar;

internal static partial class Program
{
    private static void CheckSummaryAndCourseScenario(string? outDir)
    {
        var today = new DateTime(2026, 11, 25);
        var nowTime = today.AddHours(22).AddMinutes(22);
        var work = new CalendarSource("work", "工作", "#000000", false);
        var team = new CalendarSource("team", "团队", "#278AD1", true);

        var items = new List<Entry>
        {
            new(new Resource("", ""), "产品需求评审", today.AddDays(1).AddHours(8).AddMinutes(55), today.AddDays(1).AddHours(11).AddMinutes(40),
                false, false, TimeSpan.FromMinutes(15), false, "会议室 A201", "准备演示文档", "每周四重复") { Source = work },
            new(new Resource("", ""), "前端技术分享", today.AddDays(1).AddHours(13).AddMinutes(30), today.AddDays(1).AddHours(16).AddMinutes(30),
                false, false, TimeSpan.FromMinutes(15), false, "报告厅 B305") { Source = work },
            new(new Resource("", ""), "迭代复盘会", today.AddDays(1).AddHours(19), today.AddDays(1).AddHours(20).AddMinutes(30),
                false, true, TimeSpan.FromMinutes(15)) { Source = team },
            new(new Resource("", ""), "系统性能优化与架构设计讨论", today.AddDays(2).AddHours(8).AddMinutes(55), today.AddDays(2).AddHours(11).AddMinutes(40),
                false, false, TimeSpan.FromMinutes(15)) { Source = work }
        };

        var size = CalendarCard.Sizes[(int)Variant.Summary];
        var card = new CalendarCard(Variant.Summary, today, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        card.Measure(size);
        card.Arrange(new Rect(size));
        card.UpdateLayout();

        var texts = Tree(card).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(texts.Contains("明天"), "双栏摘要应显示明天");
        Check(texts.Contains("产品需求评审"), "应显示明天第一条日程");
        Check(texts.Contains("前端技术分享"), "应优先显示明天第二条日程而非强行折叠");
        Check(texts.Contains("迭代复盘会"), "应优先完整显示明天的第三条日程");
        Check(!texts.Any(t => t.Contains("行程时间")), "明天及以后摘要卡片不应占行显示行程时间");
        Check(!texts.Contains("系统性能优化与架构设计讨论"), "空间不足时不应硬塞后天并裁切");

        foreach (var btn in Tree(card).OfType<Button>())
        {
            var pt = btn.TranslatePoint(new Point(), card);
            Check(pt.Y + btn.ActualHeight <= size.Height + 0.5, $"卡片内按钮不应溢出底边: {AutomationProperties.GetName(btn)}");
        }

        // Crowded tomorrow -> collapse overflow with next hidden item's accent color (blue team, NOT hardcoded red)
        var crowded = new List<Entry>(items.Take(2))
        {
            new(new Resource("", ""), "设计走查", today.AddDays(1).AddHours(17), today.AddDays(1).AddHours(18), false, true) { Source = team },
            new(new Resource("", ""), "晚间讲座", today.AddDays(1).AddHours(19), today.AddDays(1).AddHours(20), false, true) { Source = team },
            new(new Resource("", ""), "阅读整理", today.AddDays(1).AddHours(20).AddMinutes(30), today.AddDays(1).AddHours(22), false, true) { Source = team },
        };
        var crowdedCard = new CalendarCard(Variant.Summary, today, nowTime, crowded, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        crowdedCard.Measure(size);
        crowdedCard.Arrange(new Rect(size));
        crowdedCard.UpdateLayout();

        var moreBtn = Tree(crowdedCard).OfType<Button>().Single(b => Tree(b).OfType<TextBlock>().Any(t => t.Text.StartsWith("其他 ")));
        var bar = Tree(moreBtn).OfType<Border>().Single(b => b.Width == 2.5);
        var barColor = ((SolidColorBrush)bar.Background).Color;
        var expectedBlue = ((SolidColorBrush)Palette.Color("#278AD1")).Color;
        Check(barColor == expectedBlue, $"溢出提示色条应匹配被隐藏日程的日历色，实际为 {barColor}");

        // Multi-day event starting yesterday and ending after tomorrow should appear under tomorrow
        var multiDay = new Entry(new Resource("", ""), "技术峰会", today.AddDays(-1), today.AddDays(3), true, false) { Source = team };
        var multiCard = new CalendarCard(Variant.Summary, today, nowTime, [multiDay], _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        multiCard.Measure(size);
        multiCard.Arrange(new Rect(size));
        multiCard.UpdateLayout();
        var multiTexts = Tree(multiCard).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(multiTexts.Contains("明天") && multiTexts.Count(t => t == "技术峰会") >= 2, "跨多天日程应在明天分组中显示");

        // CalendarStore cache folder hot migration
        string srcDir = Path.Combine(Path.GetTempPath(), "wmc-mig-src-" + Guid.NewGuid().ToString("N"));
        string dstDir = Path.Combine(Path.GetTempPath(), "wmc-mig-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new CalendarStore(Path.Combine(srcDir, "calendar.db"));
            store.Set("theme", "Dark");
            store.Save(CalendarStore.Local, FileAt("迁移测试日程", today.AddHours(10), today.AddHours(11)));
            store.MigrateToFolder(dstDir, useExistingTargetIfPresent: false);
            Check(string.Equals(StorageLocation.Normalize(store.FolderPath), StorageLocation.Normalize(dstDir), StringComparison.OrdinalIgnoreCase), "迁移后目录应更新为目标目录");
            Check(File.Exists(Path.Combine(dstDir, "calendar.db")), "目标目录应生成 calendar.db");
            Check(!File.Exists(Path.Combine(srcDir, "calendar.db")), "迁移成功后旧目录 calendar.db 应已清理");
            Check(store.Get("theme") == "Dark" && store.List(CalendarStore.Local).Any(r => r.Ics.Contains("迁移测试日程")), "迁移后所有设置与日程数据完整保留");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
            if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true);
        }

        // Run today-first, evening overflow, and afternoon elastic timeline logic checks
        CheckEveningScenario(outDir);
        CheckMiddayTodayFirstScenario(outDir);
        CheckAfternoonElasticTimelineScenario(outDir);

        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            SaveVisual(card, size, Path.Combine(outDir, "summary-light.png"));
            RenderMonthComparison(outDir);
            RenderSettingsPreview(outDir, items[0]);
        }
    }

    private static void CheckEveningScenario(string? outDir)
    {
        var day = new DateTime(2026, 11, 25);
        var nowTime = day.AddHours(23).AddMinutes(2);
        var work = new CalendarSource("work", "工作", "#000000", false);

        var items = new List<Entry>
        {
            new(new Resource("", ""), "晚间值班例会", day.AddHours(23), day.AddDays(1),
                false, true, TimeSpan.FromMinutes(15), false, "线上会议室") { Source = work },
            new(new Resource("", ""), "产品需求评审", day.AddDays(1).AddHours(8).AddMinutes(55), day.AddDays(1).AddHours(11).AddMinutes(40),
                false, false, TimeSpan.FromMinutes(15), false, "报告厅 A101") { Source = work },
            new(new Resource("", ""), "技术方案研讨", day.AddDays(1).AddHours(13).AddMinutes(30), day.AddDays(1).AddHours(15).AddMinutes(10),
                false, false, TimeSpan.Zero, false, "会议室 B202") { Source = work },
            new(new Resource("", ""), "架构设计分享", day.AddDays(1).AddHours(20), day.AddDays(1).AddHours(21).AddMinutes(30),
                false, false, TimeSpan.FromMinutes(15), false, "阶梯教室 C303") { Source = work },
            new(new Resource("", ""), "性能调优实践与总结", day.AddDays(2).AddHours(13).AddMinutes(30), day.AddDays(2).AddHours(15).AddMinutes(10),
                false, false, TimeSpan.Zero, false, "会议室 D407") { Source = work },
            new(new Resource("", ""), "周度同步会", day.AddDays(3).AddHours(8).AddMinutes(55), day.AddDays(3).AddHours(11).AddMinutes(40),
                false, false, TimeSpan.Zero, false, "会议室 A102") { Source = work }
        };

        var agendaSize = CalendarCard.Sizes[(int)Variant.Agenda];
        var agenda = new CalendarCard(Variant.Agenda, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        agenda.Measure(agendaSize);
        agenda.Arrange(new Rect(agendaSize));
        agenda.UpdateLayout();
        var agendaTexts = Tree(agenda).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(agendaTexts.Count(t => t.Contains("行程时间 15 分钟")) >= 3, "日程列表应同时显示今天和明天的行程时间");
        Check(agendaTexts.Contains("报告厅 A101") && agendaTexts.Contains("会议室 B202") && agendaTexts.Contains("阶梯教室 C303"), "日程列表应显示地点");

        var compactSize = CalendarCard.Sizes[(int)Variant.Compact];
        var compact = new CalendarCard(Variant.Compact, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        compact.Measure(compactSize);
        compact.Arrange(new Rect(compactSize));
        compact.UpdateLayout();
        var compactTexts = Tree(compact).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(compactTexts.Contains("行程时间 15 分钟") && compactTexts.Contains("线上会议室"), "紧凑摘要应显示行程时间与地点");

        var stSize = CalendarCard.Sizes[(int)Variant.ShortTimeline];
        var st = new CalendarCard(Variant.ShortTimeline, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        st.Measure(stSize);
        st.Arrange(new Rect(stSize));
        st.UpdateLayout();
        var stTexts = Tree(st).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(stTexts.Contains("其他 1 个日程"), "双日时间线超出底部窗口的日程应显示 其他 1 个日程");
        Check(stTexts.Contains("报告厅 A101") && stTexts.Contains("会议室 B202"), "时间线事件块应显示地点");

        var monthSize = CalendarCard.Sizes[(int)Variant.Month];
        var monthCard = new CalendarCard(Variant.Month, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        monthCard.Measure(monthSize);
        monthCard.Arrange(new Rect(monthSize));
        monthCard.UpdateLayout();
        var smallDots = Tree(monthCard).OfType<System.Windows.Shapes.Ellipse>().Count(e => e.Width < 5);
        Check(smallDots == 0, "月历默认不显示多余日程圆点");
    }

    private static void CheckMiddayTodayFirstScenario(string? outDir)
    {
        var day = new DateTime(2026, 11, 26);
        var nowTime = day.AddHours(12).AddMinutes(5);
        var work = new CalendarSource("work", "工作", "#000000", false);

        var items = new List<Entry>
        {
            new(new Resource("", ""), "晨会同步", day.AddHours(8).AddMinutes(55), day.AddHours(11).AddMinutes(40),
                false, false, TimeSpan.FromMinutes(15), false, "报告厅 A101") { Source = work },
            new(new Resource("", ""), "技术方案研讨", day.AddHours(13).AddMinutes(30), day.AddHours(15).AddMinutes(10),
                false, false, TimeSpan.FromMinutes(15), false, "会议室 B202") { Source = work },
            new(new Resource("", ""), "架构设计分享", day.AddHours(20), day.AddHours(21).AddMinutes(30),
                false, false, TimeSpan.FromMinutes(15), false, "阶梯教室 C303") { Source = work },
            new(new Resource("", ""), "性能调优实践与总结", day.AddDays(1).AddHours(13).AddMinutes(30), day.AddDays(1).AddHours(17).AddMinutes(10),
                false, false, TimeSpan.FromMinutes(15), false, "会议室 D407") { Source = work },
            new(new Resource("", ""), "周度同步会", day.AddDays(2).AddHours(8).AddMinutes(55), day.AddDays(2).AddHours(11).AddMinutes(40),
                false, false, TimeSpan.Zero, false, "会议室 A102") { Source = work }
        };

        var compactSize = CalendarCard.Sizes[(int)Variant.Compact];
        var compact = new CalendarCard(Variant.Compact, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        compact.Measure(compactSize);
        compact.Arrange(new Rect(compactSize));
        compact.UpdateLayout();
        var compactTexts = Tree(compact).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(compactTexts.Contains("技术方案研讨") && compactTexts.Contains("架构设计分享"), "紧凑摘要应优先展示今天的全部 2 条未结束日程");
        Check(compactTexts.Contains("会议室 B202"), "紧凑摘要首条应保留地点");
        Check(!compactTexts.Contains("晨会同步"), "已结束的日程不应出现在紧凑摘要");

        var sumSize = CalendarCard.Sizes[(int)Variant.Summary];
        var summary = new CalendarCard(Variant.Summary, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        summary.Measure(sumSize);
        summary.Arrange(new Rect(sumSize));
        summary.UpdateLayout();
        var sumTexts = Tree(summary).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(sumTexts.Contains("技术方案研讨") && sumTexts.Contains("架构设计分享"), "双栏摘要应把今天第 2 条溢流到右栏展示");
        Check(sumTexts.IndexOf("架构设计分享") < sumTexts.IndexOf("明天"), "今天溢流日程必须排在明天之前");
        Check(sumTexts.Contains("阶梯教室 C303"), "右栏延续的今天日程应仍保留地点");
        Check(sumTexts.Contains("性能调优实践与总结"), "右栏排完今天后应顺延显示明天日程");

        var stSize = CalendarCard.Sizes[(int)Variant.ShortTimeline];
        var st = new CalendarCard(Variant.ShortTimeline, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        st.Measure(stSize);
        st.Arrange(new Rect(stSize));
        st.UpdateLayout();
        var stTexts = Tree(st).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(!stTexts.Contains("明天"), "双日时间线当天下午/晚上仍有日程时，右列应延续今天而不显示明天");
        Check(stTexts.Contains("技术方案研讨") && stTexts.Contains("架构设计分享"), "双日时间线应两列接力展示今天的全部未结束日程");
        Check(stTexts.Contains("17 时") && stTexts.Contains("22 时"), "双日时间线右列应延续 17 时 – 22 时");

        var wkSize = CalendarCard.Sizes[(int)Variant.Week];
        var wk = new CalendarCard(Variant.Week, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        wk.Measure(wkSize);
        wk.Arrange(new Rect(wkSize));
        wk.UpdateLayout();
        var wkTexts = Tree(wk).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(wkTexts.Contains("星期五") && wkTexts.Contains("星期六") && !wkTexts.Contains("星期日"), "四日时间线今天占 2 列，右侧展示未来 2 天");
        Check(wkTexts.Contains("架构设计分享") && wkTexts.Contains("性能调优实践与总结"), "四日时间线应同时展示今晚和明天日程");

        var agSize = CalendarCard.Sizes[(int)Variant.Agenda];
        var ag = new CalendarCard(Variant.Agenda, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        ag.Measure(agSize);
        ag.Arrange(new Rect(agSize));
        ag.UpdateLayout();
        var agTexts = Tree(ag).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(!agTexts.Contains("晨会同步"), "日程列表不应显示今天已结束日程");
        Check(agTexts.Contains("其他 1 个日程"), "日程列表首屏底部应将放不下的 11月28日 折叠为 其他 1 个日程");
    }

    private static void CheckAfternoonElasticTimelineScenario(string? outDir)
    {
        // Mirrors 15:59 scenario: today has a just-ended 13:30-15:10 event and evening 20:00-21:30 event (span 15..22 = 7h),
        // tomorrow has 13:30-17:10, day after tomorrow has 08:55-11:40.
        var day = new DateTime(2026, 11, 26);
        var nowTime = day.AddHours(15).AddMinutes(59);
        var work = new CalendarSource("work", "工作", "#000000", false);

        var items = new List<Entry>
        {
            new(new Resource("", ""), "技术方案研讨", day.AddHours(13).AddMinutes(30), day.AddHours(15).AddMinutes(10),
                false, false, TimeSpan.FromMinutes(15), false, "会议室 B202") { Source = work },
            new(new Resource("", ""), "架构设计分享", day.AddHours(20), day.AddHours(21).AddMinutes(30),
                false, false, TimeSpan.FromMinutes(15), false, "阶梯教室 C303") { Source = work },
            new(new Resource("", ""), "性能调优实践与总结", day.AddDays(1).AddHours(13).AddMinutes(30), day.AddDays(1).AddHours(17).AddMinutes(10),
                false, false, TimeSpan.FromMinutes(15), false, "会议室 D407") { Source = work },
            new(new Resource("", ""), "周度同步会", day.AddDays(2).AddHours(8).AddMinutes(55), day.AddDays(2).AddHours(11).AddMinutes(40),
                false, false, TimeSpan.FromMinutes(15), false, "会议室 A102") { Source = work }
        };

        // 4-day timeline should stretch today to 15..22 (1 column!) and show Fri/Sat/Sun (3 future columns!) anchored on 13..18
        var wkSize = CalendarCard.Sizes[(int)Variant.Week];
        var wk = new CalendarCard(Variant.Week, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        wk.Measure(wkSize);
        wk.Arrange(new Rect(wkSize));
        wk.UpdateLayout();
        var wkTexts = Tree(wk).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(wkTexts.Contains("15 时") && wkTexts.Contains("22 时"), "15:59 四日时间线左列应弹性拉伸至 15 时 – 22 时");
        Check(wkTexts.Contains("星期五") && wkTexts.Contains("星期六") && wkTexts.Contains("星期日"), "单列装得下今天时，四日时间线右侧应保留未来 3 天");
        Check(wkTexts.Contains("13 时") && wkTexts.Contains("18 时") && !wkTexts.Contains("8 时"), "右侧时间轴应锚定明天 13 时 – 18 时，不被后天早课截断");
        Check(wkTexts.Contains("技术方案研讨") && wkTexts.Contains("架构设计分享") && wkTexts.Contains("性能调优实践与总结"), "左列与右列核心日程均应完整显示");

        // 2-day timeline should show Today (15..22) on left + Tomorrow (13..18) on right!
        var stSize = CalendarCard.Sizes[(int)Variant.ShortTimeline];
        var st = new CalendarCard(Variant.ShortTimeline, day, nowTime, items, _ => { }, _ => { }, _ => { }, () => { }, dark: false).Root;
        st.Measure(stSize);
        st.Arrange(new Rect(stSize));
        st.UpdateLayout();
        var stTexts = Tree(st).OfType<TextBlock>().Select(t => t.Text).ToList();
        Check(stTexts.Contains("明天"), "15:59 双日时间线单列装得下今天时，右列应显示明天");
        Check(stTexts.Contains("架构设计分享") && stTexts.Contains("性能调优实践与总结"), "双日时间线应同时展示今晚与明天日程");

        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
            SaveVisual(wk, wkSize, Path.Combine(outDir, "week-1559.png"));
            SaveVisual(st, stSize, Path.Combine(outDir, "short-1559.png"));
        }
    }

    private static void RenderMonthComparison(string outDir)
    {
        var day = new DateTime(2026, 11, 26);
        var size = CalendarCard.Sizes[(int)Variant.Month];
        var plain = new CalendarCard(Variant.Month, day, day, [], _ => { }, _ => { }, _ => { }, () => { }, dark: false, detailedLunar: false).Root;
        var lunar = new CalendarCard(Variant.Month, day, day, [], _ => { }, _ => { }, _ => { }, () => { }, dark: false, detailedLunar: true).Root;
        var lunarDark = new CalendarCard(Variant.Month, day, day, [], _ => { }, _ => { }, _ => { }, () => { }, dark: true, detailedLunar: true).Root;

        var board = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        foreach (var card in new[] { plain, lunar, lunarDark })
        {
            card.Width = size.Width;
            card.Height = size.Height;
            card.Margin = new Thickness(12);
            board.Children.Add(card);
        }
        double w = (size.Width + 24) * 3, h = size.Height + 24;
        board.Measure(new Size(w, h));
        board.Arrange(new Rect(0, 0, w, h));
        board.UpdateLayout();
        SaveRawVisual(board, new Size(w, h), Path.Combine(outDir, "month-compare.png"));
    }

    private static void RenderSettingsPreview(string outDir, Entry sampleEntry)
    {
        var dummyOwner = new Window { Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowInTaskbar = false, AllowsTransparency = true, Background = Brushes.Transparent, Left = -10000, Top = -10000 };
        dummyOwner.Show();
        try
        {
            foreach (var (theme, suffix) in new[] { (ThemePreference.Light, "light"), (ThemePreference.Dark, "dark") })
            {
                var sampleWidgets = new List<WidgetInstance>
                {
                    new() { Id = "w1", Variant = Variant.Summary, Left = 1420, Top = 28, Scale = 0.85 },
                    new() { Id = "w2", Variant = Variant.Month, Left = 1420, Top = 208, Scale = 0.85 }
                };
                bool uniform = true;
                bool snap = true;
                double gScale = 0.85;
                var curTheme = theme;
                var curTravel = TravelDisplayMode.TodayOnly;
                double StepPx() => Math.Round(WidgetInstance.GridStep * (uniform ? gScale : sampleWidgets[0].Scale));

                var host = new SettingsHost
                {
                    GetWidgets = () => sampleWidgets.ToList(),
                    AddWidget = v =>
                    {
                        var added = new WidgetInstance { Id = "w" + (sampleWidgets.Count + 1), Variant = v, Left = 1420 + StepPx(), Top = 208, Scale = gScale };
                        sampleWidgets.Add(added);
                        return added;
                    },
                    RemoveWidget = id => { if (sampleWidgets.Count > 1) sampleWidgets.RemoveAll(x => x.Id == id); },
                    UpdateWidget = (id, mutate, _) => { var target = sampleWidgets.FirstOrDefault(x => x.Id == id); if (target != null) mutate(target); },
                    GetUniformScale = () => uniform,
                    SetUniformScale = on => uniform = on,
                    GetGlobalScale = () => gScale,
                    SetGlobalScale = s => gScale = s,
                    GetSnapToGrid = () => snap,
                    SetSnapToGrid = on => snap = on,
                    GetGridCell = w => w == sampleWidgets[0] ? (0, 0) : ((int)Math.Round((w.Left - sampleWidgets[0].Left) / StepPx()), (int)Math.Round((w.Top - sampleWidgets[0].Top) / StepPx())),
                    GetGridStepPixels = StepPx,
                    GetGridGapPixels = () => Math.Round(WidgetInstance.GridGap * (uniform ? gScale : sampleWidgets[0].Scale)),
                    SnapWidgetToGrid = id =>
                    {
                        var w = sampleWidgets.First(x => x.Id == id);
                        int c = (int)Math.Round((w.Left - sampleWidgets[0].Left) / StepPx());
                        int r = (int)Math.Round((w.Top - sampleWidgets[0].Top) / StepPx());
                        w.Left = Math.Round(sampleWidgets[0].Left + c * StepPx());
                        w.Top = Math.Round(sampleWidgets[0].Top + r * StepPx());
                    },
                    SnapAllToGrid = () => { },
                    NudgeWidgetOnGrid = (id, dc, dr) =>
                    {
                        var w = sampleWidgets.First(x => x.Id == id);
                        w.Left = Math.Round(w.Left + dc * StepPx());
                        w.Top = Math.Round(w.Top + dr * StepPx());
                    },
                    GetTheme = () => curTheme,
                    SetTheme = t => curTheme = t,
                    GetTravelMode = () => curTravel,
                    SetTravelMode = m => curTravel = m,
                    GetMonthDots = () => false,
                    SetMonthDots = _ => { },
                    GetOpacity = () => 1.0,
                    SetOpacity = _ => { },
                    GetAccount = () => new Account("demo@icloud.com", "", [new CloudCalendar("https://caldav.icloud.com/w/", "工作", true, "#278AD1")]),
                    GetSyncText = () => "上次同步 22:20",
                    GetReadErrors = () => new Dictionary<string, int>(),
                    GetSources = () =>
                    [
                        (CalendarSource.Local, true),
                        (new CalendarSource("w", "工作", "#278AD1", true), true)
                    ],
                    SetSourceVisible = (_, _) => { },
                    ConnectAccount = _ => false,
                    DisconnectAccount = () => { },
                    SyncNow = () => Task.CompletedTask,
                    ResetAllPositions = () => { },
                    GoToday = () => { },
                    GetDataFolder = () => @"D:\MyCalendarData",
                    ChangeDataFolder = (_, _) => Task.FromResult(false)
                };

                var win = new SettingsWindow(dummyOwner, host) { Left = -10000, Top = -10000 };
                win.Show();
                win.UpdateLayout();
                var content = (FrameworkElement)win.Content;
                SaveRawVisual(content, new Size(content.ActualWidth, content.ActualHeight), Path.Combine(outDir, $"settings-{suffix}.png"));

                if (theme == ThemePreference.Light)
                {
                    void FlushDispatcher() => win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    void PressButton(string label)
                    {
                        var btn = Tree(win).OfType<Button>().First(b => Equals(b.Content, label));
                        btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        FlushDispatcher();
                        win.UpdateLayout();
                    }

                    PressButton("+ 添加小组件");
                    Check(sampleWidgets.Count == 3, "点击添加小组件应增加到 3 个");
                    double beforeX = sampleWidgets[1].Left;
                    PressButton("→");
                    Check(Math.Abs(sampleWidgets[1].Left - (beforeX + StepPx())) < 1, "按网格右移应精确移动一个 Apple 网格步距");
                    PressButton("80%");
                    Check(Math.Abs(gScale - 0.80) < 0.001, "快捷缩放按钮 80% 应生效");
                    var uniformCb = Tree(win).OfType<CheckBox>().First();
                    uniformCb.IsChecked = false;
                    FlushDispatcher();
                    win.UpdateLayout();
                    Check(!uniform, "关闭统一大小开关应生效");
                    PressButton("移除");
                    Check(sampleWidgets.Count == 2, "移除小组件应减少到 2 个");

                    PressButton("外观与显示");
                    PressButton("深色");
                    Check(curTheme == ThemePreference.Dark, "切换深色主题不应崩溃并生效");
                    PressButton("浅色");
                    Check(curTheme == ThemePreference.Light, "切换浅色主题不应崩溃并生效");
                    PressButton("始终");
                    Check(curTravel == TravelDisplayMode.Always, "切换行程时间提示不应崩溃并生效");
                    PressButton("iCloud 与日历");
                    Check(Tree(win).OfType<TextBlock>().Any(t => t.Text == "iCloud 账户"), "切换到 iCloud 导航不应崩溃");
                    PressButton("常规");
                    Check(Tree(win).OfType<TextBlock>().Any(t => t.Text == "开机自动启动"), "切换到常规导航不应崩溃");
                    PressButton("小组件与布局");
                    Check(Tree(win).OfType<TextBlock>().Any(t => t.Text == "小组件与布局"), "切回小组件与布局导航不应崩溃");
                }

                win.Close();

                var ro = Dialogs.BuildReadOnlyEventWindow(dummyOwner, sampleEntry, theme == ThemePreference.Dark);
                ro.Left = -10000;
                ro.Top = -10000;
                ro.Show();
                ro.UpdateLayout();
                var roContent = (FrameworkElement)ro.Content;
                SaveRawVisual(roContent, new Size(roContent.ActualWidth, roContent.ActualHeight), Path.Combine(outDir, $"inspector-{suffix}.png"));
                ro.Close();
            }
        }
        finally
        {
            dummyOwner.Close();
        }
    }

    private static void SaveRawVisual(FrameworkElement element, Size size, string path)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        const double scale = 1.5;
        var bmp = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void SaveVisual(FrameworkElement element, Size size, string path)
    {
        var wrap = new Border { Width = size.Width + 24, Height = size.Height + 24, Padding = new Thickness(12), Background = Brushes.Transparent };
        element.Width = size.Width;
        element.Height = size.Height;
        wrap.Child = element;
        wrap.Measure(new Size(wrap.Width, wrap.Height));
        wrap.Arrange(new Rect(0, 0, wrap.Width, wrap.Height));
        wrap.UpdateLayout();

        const double scale = 2.0;
        var bmp = new RenderTargetBitmap((int)(wrap.Width * scale), (int)(wrap.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(wrap);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
