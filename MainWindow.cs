using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WinMacCalendar;

public sealed class MainWindow : Window
{
    private readonly CalendarStore store;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private CancellationTokenSource? activeSync;
    private readonly CancellationTokenSource closing = new();
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;

    private readonly List<WidgetInstance> widgets = [];
    private readonly Dictionary<string, WidgetWindow> extraWindows = new();
    private SettingsWindow? settingsWindow;
    private Window? trayMenuHost;

    private Account? account;
    private ThemePreference themePreference;
    private TravelDisplayMode travelMode;
    private bool monthDots;
    private bool uniformScale = true;
    private bool snapToGrid = true;
    private double globalScale = 1.0;
    private double cardOpacity = 1.0;

    private DateTime month = DateTime.Today, lastPaint, lastAttempt, nextMetadataRefresh;
    private bool followToday = true, cacheDirty = true, modalOpen, isClosing;
    private bool? dark;
    private DateTime cacheFrom, cacheTo;
    private List<Entry> entries = [];
    private string syncText = "等待同步";
    private string syncDetail = "";
    private int recoveryScheduled;
    private readonly Dictionary<string, int> readErrors = new();
    private readonly HashSet<string> hidden;

    private WidgetInstance Primary => widgets[0];
    private IEnumerable<CalendarSource> Sources => new[] { CalendarSource.Local }.Concat(
        account?.Calendars.Select(c => CalendarSource.From(account, c)) ?? Enumerable.Empty<CalendarSource>());
    private IEnumerable<CalendarSource> Visible => Sources.Where(s => !hidden.Contains(s.Scope));
    private bool Writable => Visible.Any(s => s.Writable);
    private bool IsDark => Palette.ResolveDark(themePreference);

