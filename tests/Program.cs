using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using WinMacCalendar;

internal static partial class Program
{
    private static readonly DateTime Day = new(2026, 7, 17);
    private static readonly DateTime Now = Day.AddHours(19).AddMinutes(28);
    private static void Check(bool pass, string detail)
    {
        if (!pass) throw new Exception(detail);
    }
    private static Resource FileAt(string title, DateTime start, DateTime end, bool allDay = false) =>
        new(Guid.NewGuid() + ".ics", Events.Write(null, title, start, end, allDay));
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--check-cache", var path]) { CheckCache(path); return; }
        if (args.FirstOrDefault() is "--desktop" or "--desktop-multi") { DesktopCheck(args[0]=="--desktop-multi"); return; }
        Check(Dates.Parts(Day) == ("六月", "初四"), "参考图农历");
        Check(Dates.Parts(new(2025, 7, 25)) == ("闰六月", "初一"), "闰月初一");
        Check(Dates.GridStart(new(2026, 3, 15)) == new DateTime(2026, 2, 23), "跨月网格");
        var examples = new[]{
            FileAt("设计评审",Day.AddHours(20),Day.AddHours(21)),
            FileAt("项目交付",Day,Day.AddDays(1),true),
            FileAt("项目讨论",Day.AddDays(1).AddHours(19),Day.AddDays(1).AddHours(20.5)),
            FileAt("读书会",Day.AddDays(2).AddHours(20),Day.AddDays(2).AddHours(21)),
            FileAt("出差",Day.AddDays(2),Day.AddDays(4),true)};
        var entries = examples.SelectMany(f => Events.Expand(f, Day, Day.AddDays(7))).ToList();
        var localBounds = DateTime.SpecifyKind(Day,DateTimeKind.Local);
        Check(Events.Expand(examples[0],localBounds,localBounds.AddDays(32)).Count==1,"从今天开始的时间线");
        Check(entries.Count == 5 && entries[0].Start == Day.AddHours(20), "本地时间往返");
        Check(entries[4].On(Day.AddDays(3)) && !entries[4].On(Day.AddDays(4)), "全天排他结束日");
        var ongoing = FileAt("跨月", Day.AddDays(-20), Day.AddDays(4), true);
        Check(Events.Expand(ongoing, Day, Day.AddDays(1)).Count == 1, "跨视口日程");
        var recurring = examples[0] with { Ics = examples[0].Ics.Replace("END:VEVENT", "RRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT") };
        var series = Events.Expand(recurring, Day, Day.AddDays(7));
        Check(series.Count == 3 && series.All(e => !e.Editable), "重复日程展开及编辑保护");
        var changed = Events.Write(examples[0], "变更后的日程", Day, Day.AddDays(1), true);
        Check(Events.Expand(examples[0] with { Ics = changed }, Day, Day.AddDays(1)).Single().AllDay, "定时转全天");
        foreach (string host in new[] { "caldav.icloud.com", "p01-caldav.icloud.com.cn" })
            SyncChecks(examples[0].Ics, host).GetAwaiter().GetResult();
        var travel = FileAt("测试日程 2（有行程时间）", Day.AddHours(20), Day.AddHours(21));
        travel = travel with { Ics = travel.Ics.Replace("END:VEVENT","X-APPLE-TRAVEL-DURATION:PT15M\r\nEND:VEVENT") };
        var schedule = new[] { travel, FileAt("全天日程 1", Day, Day.AddDays(1), true),
            FileAt("测试日程 2",Day.AddDays(1).AddHours(14),Day.AddDays(1).AddHours(17).AddMinutes(10)),
            FileAt("跨天全天日程",Day.AddDays(2),Day.AddDays(4),true),
            FileAt("测试日程 3",Day.AddDays(2).AddHours(13.5),Day.AddDays(2).AddHours(16.5)),
            FileAt("测试日程 4",Day.AddDays(3).AddHours(17),Day.AddDays(3).AddHours(18)) };
        var shown = schedule.SelectMany(f => Events.Expand(f,Day,Day.AddDays(8))).ToList();
        Check(shown[0].Travel == TimeSpan.FromMinutes(15), "事件行程时间");
        var work = new CalendarSource("example-work","工作","#278AD1",true);
        var personal = new CalendarSource("example-personal","个人","#D72B48",true);
        shown = shown.Select(e => e with { Source = e.File==travel || e.File==schedule[1] ? work : personal }).ToList();
        RenderChecks(shown, args.FirstOrDefault(), dark: false);
        var holiday = FileAt("大暑", Day.AddDays(6), Day.AddDays(7), true);
        holiday = holiday with { Ics = holiday.Ics.Replace("END:VEVENT", "CATEGORIES:HOLIDAY\r\nEND:VEVENT") };
        RenderChecks(Events.Expand(holiday, Day, Day.AddDays(8)).Select(e => e with { Source = new CalendarSource("example-holidays", "节假日", "#F29323", false) }).ToList(), args.ElementAtOrDefault(1), dark: false);
        if (args.ElementAtOrDefault(2) is { Length: > 0 } darkPreview)
            RenderChecks(shown, darkPreview, dark: true);
        CheckMonthLayouts();
        CheckTimelineLayers(null);
        CheckSummaryAndCourseScenario(args.ElementAtOrDefault(3));
        CheckSyncErrorAndMenuBounds();
        Console.WriteLine("通过：农历、日期边界、日程编辑、重复日程、SQLite、CalDAV 同步、七种 WPF 视图、月历交互、双栏摘要智能排布及右键菜单宽度边界。");
    }

    private static void CheckSyncErrorAndMenuBounds()
    {
        var dnsErr = new HttpRequestException(HttpRequestError.NameResolutionError, "不知道这样的主机。 (caldav.icloud.com:443)");
        var (netSummary, netDetail) = MainWindow.FormatSyncFailure([
            ("日历名称与颜色", dnsErr),
            ("家庭", dnsErr),
            ("工作", dnsErr),
            ("个人", dnsErr),
            ("节假日", dnsErr)
        ]);
        Check(netSummary == "同步失败 · 网络暂不可用", "多日历同类网络报错应去重精简：" + netSummary);
        Check(netDetail.Contains("共 5 项失败"), "详情应保留失败项统计：" + netDetail);

        var authErr = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        var longErr = new InvalidOperationException(new string('错', 300));
        var (mixedSummary, mixedDetail) = MainWindow.FormatSyncFailure([
            ("工作", authErr),
            ("个人", longErr)
        ]);
        Check(mixedSummary.Length <= 28 && mixedSummary.Contains("账户验证失败"), "混合报错摘要应精简：" + mixedSummary);
        Check(mixedDetail.Contains("工作：") && mixedDetail.Contains("个人："), "混合报错详情应包含各日历");

        // Verify ContextMenu and StatusMenuItem never stretch to full screen width even with huge text.
        var menu = new ContextMenu();
        ThemeResources.Apply(menu, false);
        menu.Items.Add(MainWindow.StatusMenuItem(new string('错', 400), mixedDetail));
        menu.Items.Add(new MenuItem { Header = new string('A', 400), IsEnabled = false });
        menu.Measure(new Size(4000, 2000));
        Check(menu.DesiredSize.Width <= 285, $"右键菜单宽度应受控于 285px 以内，实际 {menu.DesiredSize.Width:F1}");
    }
    private static void CheckCache(string path)
    {
        var options = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
            DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false };
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(options.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT ics FROM resources WHERE pending <> 'delete'";
        using var reader = query.ExecuteReader();
        var failures = new Dictionary<string,int>();
        int total = 0, visible = 0;
        var start = DateTime.Today;
        while (reader.Read())
        {
            total++;
            try { visible += Events.Expand(new Resource("",reader.GetString(0)),start,start.AddDays(32)).Count; }
            catch (Exception ex)
            {
                var error = ex.GetBaseException();
                var reason = error.GetType().Name + " " + error.TargetSite?.DeclaringType?.Name + "." + error.TargetSite?.Name;
                failures[reason] = failures.GetValueOrDefault(reason) + 1;
            }
        }
        Console.WriteLine($"缓存文件 {total}，时间线读取范围内的日程 {visible}，解析失败 {failures.Values.Sum()}");
        foreach (var failure in failures) Console.WriteLine($"{failure.Key}: {failure.Value}");
    }

    private static void DesktopCheck(bool multiple)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string path = Path.Combine(Path.GetTempPath(), "calendar-check-" + Guid.NewGuid() + ".db");
        var store = new CalendarStore(path);
        string targetScope = CalendarStore.Local;
        if (multiple)
        {
            var work = new CloudCalendar("https://caldav.icloud.com/work/","工作",true,"#FF5522FF");
            var shared = new CloudCalendar("https://caldav.icloud.com/shared/","订阅",false,"#9966CCFF");
            // Deliberately invalid secret keeps this UI exercise entirely offline.
            var demo = new Account("test@example.invalid","!",[work,shared]);
            store.Set("account",System.Text.Json.JsonSerializer.Serialize(demo));
            store.Set("hiddenCalendars","[]");
            targetScope=demo.ScopeFor(work);
            foreach (var cal in demo.Calendars)
                store.Save(demo.ScopeFor(cal),FileAt(cal.Name,DateTime.Today.AddHours(9),DateTime.Today.AddHours(10)));
        }
        var window = new MainWindow(store);
        Exception? failure = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        int stage = 0;
        var timeout = DateTime.UtcNow.AddSeconds(15);
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > timeout) throw new TimeoutException("桌面交互超时，步骤 " + stage);
                var modal = app.Windows.OfType<Window>().FirstOrDefault(w => w != window && w.IsVisible);
                if (stage == 0 && window.IsVisible)
                {
                    var today = DateTime.Today;
                    var button = Tree(window).OfType<Button>().Single(b => AutomationProperties.GetName(b).StartsWith(today.ToString("M月d日") + " "));
                    stage++; window.Dispatcher.BeginInvoke(() => Click(button));
                }
                else if (stage == 1 && modal != null)
                {
                    if (multiple) foreach (string name in new[]{"工作","订阅"})
                        Check(Tree(modal).OfType<Button>().Any(b=>Equals(b.ToolTip,name)),"两本日历同时显示");
                    var add = Tree(modal).OfType<Button>().Single(b => Equals(b.Content, "新建日程"));
                    stage++; window.Dispatcher.BeginInvoke(() => Click(add));
                }
                else if (stage == 2 && modal?.Title == "新建日程")
                {
                    Tree(modal).OfType<TextBox>().First().Text = "窗口交互测试";
                    if (multiple)
                    {
                        var picker=Tree(modal).OfType<ComboBox>().Single(b=>b.Items.OfType<ComboBoxItem>().Any(i=>i.Tag is CalendarSource));
                        var choices=picker.Items.Cast<ComboBoxItem>().ToArray();
                        Check(choices.Length==2 && choices.All(i=>((CalendarSource)i.Tag).Writable),"仅本地及可写日历可选");
                        picker.SelectedItem=choices.Single(i=>((CalendarSource)i.Tag).Scope==targetScope);
                    }
                    var save = Tree(modal).OfType<Button>().Single(b => Equals(b.Content, "保存"));
                    stage++; window.Dispatcher.BeginInvoke(() => Click(save));
                }
                else if (stage == 3 && modal == null)
                {
                    Check(store.List(targetScope).Any(r=>r.Ics.Contains("窗口交互测试")), "界面保存");
                    stage++; timer.Stop(); window.Close(); app.Shutdown();
                }
            }
            catch (Exception e) { failure = e; timer.Stop(); app.Shutdown(); }
        };
        timer.Start(); app.Run(window);
        if (failure != null) throw failure;
        Check(stage == 4, "桌面窗口生命周期");
        using (var reopened = new CalendarStore(path)) Check(reopened.List(targetScope).Count(r=>r.Ics.Contains("窗口交互测试")) == 1, "重启后日程保留");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path);
        Console.WriteLine("通过：启动原生窗口、打开当天、创建并保存日程、关闭程序、重新打开 SQLite 验证持久化。");
    }
    private static async Task SyncChecks(string ics, string host)
    {
        string collection = $"https://{host}/sample/calendar/";
        string href = collection + "example.ics";
        using var db = new CalendarStore(":memory:");
        var tests = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        var calendar = new CloudCalendar(collection, "测试日历", true);
        var account = new Account("demo@example.com", "", [calendar]);
        string scope = account.ScopeFor(calendar);
        var d = (XNamespace)"DAV:"; var c = (XNamespace)"urn:ietf:params:xml:ns:caldav";
        XElement Row(params XElement[] props) => new(d + "response", new XElement(d + "href", href), new XElement(d + "propstat",
            new XElement(d + "prop", props), new XElement(d + "status", "HTTP/1.1 200 OK")));
        string Multi(params XElement[] rows) => new XElement(d + "multistatus", rows).ToString();
        void Reply(string method, int code, string body, string? required = null, string? tag = null, string? expectedUrl = null)
        {
            tests.Enqueue(r =>
            {
                if (expectedUrl != null) Check(r.RequestUri?.AbsoluteUri == expectedUrl, "请求指向所发现的日历服务器");
                Check(r.Method.Method == method, "意外的 HTTP 方法 " + r.Method);
                if (required != null) Check((r.ToString() + r.Content?.ReadAsStringAsync().Result).Contains(required), "请求缺少 " + required);
                var response = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8), RequestMessage = r };
                if (tag != null) response.Headers.ETag = new(tag);
                return response;
            });
        }
        using var cloud = new ICloud(account.Email, "example", new Server(tests));
        string hrefProp(string prop, string target) => Multi(Row(new XElement(prop == "calendar-home-set" ? c + prop : d + prop, new XElement(d + "href", target))));
        Reply("PROPFIND", 207, hrefProp("current-user-principal", "/sample/"), "current-user-principal");
        Reply("PROPFIND", 207, hrefProp("calendar-home-set", $"https://{host}/sample/"), "calendar-home-set");
        var calendarRow = Row(new XElement(d + "resourcetype", new XElement(c + "calendar")),
            new XElement(d + "displayname", "工作"), new XElement(d + "current-user-privilege-set", new XElement(d + "privilege", new XElement(d + "write"))));
        calendarRow.Element(d + "href")!.Value = "calendar/";
        var apple=(XNamespace)"http://apple.com/ns/ical/";
        calendarRow.Descendants(d+"prop").Single().Add(new XElement(apple+"calendar-color","#CA3244FF"));
        var sharedRow=new XElement(calendarRow);
        sharedRow.Element(d+"href")!.Value="shared/";
        sharedRow.Descendants(apple+"calendar-color").Single().Value="#118833CC";
        sharedRow.Descendants(d+"current-user-privilege-set").Remove();
        Reply("PROPFIND", 207, Multi(calendarRow,sharedRow), "calendar-color", expectedUrl: $"https://{host}/sample/");
        var discovered = await cloud.Discover(default);
        var found = discovered.First();
        Check(discovered.Count==2 && !discovered[1].Writable && CalendarSource.ColorOf(discovered[1])=="#118833","多日历发现与 RGBA 颜色");
        var legacy=Account.Load(System.Text.Json.JsonSerializer.Serialize(new { account.Email,account.Secret,Calendar=calendar }))!;
        Check(legacy.ScopeFor(legacy.Calendars.Single())==scope,"旧单日历设置和缓存兼容");
        Check(found is { Name: "工作", Writable: true } && found.Url == collection, "日历发现");
        Reply("REPORT", 207, Multi(Row(new XElement(d + "getetag", "\"1\"")),
            new XElement(d + "response", new XElement(d + "href", collection), new XElement(d + "status", "HTTP/1.1 507 Insufficient Storage")),
            new XElement(d + "sync-token", "page-2")), "sync-collection");
        Reply("REPORT", 207, Multi(new XElement(d + "sync-token", "t1")), "page-2");
        Reply("REPORT", 207, Multi(Row(new XElement(d + "getetag", "\"1\""), new XElement(c + "calendar-data", ics))), "calendar-multiget");
        await cloud.Sync(db, account, calendar, default);
        var other=discovered[1];
        Reply("REPORT",207,Multi(new XElement(d+"sync-token","shared-token")),"sync-collection",expectedUrl:other.Url);
        await cloud.Sync(db,account,other,default);
        Check(db.Get("token:"+scope)=="t1" && db.Get("token:"+account.ScopeFor(other))=="shared-token","每本日历独立保存增量标记");
        var item = db.List(scope).Single(); Check(item.Ics.ReplaceLineEndings() == ics.ReplaceLineEndings() && item.Etag == "\"1\"", "首次同步");
        db.Save(scope, item with { Pending = "put" });
        Reply("PUT", 412, "", "If-Match: \"1\"");
        Reply("GET", 200, ics.Replace("设计评审", "远端优先"), tag: "\"2\"");
        Reply("REPORT", 207, Multi(new XElement(d + "sync-token", "t2")), "t1");
        await cloud.Sync(db, account, calendar, default);
        Check(db.List(scope).Single().Ics.Contains("远端优先"), "ETag 冲突保留远端");
        Reply("REPORT", 207, Multi(new XElement(d + "response", new XElement(d + "href", href),
            new XElement(d + "status", "HTTP/1.1 404 Not Found")), new XElement(d + "sync-token", "t3")), "t2");
        await cloud.Sync(db, account, calendar, default); Check(db.List(scope).Count == 0, "远端删除");
        db.Save(scope, new(href, ics, "", "put")); Reply("PUT", 503, "", "If-None-Match: *");
        try { await cloud.Sync(db, account, calendar, default); throw new Exception("应报告同步失败"); } catch (HttpRequestException) { }
        Check(db.List(scope).Single().Pending == "creating", "失败后保留待同步更改");
        Reply("PUT", 201, "", "If-None-Match: *"); Reply("GET", 200, ics, tag: "\"3\"");
        Reply("REPORT", 403, new XElement(d + "error", new XElement(d + "valid-sync-token")).ToString(), "t3");
        Reply("REPORT", 207, Multi(Row(new XElement(d + "getetag", "\"3\"")), new XElement(d + "sync-token", "t4")));
        await cloud.Sync(db, account, calendar, default);
        Check(db.List(scope).Single().Pending == "" && db.Get("token:" + scope) == "t4", "重试和过期标记");
        db.Save(scope, db.List(scope).Single() with { Pending = "delete" });
        Reply("DELETE", 204, "", "If-Match: \"3\""); Reply("REPORT", 207, Multi(new XElement(d + "sync-token", "t5")), "t4");
        await cloud.Sync(db, account, calendar, default); Check(tests.Count == 0 && db.List(scope).Count == 0, "删除推送");
        db.Save(scope, new(href, ics, "", "delete"));
        Reply("GET", 200, ics, tag: "\"4\"");
        Reply("DELETE", 204, "", "If-Match: \"4\"");
        Reply("REPORT", 207, Multi(new XElement(d + "sync-token", "t6")), "t5");
        await cloud.Sync(db, account, calendar, default);
        Check(db.List(scope).Count == 0 && tests.Count == 0, "取消未收到上传回执的日程");
        // A redirect to the regional host may precede DAV discovery.
        tests.Enqueue(r => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            RequestMessage = r, Headers = { Location = new Uri($"https://{host}/principal/") }
        });
        Reply("PROPFIND", 207, hrefProp("current-user-principal", "https://calendar.example/account/"),
            "current-user-principal", expectedUrl: $"https://{host}/principal/");
        try
        {
            await cloud.Discover(default);
            throw new Exception("应拒绝非 iCloud 日历域名");
        }
        catch (InvalidDataException ex)
        {
            Check(ex.Message.Contains("calendar.example") && !ex.Message.Contains("/account/"), "错误提示只显示域名");
        }
        Check(tests.Count == 0, "区域重定向后仍校验后续地址");
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var obj in Tree(VisualTreeHelper.GetChild(root, i))) yield return obj;
    }
    private static void CheckMonthLayouts()
    {
        // February fits four weeks in 2027; March 2026 needs six. Both must expose every day.
        foreach (var date in new[] { new DateTime(2027,2,1), new DateTime(2026,3,31) })
        foreach (var detailed in new[] { false,true })
        {
            DateTime selected = default;
            var card = new CalendarCard(Variant.Month,date,date,[],d => selected=d,_=>{},_=>{},()=>{},
                dark:true,detailedLunar:detailed).Root;
            var size = CalendarCard.Sizes[(int)Variant.Month];
            card.Measure(size);
            card.Arrange(new Rect(size));
            card.UpdateLayout();
            var buttons = Tree(card).OfType<Button>().ToList();
            int days = DateTime.DaysInMonth(date.Year,date.Month);
            Check(buttons.Count == (detailed ? 42 : days)+3, "月份行数与农历模式");
            var last = new DateTime(date.Year,date.Month,days);
            var day = buttons.Single(b=>AutomationProperties.GetName(b).StartsWith(last.ToString("M月d日")+" "));
            var origin = day.TranslatePoint(new Point(),card);
            Check(origin.Y>=0 && origin.Y+day.ActualHeight<=size.Height, "末日完整可见");
            day.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(selected==last, "可选中当月最后一天");
        }
    }
    private static void RenderChecks(List<Entry> entries, string? preview, bool dark = false)
    {
        var panels = new List<(Variant V, Border Card)>();
        foreach (var variant in Enum.GetValues<Variant>())
        {
            DateTime selected = default; int moved = 0;
            var card = new CalendarCard(variant, Day, Now, entries, d => selected = d, _ => { }, n => moved = n, () => { }, dark).Root;
            var size = CalendarCard.Sizes[(int)variant];
            card.Measure(size); card.Arrange(new Rect(size)); card.UpdateLayout();
            if (variant == Variant.Month)
            {
                var buttons = Tree(card).OfType<Button>().ToList();
                Check(buttons.Count == 34, "本月 31 天和 3 个导航按钮");
                buttons.Single(b => AutomationProperties.GetName(b).StartsWith("7月17日 ")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                buttons.Single(b => AutomationProperties.GetName(b) == "下个月").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(selected == Day && moved == 1, "月份导航及选中日期");
            }
            panels.Add((variant, card));
        }
        if (preview == null) return;
        var board = new Canvas { Width = 1325, Height = 853, Background = Brushes.Transparent };
        var positions = new Dictionary<Variant, (int X, int Y)>
        {
            [Variant.Compact] = (43,24), [Variant.Date] = (255,24), [Variant.Week] = (467,24),
            [Variant.Summary] = (43,233), [Variant.Month] = (43,442), [Variant.Agenda] = (467,441),
            [Variant.ShortTimeline] = (891,441)
        };
        foreach (var (v, card) in panels)
        {
            var (x, y) = positions[v];
            card.Width = CalendarCard.Sizes[(int)v].Width;
            card.Height = CalendarCard.Sizes[(int)v].Height;
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = dark ? 0.34 : 0.14,
                Color = Colors.Black
            };
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            board.Children.Add(card);
        }
        board.Measure(new(board.Width,board.Height));
        board.Arrange(new Rect(0,0,board.Width,board.Height));
        board.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)(board.Width*1.5),(int)(board.Height*1.5),144,144,PixelFormats.Pbgra32);
        bmp.Render(board);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var output = File.Create(preview);
        encoder.Save(output);
    }
    private sealed class Server(Queue<Func<HttpRequestMessage, HttpResponseMessage>> replies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(replies.Dequeue()(req));
    }
}
