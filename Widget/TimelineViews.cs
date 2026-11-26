using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinMacCalendar;

public sealed partial class CalendarCard
{
    private const double AxisWidth = 32;
    private const double TickOffset = 8;
    private const double RailWidth = 2.5;

    private List<Entry> TimelineDay(DateTime day)
    {
        var nextDay = day.AddDays(1);
        return entries.Where(e => e.On(day) || (!e.AllDay && e.Start >= nextDay && e.Start - e.Travel < nextDay))
            .OrderByDescending(e => e.AllDay).ThenBy(e => e.Start).ToList();
    }

    private void Timeline(bool wide)
    {
        var today = TimelineDay(now.Date);
        var tomorrow = now.Date.AddDays(1);
        int todayStart = Math.Min(now.Hour, 20);
        int maxSingleEnd = Math.Min(todayStart + 7, 24);
        int minSingleEnd = Math.Min(todayStart + 4, 24);

        // Compute the latest ending hour among today's timed events after todayStart.
        int latestTodayEnd = minSingleEnd;
        foreach (var ev in today.Where(e => !e.AllDay && e.Start < tomorrow && e.End > now.Date.AddHours(todayStart)))
        {
            double endHour = Math.Min(24, (ev.End - now.Date).TotalHours);
            latestTodayEnd = Math.Max(latestTodayEnd, (int)Math.Ceiling(endHour));
        }
        latestTodayEnd = Math.Clamp(latestTodayEnd, minSingleEnd, 24);

        // Apple elastic Today rule:
        // - If all remaining events today fit within a single 4..7h column (e.g. 15:59 -> 15..22 = 7h),
        //   stretch column 1 to fit them and keep the right side for tomorrow / next 3 days.
        // - Only split today across 2 columns when the required span exceeds 7h (e.g. 12:05 -> 12..22 = 10h -> 12..17 + 17..22).
        if (latestTodayEnd > maxSingleEnd)
        {
            int totalSpan = latestTodayEnd - todayStart;
            int col1Span = Math.Clamp((int)Math.Ceiling(totalSpan / 2.0), 4, 7);
            int col1End = Math.Min(todayStart + col1Span, 24);
            int col2End = Math.Clamp(latestTodayEnd, Math.Min(col1End + 4, 24), Math.Min(col1End + 7, 24));
            TimelineExtendedToday(wide, today, todayStart, col1End, col2End);
            return;
        }

        int todayEnd = Math.Clamp(latestTodayEnd, minSingleEnd, maxSingleEnd);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wide ? 1.98 : 1, GridUnitType.Star) });

        var left = BuildTodayColumn(today, todayStart, todayEnd, showOverflow: true);
        left.Margin = new Thickness(0, 0, 5, 0);
        layout.Children.Add(left);

        int days = wide ? 3 : 1;
        var future = BuildFutureColumns(days, wide, marginLeft: 4);
        Grid.SetColumn(future, 1);
        layout.Children.Add(future);
        body.Children.Add(layout);
    }

    private void TimelineExtendedToday(bool wide, List<Entry> today, int todayStart, int todayEnd, int today2End)
    {
        int today2Start = todayEnd;

        var layout = new Grid();
        if (!wide)
        {
            // ShortTimeline (2 columns): Col 0 = Today part 1, Col 1 = Today part 2 (seamless continuation)
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var col1 = BuildTodayColumn(today, todayStart, todayEnd, showOverflow: false);
            col1.Margin = new Thickness(0, 0, 5, 0);
            layout.Children.Add(col1);

            var col2 = BuildTodayContinuationColumn(today, today2Start, today2End);
            col2.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(col2, 1);
            layout.Children.Add(col2);
        }
        else
        {
            // Week (4 columns): Col 0 = Today part 1, Col 1 = Today part 2, Col 2..3 = Next 2 days (e.g. Fri, Sat)
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.42, GridUnitType.Star) });

            var col1 = BuildTodayColumn(today, todayStart, todayEnd, showOverflow: false);
            col1.Margin = new Thickness(0, 0, 5, 0);
            layout.Children.Add(col1);

            var col2 = BuildTodayContinuationColumn(today, today2Start, today2End);
            col2.Margin = new Thickness(4, 0, 6, 0);
            Grid.SetColumn(col2, 1);
            layout.Children.Add(col2);

            var future = BuildFutureColumns(days: 2, wide: true, marginLeft: 4);
            Grid.SetColumn(future, 2);
            layout.Children.Add(future);
        }

        body.Children.Add(layout);
    }

    private DockPanel BuildTodayColumn(List<Entry> today, int start, int end, bool showOverflow)
    {
        var left = new DockPanel();
        Dock(left, Tap(DateMark(), "今天的全部日程", () => openDay(now.Date), radius: 6));
        var allDay = today.Where(e => e.AllDay).ToList();
        if (allDay.Count > 0)
        {
            var strip = AllDayStrip(now.Date, allDay);
            strip.Margin = new Thickness(AxisWidth, 2, 0, 0);
            Dock(left, strip);
        }
        if (showOverflow)
        {
            var todayAfter = OverflowAfter(now.Date, today, end);
            if (todayAfter.Count > 0)
            {
                var more = MoreIndicator(todayAfter.Count, todayAfter[0], now.Date, leftInset: 0);
                more.Margin = new Thickness(AxisWidth + 2, 2, 0, 0);
                Dock(left, more, System.Windows.Controls.Dock.Bottom);
            }
        }
        var current = Plot(now.Date, today, start, end, true);
        current.Margin = new Thickness(0, allDay.Count > 0 ? 10 : 14, 0, 2);
        left.Children.Add(current);
        return left;
    }

    private DockPanel BuildTodayContinuationColumn(List<Entry> today, int start, int end)
    {
        var col = new DockPanel();
        var allDay = today.Where(e => e.AllDay).ToList();
        // Keep vertical alignment with column 0's plot area (DateMark height 38 + optional AllDay strip)
        double topSpacer = 38 + (allDay.Count > 0 ? 23 + 10 : 14);
        Dock(col, new Border { Height = topSpacer });

        var after = OverflowAfter(now.Date, today, end);
        if (after.Count > 0)
        {
            var more = MoreIndicator(after.Count, after[0], now.Date, leftInset: 0);
            more.Margin = new Thickness(AxisWidth + 2, 2, 0, 0);
            Dock(col, more, System.Windows.Controls.Dock.Bottom);
        }

        var canvas = Plot(now.Date, today, start, end, true);
        canvas.Margin = new Thickness(0, 0, 0, 2);
        col.Children.Add(canvas);
        return col;
    }

    private Grid BuildFutureColumns(int days, bool wide, double marginLeft)
    {
        var next = Enumerable.Range(1, days).Select(i => now.Date.AddDays(i)).ToArray();
        var groups = next.Select(TimelineDay).ToList();
        var (start, end) = FutureHours(next, groups);

        var overflows = Enumerable.Range(0, days).Select(i => OverflowAfter(next[i], groups[i], end)).ToList();
        bool anyOverflow = overflows.Any(o => o.Count > 0);

        var future = new Grid { Margin = new Thickness(marginLeft, 0, 0, 2) };
        future.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AxisWidth + (wide ? 1 : 0)) });
        for (int i = 0; i < days; i++)
        {
            if (i > 0) future.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            future.ColumnDefinitions.Add(new ColumnDefinition());
        }
        future.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Day heading
        future.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // All-day strip
        future.RowDefinitions.Add(new RowDefinition());                           // Timeline plot
        future.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Overflow ("其他 N 个日程")

        bool hasAllDay = groups.Any(group => group.Any(e => e.AllDay));
        for (int i = 0; i < days; i++)
        {
            var day = next[i];
            var label = Text(wide ? day.ToString("dddd", Dates.Chinese) : "明天", 11.5, muted, FontWeights.Medium);
            var heading = Tap(label, day.ToString("M月d日") + " " + Dates.LunarDate(day) + "的全部日程", () => openDay(day), radius: 4);
            heading.Margin = new Thickness(0, wide ? 2 : 1, 0, wide && hasAllDay ? 6 : 4);
            int column = 1 + 2 * i;
            Grid.SetColumn(heading, column);
            future.Children.Add(heading);

            var all = groups[i].Where(e => e.AllDay).ToList();
            if (all.Count > 0)
            {
                var strip = AllDayStrip(day, all);
                Grid.SetRow(strip, 1);
                Grid.SetColumn(strip, column);
                future.Children.Add(strip);
            }

            var canvas = Plot(day, groups[i], start, end, false);
            canvas.Margin = new Thickness(0, hasAllDay ? 9 : 10, 0, 0);
            Grid.SetRow(canvas, 2);
            Grid.SetColumn(canvas, column);
            future.Children.Add(canvas);

            if (overflows[i].Count > 0)
            {
                var more = MoreIndicator(overflows[i].Count, overflows[i][0], day, leftInset: 2);
                more.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetRow(more, 3);
                Grid.SetColumn(more, column);
                future.Children.Add(more);
            }
            else if (anyOverflow)
            {
                var spacer = new Border { Height = 16 };
                Grid.SetRow(spacer, 3);
                Grid.SetColumn(spacer, column);
                future.Children.Add(spacer);
            }
        }

        var axis = new Canvas { Margin = new Thickness(0, hasAllDay ? 9 : 10, 0, 0), ClipToBounds = true };
        axis.SizeChanged += (_, _) => DrawTicks(axis, start, end, 0, false, true);
        Grid.SetRow(axis, 2);
        future.Children.Add(axis);

        return future;
    }

    private static List<Entry> OverflowAfter(DateTime day, List<Entry> dayEntries, int endHour)
    {
        var cutoff = day.AddHours(endHour);
        var nextDay = day.AddDays(1);
        return dayEntries
            .Where(e => !e.AllDay && e.Start < nextDay && e.Start - e.Travel >= cutoff)
            .OrderBy(e => e.Start)
            .ToList();
    }

    private FrameworkElement AllDayStrip(DateTime date, List<Entry> events)
    {
        var strip = new DockPanel { Height = 21 };
        if (events.Count > 1)
        {
            var more = Tap(Text("+" + (events.Count - 1), 10.5, muted, FontWeights.Medium), "查看全天日程", () => openDay(date), radius: 4);
            more.Margin = new Thickness(3, 0, 0, 0);
            Dock(strip, more, System.Windows.Controls.Dock.Right);
        }
        strip.Children.Add(EventTile(events[0], EventStyle.Strip, showTravel: false));
        return strip;
    }

    private static (int Start, int End) FutureHours(DateTime[] dates, List<List<Entry>> groups)
    {
        var perDay = new List<List<(double StartHour, double EndHour)>>();
        for (int i = 0; i < dates.Length; i++)
        {
            var nextDay = dates[i].AddDays(1);
            var list = new List<(double, double)>();
            foreach (var ev in groups[i].Where(e => !e.AllDay && e.Start < nextDay))
            {
                double s = Math.Max(0, (ev.Start - ev.Travel - dates[i]).TotalHours);
                double eHour = Math.Min(24, (ev.End - dates[i]).TotalHours);
                list.Add((s, eHour));
            }
            perDay.Add(list);
        }

        // Anchor on the first future day that has timed events (typically tomorrow) so later days' early
        // morning events never chop off tomorrow's afternoon schedule.
        int anchorIdx = perDay.FindIndex(l => l.Count > 0);
        if (anchorIdx < 0) return (9, 16);

        var anchor = perDay[anchorIdx];
        int start = Math.Clamp((int)Math.Floor(anchor.Min(t => t.StartHour)), 0, 20);
        int maxWindowEnd = Math.Min(start + 7, 24);
        double anchorLatest = anchor
            .Where(t => t.StartHour < maxWindowEnd)
            .Select(t => Math.Min(t.EndHour, maxWindowEnd))
            .DefaultIfEmpty(Math.Min(anchor.Max(t => t.EndHour), maxWindowEnd))
            .Max();
        int end = Math.Clamp((int)Math.Ceiling(anchorLatest), Math.Min(start + 4, 24), maxWindowEnd);

        // Expand slightly to also include compatible events on subsequent days if total span stays within 7h.
        for (int i = anchorIdx + 1; i < perDay.Count; i++)
        {
            foreach (var (s, eHour) in perDay[i])
            {
                int candStart = Math.Min(start, (int)Math.Floor(s));
                int candEnd = Math.Max(end, (int)Math.Ceiling(eHour));
                if (candEnd - candStart <= 7 && candStart >= 0 && candEnd <= 24)
                {
                    start = candStart;
                    end = candEnd;
                }
            }
        }
        return (start, end);
    }

    private Canvas Plot(DateTime date, List<Entry> entries, int start, int end, bool current)
    {
        var canvas = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };
        canvas.SizeChanged += (_, _) => DrawTimeline(canvas, date, entries, start, end, current);
        return canvas;
    }

    private static void Place(Canvas canvas, UIElement item, double x, double y)
    {
        Canvas.SetLeft(item, x);
        Canvas.SetTop(item, y);
        canvas.Children.Add(item);
    }

    private static double Step(Canvas canvas, int start, int end) => (canvas.ActualHeight - TickOffset - 10) / (end - start);

    private void DrawTicks(Canvas canvas, int start, int end, double inset, bool lines, bool labels)
    {
        canvas.Children.Clear();
        double step = Step(canvas, start, end);
        if (step <= 0 || canvas.ActualWidth <= inset) return;
        for (int h = start; h <= end; h++)
        {
            double y = TickOffset + (h - start) * step;
            if (labels) Place(canvas, Text($"{h % 24} 时", 10, muted), 0, y - 7);
            if (lines) Place(canvas, new Border { Width = canvas.ActualWidth - inset, Height = 0.65, Background = rule }, inset, y);
        }
    }

    private Button TimedBlock(Entry ev, double width, double height, double travelHeight)
    {
        var colors = Colors(ev, true);
        var rows = new Grid { ClipToBounds = true };
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(travelHeight) });
        rows.RowDefinitions.Add(new RowDefinition());

        // Align the final travel dot to the start of the solid rail, including when
        // the top of the travel interval lies outside the displayed hours.
        if (travelHeight > 0)
        {
            var dots = new Canvas
            {
                Width = RailWidth,
                Margin = new Thickness(3, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = true
            };
            for (double y = travelHeight - 2 * RailWidth; y >= 1; y -= 2 * RailWidth)
                Place(dots, new Ellipse { Width = RailWidth, Height = RailWidth, Fill = colors.Accent }, 0, y);
            if (dots.Children.Count == 0 && travelHeight >= RailWidth)
                Place(dots, new Ellipse { Width = RailWidth, Height = RailWidth, Fill = colors.Accent }, 0, (travelHeight - RailWidth) / 2);
            rows.Children.Add(dots);
        }

        if (travelHeight < height)
        {
            var rail = new Border
            {
                Width = RailWidth,
                CornerRadius = new CornerRadius(RailWidth / 2),
                Background = colors.Accent,
                Margin = new Thickness(3, travelHeight > 0 ? 0 : 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetRow(rail, 1);
            rows.Children.Add(rail);

            double bodyHeight = height - travelHeight;
            var labels = new StackPanel { Margin = new Thickness(8.5, 1.5, 4, 0), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
            bool hasLocation = !string.IsNullOrWhiteSpace(ev.Location);
            var title = Text(ev.Title, 11, colors.Text, FontWeights.SemiBold);
            title.TextWrapping = hasLocation && bodyHeight < 52 ? TextWrapping.NoWrap : TextWrapping.Wrap;
            title.MaxHeight = hasLocation ? 28 : 30;
            TightLines(title, 14);
            labels.Children.Add(title);

            if (bodyHeight >= 25)
            {
                var startText = Text(ev.Start.ToString("HH:mm"), 10.5, colors.SubText);
                TightLines(startText, 13.5);
                labels.Children.Add(startText);
            }
            if (hasLocation && bodyHeight >= 39)
            {
                var locText = Text(ev.Location, 10.5, colors.SubText);
                TightLines(locText, 13.5);
                labels.Children.Add(locText);
            }

            Grid.SetRow(labels, 1);
            rows.Children.Add(labels);
        }

        var shape = new Border { CornerRadius = new CornerRadius(4.5), Background = colors.Wash, Child = rows };
        string name = ev.Source.Name + " · " + ev.Title + " · " + ev.TimeRange;
        if (ev.Travel > TimeSpan.Zero)
            name += $" · {(ev.Start - ev.Travel):HH:mm} 出发（行程 {(int)ev.Travel.TotalMinutes} 分钟）";
        if (!string.IsNullOrWhiteSpace(ev.Location))
            name += " · " + ev.Location;
        var button = Tap(shape, name, () => openEvent(ev), highlightBackground: false, radius: 4.5);
        button.Width = width;
        button.Height = height;
        button.ClipToBounds = true;
        return button;
    }

    private void DrawTimeline(Canvas canvas, DateTime date, List<Entry> all, int start, int end, bool current)
    {
        double gutter = current ? AxisWidth : 0;
        DrawTicks(canvas, start, end, gutter, true, current);
        double step = Step(canvas, start, end);
        if (step <= 0 || canvas.ActualWidth <= gutter) return;
        var from = date.AddHours(start);
        var until = date.AddHours(end);
        var events = all.Where(e => !e.AllDay && e.Start - e.Travel < until && (e.End > from || e.Start == from))
            .OrderBy(e => e.Start - e.Travel).ToList();

        int index = 0;
        while (index < events.Count)
        {
            var group = new List<(Entry Event, int Lane)>();
            var ends = new List<DateTime>();
            var boundary = events[index].End;
            do
            {
                var ev = events[index++];
                int lane = ends.FindIndex(t => t <= ev.Start - ev.Travel);
                if (lane < 0) { lane = ends.Count; ends.Add(ev.End); } else ends[lane] = ev.End;
                group.Add((ev, lane));
                if (ev.End > boundary) boundary = ev.End;
            } while (index < events.Count && events[index].Start - events[index].Travel < boundary);

            double width = (canvas.ActualWidth - gutter) / ends.Count;
            foreach (var (ev, lane) in group)
            {
                double top = Math.Max(0, (ev.Start - ev.Travel - from).TotalHours) * step + TickOffset;
                double bottom = Math.Min(end - start, (ev.End - from).TotalHours) * step + TickOffset;
                double rawHeight = bottom - top;
                double height = ev.End > from && ev.Start < until ? Math.Max(16, rawHeight) : Math.Max(4, rawHeight);
                double travelHeight = ev.Start >= until ? height :
                    Math.Clamp((ev.Start - from).TotalHours * step + TickOffset - top, 0, Math.Max(0, rawHeight));
                var block = TimedBlock(ev, Math.Max(1, width - 1), height, travelHeight);
                Place(canvas, block, gutter + lane * width, top);
            }
        }

        if (current && now >= from && now <= until)
        {
            double y = TickOffset + (now - from).TotalHours * step;
            Place(canvas, new Border { Width = canvas.ActualWidth, Height = 1.3, Background = red, IsHitTestVisible = false }, 0, y);
            Place(canvas, new Ellipse { Width = 5.5, Height = 5.5, Fill = red, IsHitTestVisible = false }, 0, y - 2.1);
        }
    }
}