    public MainWindow(CalendarStore store)
    {
        this.store = store;
        Title = "桌面日历";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        if (!Enum.TryParse(store.Get("theme"), out themePreference) || !Enum.IsDefined(themePreference))
            themePreference = ThemePreference.System;
        if (!Enum.TryParse(store.Get("travelMode"), out travelMode) || !Enum.IsDefined(travelMode))
            travelMode = TravelDisplayMode.TodayOnly;
        monthDots = store.Get("monthDots") == "1";
        uniformScale = store.Get("uniformScale") != "0";
        snapToGrid = store.Get("snapToGrid") != "0";
        if (double.TryParse(store.Get("globalScale"), CultureInfo.InvariantCulture, out var gs) && double.IsFinite(gs))
            globalScale = Math.Clamp(gs, 0.60, 1.30);
        if (double.TryParse(store.Get("opacity"), CultureInfo.InvariantCulture, out var op) && double.IsFinite(op))
            cardOpacity = Math.Clamp(op, 0.75, 1.0);

        LoadWidgets();

        var saved = store.Get("account");
        account = Account.Load(saved);
        string filter = store.Get("hiddenCalendars");
        hidden = filter == "" ? new() : JsonSerializer.Deserialize<HashSet<string>>(filter) ?? new();
        if (filter == "" && account != null)
        {
            if (store.Get("source") == "cloud") hidden.Add(CalendarStore.Local);
            else foreach (var calendar in account.Calendars) hidden.Add(account.ScopeFor(calendar));
        }

        using var iconStream = Application.GetResourceStream(new Uri("/WinMacCalendar;component/Resources/Calendar.ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(iconStream, 32, 32);
        trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = "桌面日历", Icon = trayIcon, Visible = true };
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
                Dispatcher.Invoke(ShowTrayMenu);
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);

        RenderAll();
        ApplyGeometry(Primary, this, saveAfter: false);

        SourceInitialized += (_, _) => Desktop.Attach(this);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Dispatcher.BeginInvoke(() => { WindowState = WindowState.Normal; Desktop.Lower(this); });
        };
        MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(Primary, this); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!Primary.Locked && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
                SyncWindowPositionToConfig(Primary, this, fromDrag: true);
                Desktop.Lower(this);
            }
        };

        timer.Tick += async (_, _) =>
        {
            if (isClosing || modalOpen || ContextMenu?.IsOpen == true || extraWindows.Values.Any(w => w.ContextMenu?.IsOpen == true))
                return;
            LowerAll();
            var now = DateTime.Now;
            bool dayOrThemeChanged = now.Date != lastPaint.Date || dark != IsDark;
            if (now.Date != lastPaint.Date && followToday) month = now.Date;

            if (dayOrThemeChanged)
            {
                RenderAll();
            }
            else if (now.Hour != lastPaint.Hour || now.Minute != lastPaint.Minute)
            {
                // Smart selective repaint: only repaint widgets whose visual output actually changes on minute tick.
                bool eventEndedThisMinute = entries.Any(e => !e.AllDay && e.End > lastPaint && e.End <= now);
                bool hourChanged = now.Hour != lastPaint.Hour;
                bool repainted = false;
                foreach (var cfg in widgets)
                {
                    bool isTimeline = cfg.Variant is Variant.ShortTimeline or Variant.Week;
                    bool isAgendaOrSummary = cfg.Variant is Variant.Compact or Variant.Summary or Variant.Agenda;
                    if (isTimeline || (isAgendaOrSummary && (eventEndedThisMinute || hourChanged)))
                    {
                        RenderWidget(cfg, WindowFor(cfg), now);
                        repainted = true;
                    }
                }
                if (repainted) lastPaint = now;
            }
            if (now - lastAttempt >= TimeSpan.FromSeconds(60)) await Sync();
        };

        Loaded += async (_, _) =>
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            EnsureExtraWindowsShown();
            timer.Start();
            await Sync();
            Desktop.TrimMemory();
        };

        Closed += (_, _) =>
        {
            isClosing = true;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            timer.Stop();
            closing.Cancel();
            CloseTrayMenuHost();
            if (settingsWindow != null)
            {
                var sw = settingsWindow;
                settingsWindow = null;
                try { sw.Close(); } catch { }
            }
            foreach (var extra in extraWindows.Values.ToList())
            {
                try { extra.Close(); } catch { }
            }
            extraWindows.Clear();
            tray.Visible = false;
            tray.Dispose();
            trayIcon.Dispose();
            store.Dispose();
        };
    }

    private void LoadWidgets()
    {
        widgets.Clear();
        string raw = store.Get("widgets");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<WidgetInstance>>(raw);
                if (parsed is { Count: > 0 })
                {
                    foreach (var w in parsed)
                    {
                        if (!Enum.IsDefined(w.Variant)) w.Variant = Variant.Month;
                        w.Scale = double.IsFinite(w.Scale) ? Math.Clamp(w.Scale, 0.60, 1.30) : 1.0;
                        if (string.IsNullOrWhiteSpace(w.Id)) w.Id = Guid.NewGuid().ToString("N")[..8];
                        widgets.Add(w);
                    }
                    return;
                }
            }
            catch { }
        }

        var savedView = store.Get("variant");
        Variant v;
        if (savedView == "Timeline") v = Variant.ShortTimeline;
        else if (!Enum.TryParse(savedView, out v) || !Enum.IsDefined(v)) v = Variant.Month;

        double left = double.NaN, top = double.NaN;
        if (double.TryParse(store.Get("left"), CultureInfo.InvariantCulture, out var x)
            && double.TryParse(store.Get("top"), CultureInfo.InvariantCulture, out var y)
            && double.IsFinite(x) && double.IsFinite(y))
        {
            left = x;
            top = y;
        }

        widgets.Add(new WidgetInstance
        {
            Id = "primary",
            Variant = v,
            Left = left,
            Top = top,
            Scale = globalScale,
            DetailedLunar = store.Get("detailedLunar") == "1",
            Locked = store.Get("locked") == "1"
        });
    }

    private void SaveWidgets()
    {
        store.Set("widgets", JsonSerializer.Serialize(widgets));
        var p = Primary;
        store.Set("variant", p.Variant.ToString());
        store.Set("detailedLunar", p.DetailedLunar ? "1" : "0");
        store.Set("locked", p.Locked ? "1" : "0");
        if (double.IsFinite(p.Left) && double.IsFinite(p.Top))
        {
            store.Set("left", p.Left.ToString("R", CultureInfo.InvariantCulture));
            store.Set("top", p.Top.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private double EffectiveScale(WidgetInstance w) => Math.Clamp(uniformScale ? globalScale : w.Scale, 0.60, 1.30);

    // Apple-style widget grid anchored at widget #1's top-left corner:
    // step = (183 + 29) * scale = 212 * scale so 2x(1x1) == 2x1 width and 2x(1x1) == 2x2 height to the pixel!
    internal double GridStepPixels() => Math.Round(WidgetInstance.GridStep * EffectiveScale(Primary));
    internal double GridGapPixels() => Math.Round(WidgetInstance.GridGap * EffectiveScale(Primary));

    internal (int Col, int Row) GetGridCell(WidgetInstance cfg)
    {
        if (cfg == Primary || !double.IsFinite(Primary.Left) || !double.IsFinite(Primary.Top)
            || !double.IsFinite(cfg.Left) || !double.IsFinite(cfg.Top))
            return (0, 0);
        double step = Math.Max(1, GridStepPixels());
        int col = (int)Math.Round((cfg.Left - Primary.Left) / step);
        int row = (int)Math.Round((cfg.Top - Primary.Top) / step);
        return (col, row);
    }

    private (double Left, double Top) GridCellToPixels(int col, int row)
    {
        double step = GridStepPixels();
        double ox = double.IsFinite(Primary.Left) ? Primary.Left : SystemParameters.WorkArea.Right - Primary.EffectiveSize(uniformScale, globalScale).Width - 24;
        double oy = double.IsFinite(Primary.Top) ? Primary.Top : SystemParameters.WorkArea.Top + 28;
        return (Math.Round(ox + col * step), Math.Round(oy + row * step));
    }

    private HashSet<(int Col, int Row)> OccupiedGridCells(string? excludeId = null)
    {
        var used = new HashSet<(int, int)>();
        foreach (var w in widgets)
        {
            if (w.Id == excludeId) continue;
            var (c0, r0) = GetGridCell(w);
            var (cols, rows) = w.GridSpan;
            for (int c = c0; c < c0 + cols; c++)
                for (int r = r0; r < r0 + rows; r++)
                    used.Add((c, r));
        }
        return used;
    }

    private bool FitsAtGridCell(int col, int row, WidgetInstance cfg, HashSet<(int Col, int Row)> occupied)
    {
        var (cols, rows) = cfg.GridSpan;
        for (int c = col; c < col + cols; c++)
            for (int r = row; r < row + rows; r++)
                if (occupied.Contains((c, r))) return false;

        var (px, py) = GridCellToPixels(col, row);
        var size = cfg.EffectiveSize(uniformScale, globalScale);
        var work = SystemParameters.WorkArea;
        return px >= work.Left - 4 && py >= work.Top - 4
            && px + size.Width <= work.Right + 4 && py + size.Height <= work.Bottom + 4;
    }

    private (int Col, int Row) FindNearestFreeGridCell(WidgetInstance cfg, int targetCol, int targetRow)
    {
        var occupied = OccupiedGridCells(cfg.Id);
        if (FitsAtGridCell(targetCol, targetRow, cfg, occupied)) return (targetCol, targetRow);

        (int Col, int Row)? best = null;
        double bestDist = double.MaxValue;
        for (int dr = -6; dr <= 6; dr++)
        {
            for (int dc = -8; dc <= 8; dc++)
            {
                int c = targetCol + dc;
                int r = targetRow + dr;
                if (!FitsAtGridCell(c, r, cfg, occupied)) continue;
                double dist = dc * dc + dr * dr;
                if (dist < bestDist) { bestDist = dist; best = (c, r); }
            }
        }
        return best ?? (targetCol, targetRow);
    }

    internal void SnapWidgetToGrid(string id, bool avoidOverlap = true)
    {
        var cfg = widgets.FirstOrDefault(w => w.Id == id);
        if (cfg == null || cfg == Primary) return;
        var (col, row) = GetGridCell(cfg);
        if (avoidOverlap) (col, row) = FindNearestFreeGridCell(cfg, col, row);
        var (nx, ny) = GridCellToPixels(col, row);
        cfg.Left = nx;
        cfg.Top = ny;
        ApplyGeometry(cfg, WindowFor(cfg), saveAfter: true);
        LowerAll();
        settingsWindow?.NotifyWidgetsChanged();
    }

    internal void NudgeWidgetOnGrid(string id, int dCol, int dRow)
    {
        var cfg = widgets.FirstOrDefault(w => w.Id == id);
        if (cfg == null || cfg == Primary) return;
        var (col, row) = GetGridCell(cfg);
        var (nx, ny) = GridCellToPixels(col + dCol, row + dRow);
        cfg.Left = nx;
        cfg.Top = ny;
        ApplyGeometry(cfg, WindowFor(cfg), saveAfter: true);
        LowerAll();
        settingsWindow?.NotifyWidgetsChanged();
    }

    internal void SnapAllToGrid()
    {
        if (widgets.Count <= 1) return;
        var placed = new HashSet<(int, int)>();
        var (pCols, pRows) = Primary.GridSpan;
        for (int c = 0; c < pCols; c++)
            for (int r = 0; r < pRows; r++)
                placed.Add((c, r));

        foreach (var cfg in widgets.Skip(1))
        {
            var (tCol, tRow) = GetGridCell(cfg);
            (int Col, int Row)? best = null;
            double bestDist = double.MaxValue;
            for (int dr = -6; dr <= 6; dr++)
                for (int dc = -8; dc <= 8; dc++)
                {
                    int c = tCol + dc, r = tRow + dr;
                    if (!FitsAtGridCell(c, r, cfg, placed)) continue;
                    double d = dc * dc + dr * dr;
                    if (d < bestDist) { bestDist = d; best = (c, r); }
                }
            var (fc, fr) = best ?? (tCol, tRow);
            var (cols, rows) = cfg.GridSpan;
            for (int c = fc; c < fc + cols; c++)
                for (int r = fr; r < fr + rows; r++)
                    placed.Add((c, r));
            var (nx, ny) = GridCellToPixels(fc, fr);
            cfg.Left = nx;
            cfg.Top = ny;
            ApplyGeometry(cfg, WindowFor(cfg), saveAfter: false);
        }
        SaveWidgets();
        LowerAll();
        settingsWindow?.NotifyWidgetsChanged();
    }

    private Window WindowFor(WidgetInstance w) =>
        w == Primary ? this : extraWindows.TryGetValue(w.Id, out var win) ? win : this;

    private void LowerAll()
    {
        Desktop.Lower(this);
        foreach (var w in extraWindows.Values) Desktop.Lower(w);
    }

    private void EnsureExtraWindowsShown()
    {
        var activeIds = widgets.Skip(1).Select(w => w.Id).ToHashSet();
        foreach (var staleId in extraWindows.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            if (extraWindows.Remove(staleId, out var oldWin))
                try { oldWin.Close(); } catch { }
        }

        for (int i = 1; i < widgets.Count; i++)
        {
            var cfg = widgets[i];
            if (!extraWindows.TryGetValue(cfg.Id, out var win))
            {
                win = new WidgetWindow(
                    cfg.Id,
                    () => widgets.FirstOrDefault(x => x.Id == cfg.Id)?.Locked == true,
                    movedWin =>
                    {
                        var item = widgets.FirstOrDefault(x => x.Id == movedWin.WidgetId);
                        if (item != null) SyncWindowPositionToConfig(item, movedWin, fromDrag: true);
                    },
                    menuWin =>
                    {
                        var item = widgets.FirstOrDefault(x => x.Id == menuWin.WidgetId);
                        if (item != null) ShowMenu(item, menuWin);
                    });
                extraWindows[cfg.Id] = win;
                RenderWidget(cfg, win, DateTime.Now);
                bool needsInitialSave = !double.IsFinite(cfg.Left) || !double.IsFinite(cfg.Top);
                ApplyGeometry(cfg, win, saveAfter: needsInitialSave);
                if (IsVisible) win.Show();
                Desktop.Lower(win);
            }
        }
    }

    private void ApplyGeometry(WidgetInstance cfg, Window win, bool saveAfter)
    {
        var size = cfg.EffectiveSize(uniformScale, globalScale);
        win.Width = size.Width;
        win.Height = size.Height;

        if (!double.IsFinite(cfg.Left) || !double.IsFinite(cfg.Top))
        {
            var work = SystemParameters.WorkArea;
            if (cfg == Primary)
            {
                cfg.Left = Math.Round(work.Right - size.Width - 24);
                cfg.Top = Math.Round(work.Top + 28);
            }
            else
            {
                var (pCols, pRows) = Primary.GridSpan;
                // Prefer placing right below #1, then to the left/right of #1 on the #1-anchored Apple grid.
                var (col, row) = FindNearestFreeGridCell(cfg, 0, pRows);
                var (nx, ny) = GridCellToPixels(col, row);
                cfg.Left = nx;
                cfg.Top = ny;
            }
        }

        win.Left = cfg.Left;
        win.Top = cfg.Top;
        KeepOnScreen(cfg, win);
        if (saveAfter) SaveWidgets();
    }

    private static void KeepOnScreen(WidgetInstance cfg, Window win)
    {
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double maxX = Math.Max(vx, vx + SystemParameters.VirtualScreenWidth - win.Width);
        double maxY = Math.Max(vy, vy + SystemParameters.VirtualScreenHeight - win.Height);
        win.Left = Math.Round(Math.Clamp(win.Left, vx, maxX));
        win.Top = Math.Round(Math.Clamp(win.Top, vy, maxY));
        cfg.Left = win.Left;
        cfg.Top = win.Top;
    }

    private void SyncWindowPositionToConfig(WidgetInstance cfg, Window win, bool fromDrag)
    {
        KeepOnScreen(cfg, win);
        if (fromDrag && snapToGrid && cfg != Primary)
        {
            var (col, row) = GetGridCell(cfg);
            (col, row) = FindNearestFreeGridCell(cfg, col, row);
            var (nx, ny) = GridCellToPixels(col, row);
            win.Left = nx;
            win.Top = ny;
            KeepOnScreen(cfg, win);
        }
        SaveWidgets();
        settingsWindow?.NotifyWidgetsChanged();
    }

    private void ResetAllPositions()
    {
        var work = SystemParameters.WorkArea;
        var pSize = Primary.EffectiveSize(uniformScale, globalScale);
        Primary.Left = Math.Round(work.Right - pSize.Width - 24);
        Primary.Top = Math.Round(work.Top + 28);
        ApplyGeometry(Primary, this, saveAfter: false);

        var placed = new HashSet<(int, int)>();
        var (pCols, pRows) = Primary.GridSpan;
        for (int c = 0; c < pCols; c++)
            for (int r = 0; r < pRows; r++)
                placed.Add((c, r));

        foreach (var cfg in widgets.Skip(1))
        {
            (int Col, int Row)? chosen = null;
            double bestScore = double.MaxValue;
            for (int c = 0; c >= -6; c--)
                for (int r = 0; r <= 6; r++)
                {
                    if (!FitsAtGridCell(c, r, cfg, placed)) continue;
                    double score = Math.Abs(c) * 10 + r;
                    if (score < bestScore) { bestScore = score; chosen = (c, r); }
                }
            var (fc, fr) = chosen ?? (0, pRows);
            var (cols, rows) = cfg.GridSpan;
            for (int c = fc; c < fc + cols; c++)
                for (int r = fr; r < fr + rows; r++)
                    placed.Add((c, r));
            var (nx, ny) = GridCellToPixels(fc, fr);
            cfg.Left = nx;
            cfg.Top = ny;
            ApplyGeometry(cfg, WindowFor(cfg), saveAfter: false);
        }
        SaveWidgets();
        LowerAll();
        settingsWindow?.NotifyWidgetsChanged();
    }

    private void LoadRange(DateTime from, DateTime to)
    {
        if (!cacheDirty && from >= cacheFrom && to <= cacheTo) return;
        var result = new List<Entry>();
        readErrors.Clear();
        foreach (var source in Visible)
            foreach (var item in store.List(source.Scope).Where(r => r.Pending != "delete"))
            {
                try { result.AddRange(Events.Expand(item, from, to).Select(e => e with { Source = source })); }
                catch (Exception ex)
                {
                    string reason = source.Name + " · " + ex.GetBaseException().Message;
                    readErrors[reason] = readErrors.GetValueOrDefault(reason) + 1;
                }
            }
        entries = result.OrderBy(e => e.Start).ToList();
        cacheFrom = from;
        cacheTo = to;
        cacheDirty = false;
    }

    private void RenderAll()
    {
        var now = DateTime.Now;
        if (followToday) month = now.Date;
        bool hasMonth = widgets.Any(w => w.Variant == Variant.Month);
        var from = hasMonth ? (Dates.GridStart(month) < now.Date ? Dates.GridStart(month) : now.Date) : now.Date;
        var to = hasMonth
            ? (Dates.GridStart(month).AddDays(42) > now.Date.AddDays(32) ? Dates.GridStart(month).AddDays(42) : now.Date.AddDays(32))
            : now.Date.AddDays(32);
        LoadRange(from, to);
        dark = IsDark;

        RenderWidget(Primary, this, now);
        foreach (var cfg in widgets.Skip(1))
        {
            if (extraWindows.TryGetValue(cfg.Id, out var win))
                RenderWidget(cfg, win, now);
        }
        lastPaint = now;
    }

    private void RenderWidget(WidgetInstance cfg, Window target, DateTime now)
    {
        var card = new CalendarCard(
            cfg.Variant,
            month,
            now,
            entries,
            OpenDay,
            e => Edit(e.Start.Date, e),
            MoveMonth,
            () => ShowMenu(cfg, target),
            dark,
            cfg.DetailedLunar,
            travelMode,
            cardOpacity,
            monthDots).Root;

        double scale = EffectiveScale(cfg);
        var baseSize = cfg.BaseSize;
        if (target is WidgetWindow ww)
        {
            ww.ApplyContent(card, baseSize, scale);
        }
        else
        {
            card.Width = baseSize.Width;
            card.Height = baseSize.Height;
            card.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
            target.Width = Math.Round(baseSize.Width * scale);
            target.Height = Math.Round(baseSize.Height * scale);
            target.Content = card;
        }
    }

    internal WidgetInstance AddWidget(Variant variant)
    {
        var cfg = new WidgetInstance
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Variant = variant,
            Scale = globalScale
        };
        widgets.Add(cfg);
        EnsureExtraWindowsShown();
        SaveWidgets();
        settingsWindow?.NotifyWidgetsChanged();
        return cfg;
    }

    internal void RemoveWidget(string id)
    {
        if (widgets.Count <= 1) return;
        int idx = widgets.FindIndex(w => w.Id == id);
        if (idx < 0) return;

        if (idx == 0)
        {
            var next = widgets[1];
            if (extraWindows.Remove(next.Id, out var promotedWin))
                try { promotedWin.Close(); } catch { }
            widgets.RemoveAt(0);
            RenderAll();
            ApplyGeometry(Primary, this, saveAfter: true);
        }
        else
        {
            widgets.RemoveAt(idx);
            if (extraWindows.Remove(id, out var win))
                try { win.Close(); } catch { }
            SaveWidgets();
        }
        settingsWindow?.NotifyWidgetsChanged();
    }

    internal void UpdateWidget(string id, Action<WidgetInstance> mutate, bool geometryChanged = false)
    {
        var cfg = widgets.FirstOrDefault(w => w.Id == id);
        if (cfg == null) return;
        mutate(cfg);
        cfg.Scale = Math.Clamp(cfg.Scale, 0.60, 1.30);
        var win = WindowFor(cfg);
        RenderAll();
        if (geometryChanged) ApplyGeometry(cfg, win, saveAfter: false);
        else KeepOnScreen(cfg, win);
        SaveWidgets();
        LowerAll();
        settingsWindow?.NotifyWidgetsChanged();
    }

    private void RescaleWithGridPreservation(Action applyScaleChange)
    {
        // Capture current grid cells relative to #1 BEFORE scale changes so all widgets stay tightly aligned on the grid!
        var savedCells = snapToGrid
            ? widgets.Skip(1).ToDictionary(w => w.Id, GetGridCell)
            : null;

        applyScaleChange();
        RenderAll();
        ApplyGeometry(Primary, this, saveAfter: false);

        foreach (var cfg in widgets.Skip(1))
        {
            if (savedCells != null && savedCells.TryGetValue(cfg.Id, out var cell))
            {
                var (nx, ny) = GridCellToPixels(cell.Col, cell.Row);
                cfg.Left = nx;
                cfg.Top = ny;
            }
            ApplyGeometry(cfg, WindowFor(cfg), saveAfter: false);
        }

        SaveWidgets();
        LowerAll();
        settingsWindow?.NotifyWidgetsChanged();
    }

    private void MoveMonth(int delta)
    {
        var target = month.AddMonths(delta);
        if (target.Year is < 1901 or > 2100) return;
        month = target;
        followToday = month.Year == DateTime.Today.Year && month.Month == DateTime.Today.Month;
        RenderAll();
    }

    private void Today()
    {
        month = DateTime.Today;
        followToday = true;
        RenderAll();
        LowerAll();
    }

    private static MenuItem Item(string title, Action action, bool? active = null)
    {
        var m = new MenuItem { Header = title, IsCheckable = active.HasValue, IsChecked = active ?? false };
        m.Click += (_, _) => action();
        return m;
    }

    internal static MenuItem StatusMenuItem(string summary, string detail = "")
    {
        var text = new TextBlock
        {
            Text = summary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
            VerticalAlignment = VerticalAlignment.Center
        };
        var item = new MenuItem { Header = text, IsEnabled = false };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            ToolTipService.SetShowOnDisabled(item, true);
            item.ToolTip = detail;
        }
        return item;
    }

    private void CloseTrayMenuHost()
    {
        if (trayMenuHost == null) return;
        var hostWin = trayMenuHost;
        trayMenuHost = null;
        try { hostWin.Close(); } catch { }
    }

    // Modern WPF rounded dark/light tray context menu (replaces ugly legacy WinForms ContextMenuStrip)
    private void ShowTrayMenu()
    {
        if (isClosing) return;
        CloseTrayMenuHost();

        var hostWin = new Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true
        };
        trayMenuHost = hostWin;
        hostWin.Show();
        Desktop.BringToForeground(hostWin);

        var menu = new ContextMenu();
        ThemeResources.Apply(menu, IsDark);

        menu.Items.Add(Item("设置与布局…", OpenSettings));
        var addMenu = new MenuItem { Header = "添加新小组件" };
        foreach (var v in Enum.GetValues<Variant>())
            addMenu.Items.Add(Item(CalendarCard.Names[(int)v], () => AddWidget(v)));
        menu.Items.Add(addMenu);
        if (Writable) menu.Items.Add(Item("新建日程…", () => Edit(DateTime.Today, null)));
        menu.Items.Add(Item("回到今天", Today));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("对齐到网格（以 #1 为基准）", () =>
        {
            snapToGrid = !snapToGrid;
            store.Set("snapToGrid", snapToGrid ? "1" : "0");
            if (snapToGrid) SnapAllToGrid();
            settingsWindow?.NotifyWidgetsChanged();
        }, snapToGrid));
        menu.Items.Add(Item("全部移回主屏幕", ResetAllPositions));
        if (account != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("立即同步", async () => await Sync()));
            menu.Items.Add(StatusMenuItem(syncText, syncDetail));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出桌面日历", Close));

        menu.Closed += (_, _) => Dispatcher.BeginInvoke(CloseTrayMenuHost);
        hostWin.Deactivated += (_, _) => { if (menu.IsOpen) menu.IsOpen = false; };
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void ShowMenu(WidgetInstance cfg, Window target)
    {
        if (modalOpen || isClosing) return;
        bool night = IsDark;
        var menu = new ContextMenu();
        ThemeResources.Apply(menu, night);

        var views = new MenuItem { Header = "切换此组件样式" };
        foreach (var v in Enum.GetValues<Variant>())
            views.Items.Add(Item(CalendarCard.Names[(int)v], () =>
            {
                UpdateWidget(cfg.Id, w => w.Variant = v, geometryChanged: true);
            }, v == cfg.Variant));
        menu.Items.Add(views);

        var addMenu = new MenuItem { Header = "添加新小组件" };
        foreach (var v in Enum.GetValues<Variant>())
            addMenu.Items.Add(Item(CalendarCard.Names[(int)v], () => AddWidget(v)));
        menu.Items.Add(addMenu);

        if (Writable) menu.Items.Add(Item("新建日程…", () => Edit(DateTime.Today, null)));
        menu.Items.Add(Item("回到今天", Today));
        menu.Items.Add(Item("锁定此组件位置", () =>
        {
            UpdateWidget(cfg.Id, w => w.Locked = !w.Locked);
        }, cfg.Locked));

        menu.Items.Add(new Separator());

        var sources = new MenuItem { Header = "显示日历" };
        foreach (var source in Sources)
        {
            var entry = Item(source.Name + (source.Writable ? "" : "（只读）"), () => ToggleSource(source), !hidden.Contains(source.Scope));
            entry.Header = Dialogs.SourceLabel(source.Name + (source.Writable ? "" : "（只读）"), source.Accent, night, maxTextWidth: 200);
            sources.Items.Add(entry);
        }
        menu.Items.Add(sources);

        if (account != null)
        {
            menu.Items.Add(Item("立即同步", async () => await Sync()));
            menu.Items.Add(StatusMenuItem(syncText, syncDetail));
        }

        menu.Items.Add(Item("设置与布局…", OpenSettings));

        if (widgets.Count > 1)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("移除此小组件", () => RemoveWidget(cfg.Id)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出桌面日历", Close));

        target.ContextMenu = menu;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void OpenSettings()
    {
        if (isClosing) return;
        if (settingsWindow != null)
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            Desktop.BringToForeground(settingsWindow);
            return;
        }

        var host = new SettingsHost
        {
            GetWidgets = () => widgets.ToList(),
            AddWidget = AddWidget,
            RemoveWidget = RemoveWidget,
            UpdateWidget = UpdateWidget,
            GetUniformScale = () => uniformScale,
            SetUniformScale = on =>
            {
                RescaleWithGridPreservation(() =>
                {
                    uniformScale = on;
                    store.Set("uniformScale", on ? "1" : "0");
                    if (on) foreach (var w in widgets) w.Scale = globalScale;
                });
            },
            GetGlobalScale = () => globalScale,
            SetGlobalScale = s =>
            {
                RescaleWithGridPreservation(() =>
                {
                    globalScale = Math.Clamp(s, 0.60, 1.30);
                    store.Set("globalScale", globalScale.ToString("R", CultureInfo.InvariantCulture));
                    if (uniformScale) foreach (var w in widgets) w.Scale = globalScale;
                });
            },
            GetSnapToGrid = () => snapToGrid,
            SetSnapToGrid = on =>
            {
                snapToGrid = on;
                store.Set("snapToGrid", on ? "1" : "0");
                if (on) SnapAllToGrid();
                else settingsWindow?.NotifyWidgetsChanged();
            },
            GetGridCell = GetGridCell,
            GetGridStepPixels = GridStepPixels,
            GetGridGapPixels = GridGapPixels,
            SnapWidgetToGrid = id => SnapWidgetToGrid(id, avoidOverlap: true),
            SnapAllToGrid = SnapAllToGrid,
            NudgeWidgetOnGrid = NudgeWidgetOnGrid,
            GetTheme = () => themePreference,
            SetTheme = t =>
            {
                themePreference = t;
                store.Set("theme", t.ToString());
                RenderAll();
            },
            GetTravelMode = () => travelMode,
            SetTravelMode = m =>
            {
                travelMode = m;
                store.Set("travelMode", m.ToString());
                RenderAll();
            },
            GetMonthDots = () => monthDots,
            SetMonthDots = on =>
            {
                monthDots = on;
                store.Set("monthDots", on ? "1" : "0");
                RenderAll();
            },
            GetOpacity = () => cardOpacity,
            SetOpacity = val =>
            {
                cardOpacity = Math.Clamp(val, 0.75, 1.0);
                store.Set("opacity", cardOpacity.ToString("R", CultureInfo.InvariantCulture));
                RenderAll();
            },
            GetAccount = () => account,
            GetSyncText = () => syncText,
            GetSyncDetail = () => syncDetail,
            GetReadErrors = () => readErrors,
            GetSources = () => Sources.Select(s => (s, !hidden.Contains(s.Scope))),
            SetSourceVisible = (s, visible) =>
            {
                if (visible) hidden.Remove(s.Scope);
                else hidden.Add(s.Scope);
                SaveFilter();
            },
            ConnectAccount = ownerWin => ConnectFrom(ownerWin),
            DisconnectAccount = Disconnect,
            SyncNow = Sync,
            ResetAllPositions = ResetAllPositions,
            GoToday = Today,
            GetDataFolder = () => store.FolderPath,
            ChangeDataFolder = (ownerWin, targetFolder) => ChangeDataFolderAsync(ownerWin, targetFolder)
        };

        settingsWindow = new SettingsWindow(null, host);
        settingsWindow.Closed += (_, _) =>
        {
            settingsWindow = null;
            LowerAll();
            Desktop.TrimMemory();
        };
        settingsWindow.Show();
        settingsWindow.Activate();
        Desktop.BringToForeground(settingsWindow);
    }

    private async Task<bool> ChangeDataFolderAsync(Window ownerWindow, string targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder)) return false;
        string normalizedTarget;
        try { normalizedTarget = StorageLocation.Normalize(targetFolder); }
        catch (Exception ex)
        {
            Dialogs.Alert(ownerWindow, "无效目录", ex.Message, IsDark);
            return false;
        }
        string current = StorageLocation.Normalize(store.FolderPath);
        if (string.Equals(normalizedTarget, current, StringComparison.OrdinalIgnoreCase))
            return false;

        bool useExistingTarget = false;
        string candidateDb = Path.Combine(normalizedTarget, "calendar.db");
        if (File.Exists(candidateDb))
        {
            int choice = Dialogs.Choice(
                ownerWindow,
                "目标位置已有缓存",
                $"所选目录中已存在日历缓存文件：\n{candidateDb}\n\n请选择处理方式：",
                "使用目标已有数据",
                "用当前数据覆盖",
                IsDark);
            if (choice == 0) return false;
            useExistingTarget = choice == 1;
        }

        activeSync?.Cancel();
        await syncLock.WaitAsync(closing.Token);
        try
        {
            store.MigrateToFolder(normalizedTarget, useExistingTarget);
            StorageLocation.SetFolder(normalizedTarget);

            if (useExistingTarget)
            {
                // Reload preferences, widgets, account, and calendar filters from the adopted database.
                if (!Enum.TryParse(store.Get("theme"), out themePreference) || !Enum.IsDefined(themePreference))
                    themePreference = ThemePreference.System;
                if (!Enum.TryParse(store.Get("travelMode"), out travelMode) || !Enum.IsDefined(travelMode))
                    travelMode = TravelDisplayMode.TodayOnly;
                monthDots = store.Get("monthDots") == "1";
                uniformScale = store.Get("uniformScale") != "0";
                snapToGrid = store.Get("snapToGrid") != "0";
                if (double.TryParse(store.Get("globalScale"), CultureInfo.InvariantCulture, out var gs) && double.IsFinite(gs))
                    globalScale = Math.Clamp(gs, 0.60, 1.30);
                if (double.TryParse(store.Get("opacity"), CultureInfo.InvariantCulture, out var op) && double.IsFinite(op))
                    cardOpacity = Math.Clamp(op, 0.75, 1.0);
                account = Account.Load(store.Get("account"));
                string filter = store.Get("hiddenCalendars");
                hidden.Clear();
                if (filter != "")
                    foreach (var s in JsonSerializer.Deserialize<HashSet<string>>(filter) ?? []) hidden.Add(s);
                LoadWidgets();
                EnsureExtraWindowsShown();
                foreach (var w in widgets) ApplyGeometry(w, WindowFor(w), saveAfter: false);
            }

            cacheDirty = true;
            RenderAll();
            LowerAll();
        }
        catch (Exception ex)
        {
            Dialogs.Alert(ownerWindow, "更改缓存位置失败", ex.Message, IsDark);
            return false;
        }
        finally
        {
            syncLock.Release();
        }

        Dialogs.Alert(ownerWindow, "缓存位置已更改", $"本地缓存已成功切换到：\n{store.FolderPath}", IsDark);
        if (account != null) _ = Sync();
        return true;
    }

    private void SaveFilter()
    {
        store.Set("hiddenCalendars", JsonSerializer.Serialize(hidden));
        cacheDirty = true;
        RenderAll();
    }

    private void ToggleSource(CalendarSource source)
    {
        if (!hidden.Remove(source.Scope)) hidden.Add(source.Scope);
        SaveFilter();
    }

    private bool HasPendingOutside(Account? next)
    {
        if (account == null) return false;
        var kept = next?.Calendars.Select(next.ScopeFor).ToHashSet() ?? new HashSet<string>();
        return account.Calendars.Select(account.ScopeFor).Any(scope =>
            !kept.Contains(scope) && store.List(scope).Any(r => r.Pending != ""));
    }

    private bool ConnectFrom(Window ownerWindow)
    {
        var newAccount = Dialogs.Connect(ownerWindow, account, IsDark);
        if (newAccount == null) return false;
        if (HasPendingOutside(newAccount))
        {
            Dialogs.Alert(ownerWindow, "iCloud 日历", "当前日历有待上传的更改，请先同步后再切换账户或日历。", IsDark);
            return false;
        }
        activeSync?.Cancel();
        if (account == null && !store.List(CalendarStore.Local).Any()) hidden.Add(CalendarStore.Local);
        account = newAccount;
        store.Set("account", JsonSerializer.Serialize(account));
        foreach (var c in account.Calendars) hidden.Remove(account.ScopeFor(c));
        SaveFilter();
        syncText = "等待同步";
        syncDetail = "";
        lastAttempt = DateTime.MinValue;
        nextMetadataRefresh = DateTime.UtcNow.AddHours(1);
        _ = Sync();
        return true;
    }

    private void Disconnect()
    {
        if (account == null) return;
        if (HasPendingOutside(null))
        {
            Dialogs.Alert(settingsWindow ?? (Window)this, "iCloud 日历", "还有待上传的更改，请在同步完成后断开连接。", IsDark);
            return;
        }
        activeSync?.Cancel();
        account = null;
        syncText = "等待同步";
        syncDetail = "";
        store.Set("account", "");
        hidden.Remove(CalendarStore.Local);
        SaveFilter();
    }

    private void OpenDay(DateTime date)
    {
        if (modalOpen) return;
        modalOpen = true;
        Entry? selected = null;
        bool edit = false;
        try
        {
            Dialogs.Day(
                this,
                date,
                entries.Where(e => e.On(date)).OrderByDescending(e => e.AllDay).ThenBy(e => e.Start).ToList(),
                Writable,
                e => { edit = true; selected = e; },
                IsDark);
        }
        finally
        {
            modalOpen = false;
            LowerAll();
        }
        if (edit) Edit(date, selected);
    }

    private async void Edit(DateTime day, Entry? ev)
    {
        if (modalOpen) return;
        modalOpen = true;
        try
        {
            var destinations = Visible.Where(s => s.Writable).ToList();
            var change = Dialogs.Event(this, day, ev, destinations, IsDark);
            if (change == null) return;
            var source = ev?.Source ?? change.Destination!;
            if (!source.Writable) return;
            await syncLock.WaitAsync(closing.Token);
            try
            {
                var existing = ev?.File;
                if (existing != null && !store.List(source.Scope).Contains(existing))
                {
                    Dialogs.Alert(this, "日程已更新", "此日程已更新，请重新打开后再编辑。", IsDark);
                    return;
                }
                if (change.Delete)
                {
                    if (source.Remote != null && (existing!.Etag != "" || existing.Pending == "creating"))
                        store.Save(source.Scope, existing with { Pending = "delete" });
                    else
                        store.Remove(source.Scope, existing!.Href);
                }
                else
                {
                    var href = existing?.Href ?? (source.Remote?.Url ?? "") + Guid.NewGuid().ToString("N") + ".ics";
                    var ics = Events.Write(existing, change.Title, change.Start, change.End, change.AllDay, change.Location, change.Description);
                    string pending = source.Remote != null ? (existing?.Pending == "creating" ? "creating" : "put") : "";
                    store.Save(source.Scope, new(href, ics, existing?.Etag ?? "", pending));
                }
                cacheDirty = true;
                RenderAll();
            }
            finally { syncLock.Release(); }
        }
        catch (OperationCanceledException) when (isClosing) { }
        catch (Exception ex) { if (!isClosing) Dialogs.Alert(this, "保存失败", ex.Message, IsDark); }
        finally
        {
            modalOpen = false;
            LowerAll();
        }
        await Sync();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (isClosing || e.Reason is not (SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (isClosing) return;
            LowerAll();
            if (account != null && (syncText.StartsWith("同步失败") || DateTime.Now - lastAttempt >= TimeSpan.FromSeconds(10)))
                TriggerDelayedRecoverySync();
        });
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (isClosing || !e.IsAvailable) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (isClosing || account == null) return;
            if (syncText.StartsWith("同步失败") || syncText == "等待同步")
                TriggerDelayedRecoverySync();
        });
    }

    private void TriggerDelayedRecoverySync()
    {
        if (Interlocked.CompareExchange(ref recoveryScheduled, 1, 0) != 0) return;
        _ = RunRecoverySyncAsync();
    }

    private async Task RunRecoverySyncAsync()
    {
        try
        {
            await Task.Delay(1500, closing.Token);
            if (!isClosing && account != null) await Sync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref recoveryScheduled, 0);
        }
    }

    internal static string SummarizeSyncError(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException) return "连接超时";
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode == HttpStatusCode.Unauthorized) return "账户验证失败（401）";
            if (hre.StatusCode == HttpStatusCode.Forbidden) return "无访问权限（403）";
            if (hre.StatusCode is { } code) return $"服务器错误（{(int)code}）";
            if (hre.HttpRequestError is HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError)
                return "网络暂不可用";
        }
        if (ex.GetBaseException() is SocketException) return "网络暂不可用";

        string raw = NormalizeSingleLine(ex.Message);
        if (string.IsNullOrEmpty(raw)) return "未知错误";
        return raw.Length > 20 ? raw[..20] + "…" : raw;
    }

    internal static (string Summary, string Detail) FormatSyncFailure(IReadOnlyList<(string Target, Exception Error)> failures)
    {
        if (failures.Count == 0) return ("同步失败", "");

        var reasons = failures.Select(f => SummarizeSyncError(f.Error)).Distinct().ToList();
        string summary = reasons.Count == 1
            ? $"同步失败 · {reasons[0]}"
            : $"同步失败 · {reasons[0]} 等 {reasons.Count} 项";
        if (summary.Length > 28) summary = summary[..27] + "…";

        var lines = failures
            .Select(f => $"{f.Target}：{NormalizeSingleLine(f.Error.Message)}")
            .Distinct()
            .ToList();
        string detail = reasons.Count == 1 && failures.Count > 1
            ? $"{reasons[0]}（共 {failures.Count} 项失败）\n" + string.Join("\n", lines.Take(6))
            : string.Join("\n", lines.Take(6));
        if (lines.Count > 6) detail += $"\n…另有 {lines.Count - 6} 项";
        return (summary, detail);
    }

    private static string NormalizeSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private async Task Sync()
    {
        var active = account;
        if (active == null || isClosing || !await syncLock.WaitAsync(0)) return;
        lastAttempt = DateTime.Now;
        var revision = store.Revision;
        syncText = "正在同步…";
        using var request = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        activeSync = request;
        try
        {
            using var cloud = new ICloud(active.Email, active.Password());
            var failures = new List<(string Target, Exception Error)>();
            if (DateTime.UtcNow >= nextMetadataRefresh)
            {
                try
                {
                    var discovered = await cloud.Discover(request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    nextMetadataRefresh = DateTime.UtcNow.AddHours(1);
                    var refreshed = active.Calendars.Select(old => discovered.FirstOrDefault(c => c.Url == old.Url) ?? old).ToList();
                    if (account == active && !refreshed.SequenceEqual(active.Calendars))
                    {
                        active = active with { Calendars = refreshed };
                        account = active;
                        store.Set("account", JsonSerializer.Serialize(active));
                        cacheDirty = true;
                    }
                }
                catch (Exception ex) when (!request.IsCancellationRequested)
                {
                    failures.Add(("日历名称与颜色", ex));
                }
            }
            foreach (var calendar in active.Calendars)
            {
                request.Token.ThrowIfCancellationRequested();
                try { await cloud.Sync(store, active, calendar, request.Token); }
                catch (Exception ex) when (!request.IsCancellationRequested) { failures.Add((calendar.Name, ex)); }
            }
            if (active == account)
            {
                if (failures.Count == 0)
                {
                    syncText = "上次同步 " + DateTime.Now.ToString("HH:mm");
                    syncDetail = "";
                }
                else
                {
                    (syncText, syncDetail) = FormatSyncFailure(failures);
                }
            }
        }
        catch (Exception ex)
        {
            if (active == account) (syncText, syncDetail) = FormatSyncFailure([("iCloud 账户", ex)]);
        }
        finally
        {
            activeSync = null;
            syncLock.Release();
            if (!isClosing && (cacheDirty || revision != store.Revision))
            {
                cacheDirty = true;
                RenderAll();
            }
        }
    }
}
