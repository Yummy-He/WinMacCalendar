using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinMacCalendar;

public sealed partial class CalendarCard
{
    private enum EventStyle { Summary, List, Strip }
    private sealed record EventColors(Brush Accent, Brush Text, Brush SubText, Brush Wash);
    private static readonly Dictionary<(string Accent, bool Strong, bool Night), EventColors> SharedColorCache = new();
    private static readonly Geometry CarGeometry = FreezeGeometry("M2,7 L4,2 H12 L14,7 M2,7 H14 Q15,7 15,9 V12 H1 V9 Q1,7 2,7 M3,12 V14 M13,12 V14 M3,9.5 H5 M11,9.5 H13");
    private static readonly Geometry PinGeometry = FreezeGeometry("M8,1.5 A5,5 0 0 1 13,6.5 C13,10 8,15 8,15 C8,15 3,10 3,6.5 A5,5 0 0 1 8,1.5 Z M8,4.8 A1.8,1.8 0 1 0 8,8.4 A1.8,1.8 0 1 0 8,4.8 Z");

    private static Geometry FreezeGeometry(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static double RelativeLuminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private Brush ResolveAccentBrush(string hex)
    {
        var raw = (SolidColorBrush)Palette.Color(hex);
        if (night && RelativeLuminance(raw.Color) < 0.20)
            return Palette.Color("#A1A1A6");
        return raw;
    }

    private EventColors Colors(Entry ev, bool strong)
    {
        var key = (ev.Source.Accent, strong, night);
        if (SharedColorCache.TryGetValue(key, out var cached)) return cached;

        var raw = (SolidColorBrush)Palette.Color(ev.Source.Accent);
        double lum = RelativeLuminance(raw.Color);
        bool veryDark = lum < 0.18;
        bool nearNeutral = Math.Max(raw.Color.R, Math.Max(raw.Color.G, raw.Color.B)) - Math.Min(raw.Color.R, Math.Min(raw.Color.G, raw.Color.B)) < 20;

        SolidColorBrush accent = veryDark && night
            ? (SolidColorBrush)Palette.Color("#A1A1A6")
            : veryDark && !night && !strong
                ? (SolidColorBrush)Palette.Color("#1C1C1E")
                : raw;

        Brush Mix(byte baseColor, double amount, byte alpha = 255)
        {
            byte Channel(byte value) => (byte)Math.Clamp(Math.Round(baseColor + (value - baseColor) * amount), 0, 255);
            var color = accent.Color;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, Channel(color.R), Channel(color.G), Channel(color.B)));
            brush.Freeze();
            return brush;
        }

        // Let the underlying hour grid show faintly through the timeline tint.
        const byte washAlpha = 180;
        double tint = night ? 0.34 : (veryDark ? 0.18 : 0.22);
        double softTint = night ? (veryDark ? 0.20 : 0.22) : (veryDark ? 0.085 : 0.115);

        Brush wash = strong
            ? Mix(night ? (byte)36 : (byte)255, tint * 255 / washAlpha, washAlpha)
            : Mix(night ? (byte)36 : (byte)255, softTint);

        Brush text = night
            ? (nearNeutral ? Palette.Color("#F5F5F7") : Mix(255, 0.34))
            : (veryDark ? Palette.Color("#1C1C1E") : Mix(0, 0.56));

        Brush subText = night
            ? (nearNeutral ? Palette.Color("#D1D1D6") : Mix(255, 0.44, 225))
            : (veryDark ? Palette.Color("#3A3A3C") : Mix(0, 0.60, 215));

        var colors = new EventColors(accent, text, subText, wash);
        SharedColorCache[key] = colors;
        return colors;
    }

    private bool ShouldShowTravelRow(Entry ev, bool isCompactFuture)
    {
        if (ev.AllDay || ev.Travel <= TimeSpan.Zero) return false;
        return travelMode switch
        {
            TravelDisplayMode.Never => false,
            TravelDisplayMode.Always => true,
            // Apple behavior: Summary future-day cards omit travel row to stay compact;
            // Today cards & Agenda list show travel time.
            _ => !isCompactFuture
        };
    }

    private static Path CarGlyph(Brush stroke) => new()
    {
        Width = 12,
        Height = 10,
        Stretch = Stretch.Uniform,
        Stroke = stroke,
        StrokeThickness = 1.35,
        StrokeLineJoin = PenLineJoin.Round,
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Data = CarGeometry
    };

    private static Path PinGlyph(Brush stroke) => new()
    {
        Width = 10,
        Height = 11.5,
        Stretch = Stretch.Uniform,
        Stroke = stroke,
        StrokeThickness = 1.35,
        StrokeLineJoin = PenLineJoin.Round,
        Margin = new Thickness(1, 0, 4.5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Data = PinGeometry
    };

    private FrameworkElement Badge(bool holiday, Brush accent, double size)
    {
        var badge = new Grid { Width = size, Height = size, Margin = new Thickness(1, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(new Ellipse { Fill = accent });
        if (holiday)
        {
            var star = Text("★", size * 0.72, Brushes.White);
            star.HorizontalAlignment = HorizontalAlignment.Center;
            badge.Children.Add(star);
        }
        else
        {
            var glyph = new Grid { Width = size * 0.48, Height = size * 0.44 };
            glyph.Children.Add(new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(0.75), CornerRadius = new CornerRadius(1) });
            glyph.Children.Add(new Border { Background = Brushes.White, Height = 0.8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 2, 1, 0) });
            var dots = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(1.5, 3, 1.5, 1) };
            for (int i = 0; i < 6; i++) dots.Children.Add(new Ellipse { Fill = Brushes.White, Width = 0.9, Height = 0.9 });
            glyph.Children.Add(dots);
            badge.Children.Add(glyph);
        }
        return badge;
    }

    private string AllDayRange(Entry ev)
    {
        var last = ev.End.Date.AddDays(-1);
        return last <= ev.Start.Date ? "全天" : ev.Start.ToString("M月d日") + "至" +
            last.ToString(last.Month == ev.Start.Month && last.Year == ev.Start.Year ? "d日" : "M月d日");
    }

    private Button EventTile(Entry ev, EventStyle style, bool showTravel = true, bool showLocation = false, bool dimmed = false)
    {
        bool list = style == EventStyle.List;
        bool strip = style == EventStyle.Strip;
        var colors = Colors(ev, strip);
        double titleSize = list ? 12.5 : strip ? 11.5 : 12;
        double metaSize = list ? 12 : 11;
        bool includeTravel = showTravel && !ev.AllDay && ev.Travel > TimeSpan.Zero;
        bool includeLocation = showLocation && !ev.AllDay && !string.IsNullOrWhiteSpace(ev.Location);

        var content = new DockPanel
        {
            Margin = list ? new Thickness(6, 5.5, 10, 5.5) : strip ? new Thickness(3, 1, 5, 1) : new Thickness(4, 3, 6, 3)
        };

        if (ev.AllDay)
        {
            Dock(content, Badge(ev.Holiday, colors.Accent, list ? 17 : 14), System.Windows.Controls.Dock.Left);
            if (list)
            {
                var when = Text(AllDayRange(ev), metaSize, colors.SubText);
                when.Margin = new Thickness(8, 0, 0, 0);
                Dock(content, when, System.Windows.Controls.Dock.Right);
            }
            content.Children.Add(Text(ev.Title, titleSize, colors.Text, FontWeights.SemiBold));
        }
        else
        {
            Dock(content, new Border
            {
                Background = colors.Accent,
                Width = list ? 2.8 : 2.5,
                CornerRadius = new CornerRadius(1.4),
                Margin = new Thickness(1, 1, 5.5, 1)
            }, System.Windows.Controls.Dock.Left);

            if (list)
            {
                var times = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                if (includeTravel)
                {
                    var dep = Text((ev.Start - ev.Travel).ToString("HH:mm"), metaSize, colors.SubText);
                    dep.HorizontalAlignment = HorizontalAlignment.Right;
                    TightLines(dep, 18.5);
                    times.Children.Add(dep);
                }
                var startLabel = Text(ev.Start.ToString("HH:mm"), metaSize, colors.Text, FontWeights.Medium);
                startLabel.HorizontalAlignment = HorizontalAlignment.Right;
                TightLines(startLabel, 18.5);
                times.Children.Add(startLabel);

                var endLabel = Text(ev.End.ToString("HH:mm"), metaSize, colors.SubText);
                endLabel.HorizontalAlignment = HorizontalAlignment.Right;
                TightLines(endLabel, 18.5);
                times.Children.Add(endLabel);
                Dock(content, times, System.Windows.Controls.Dock.Right);
            }

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (includeTravel)
            {
                var travel = Row();
                if (list) travel.Children.Add(CarGlyph(colors.Accent));
                var travelText = Text($"行程时间 {(int)ev.Travel.TotalMinutes} 分钟", metaSize, colors.SubText);
                TightLines(travelText, list ? 18.5 : 15);
                travel.Children.Add(travelText);
                labels.Children.Add(travel);
            }

            var title = Text(ev.Title, titleSize, colors.Text, FontWeights.SemiBold);
            TightLines(title, list ? 18.5 : 15.5);
            labels.Children.Add(title);

            if (includeLocation)
            {
                var locRow = Row();
                if (list) locRow.Children.Add(PinGlyph(colors.SubText));
                var locText = Text(ev.Location, metaSize, colors.SubText);
                TightLines(locText, list ? 18.5 : 15);
                locRow.Children.Add(locText);
                labels.Children.Add(locRow);
            }

            if (!list)
            {
                var timeText = Text(ev.TimeRange, metaSize, colors.SubText);
                TightLines(timeText, 15);
                labels.Children.Add(timeText);
            }
            content.Children.Add(labels);
        }

        double corner = ev.AllDay ? (list ? 14 : 9.5) : (list ? 6 : 4.5);
        var shape = new Border
        {
            Background = colors.Wash,
            CornerRadius = new CornerRadius(corner),
            Child = content,
            ClipToBounds = true
        };

        if (list)
        {
            int rows = (includeTravel ? 1 : 0) + 1 + (includeLocation ? 1 : 0);
            shape.MinHeight = ev.AllDay ? 29 : rows >= 3 ? 66 : 48;
            content.VerticalAlignment = VerticalAlignment.Center;
        }
        else if (style == EventStyle.Summary)
        {
            int rows = (includeTravel ? 1 : 0) + 1 + (includeLocation ? 1 : 0) + 1;
            shape.MinHeight = ev.AllDay ? 21 : rows >= 4 ? 66 : rows == 3 ? 51 : 36;
        }

        string tooltip = ev.Source.Name + " · " + ev.Title + " · " + ev.TimeRange;
        if (!string.IsNullOrEmpty(ev.Location)) tooltip += " · " + ev.Location;
        var button = Tap(shape, tooltip, () => openEvent(ev), highlightBackground: false, radius: corner);
        button.Margin = new Thickness(0, list ? 2.5 : 1.5, 0, list ? 2.5 : 1.5);
        if (dimmed) button.Opacity = 0.52;
        return button;
    }

    private Button MoreIndicator(int count, Entry nextHidden, DateTime day, double leftInset = 5)
    {
        var accent = ResolveAccentBrush(nextHidden.Source.Accent);
        var row = Row();
        row.Margin = new Thickness(leftInset, 1, 4, 1);
        row.Children.Add(new Border
        {
            Background = accent,
            Width = 2.5,
            Height = 11.5,
            CornerRadius = new CornerRadius(1.25),
            Margin = new Thickness(0, 0, 5.5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(Text($"其他 {count} 个日程", 11, muted, FontWeights.Medium));

        var button = Tap(row, day.ToString("M月d日") + "的全部日程", () => openDay(day), radius: 4);
        button.Margin = new Thickness(0, 1, 0, 1);
        return button;
    }

    private IEnumerable<DateTime> FutureGroups()
    {
        var start = now.Date.AddDays(1);
        var horizon = now.Date.AddDays(32);
        var days = new SortedSet<DateTime>();
        foreach (var ev in entries)
        {
            var last = ev.AllDay || (ev.End > ev.Start && ev.End.TimeOfDay == TimeSpan.Zero)
                ? ev.End.Date.AddDays(-1)
                : ev.End.Date;
            if (last < ev.Start.Date) last = ev.Start.Date;
            var from = ev.Start.Date > start ? ev.Start.Date : start;
            var to = last < horizon ? last : horizon;
            for (var d = from; d <= to; d = d.AddDays(1)) days.Add(d);
        }
        return days;
    }

    private static double EstimateSummaryTileHeight(Entry ev, bool showTravel, bool showLocation)
    {
        if (ev.AllDay) return 21 + 3;
        int rows = (showTravel && ev.Travel > TimeSpan.Zero ? 1 : 0)
                 + 1
                 + (showLocation && !string.IsNullOrWhiteSpace(ev.Location) ? 1 : 0)
                 + 1;
        double minH = rows >= 4 ? 66 : rows == 3 ? 51 : 36;
        return minH + 3;
    }

    private void Summary(bool wide)
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        if (wide) columns.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel { Margin = new Thickness(0, 0, wide ? 10 : 0, 0), ClipToBounds = true };
        Dock(left, Tap(DateMark(), "今天的全部日程", () => openDay(now.Date), radius: 6));

        var remaining = Day(now.Date).Where(e => e.AllDay || e.End > now).ToList();
        const double contentHeight = 183 - 15 - 16 - 2; // 150px usable inside border + padding
        const double moreHeight = 18;
        int todayShownLeft = 0;

        if (remaining.Count == 0)
        {
            var empty = Text("今天无日程", 14.5, muted);
            empty.VerticalAlignment = VerticalAlignment.Center;
            empty.Margin = new Thickness(1, 6, 0, 0);
            left.Children.Add(empty);
        }
        else if (!wide)
        {
            // Compact (Small 183x183): prioritize showing as many of today's upcoming timed events as fit.
            var list = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
            double budget = contentHeight - 38 - 7; // 105px
            var timed = remaining.Where(e => !e.AllDay).ToList();
            if (timed.Count == 1)
            {
                // Single upcoming timed event -> show full detail (travel + title + location + time).
                var primary = timed[0];
                bool showTravel = ShouldShowTravelRow(primary, isCompactFuture: false);
                list.Children.Add(EventTile(primary, EventStyle.Summary, showTravel, showLocation: true));
            }
            else if (timed.Count >= 2)
            {
                // Multiple upcoming timed events today -> compress metadata to fit 2+ events (matching Apple 1126).
                // First event drops travel time but keeps location if both still fit; subsequent events use 2-line compact.
                var first = timed[0];
                var second = timed[1];
                bool firstKeepLoc = !string.IsNullOrWhiteSpace(first.Location)
                    && EstimateSummaryTileHeight(first, false, true) + EstimateSummaryTileHeight(second, false, false)
                       + (timed.Count > 2 ? moreHeight : 0) <= budget;

                int shown = 0;
                for (int i = 0; i < timed.Count; i++)
                {
                    var ev = timed[i];
                    bool loc = i == 0 && firstKeepLoc;
                    double h = EstimateSummaryTileHeight(ev, false, loc);
                    bool hasMore = i < timed.Count - 1;
                    if (shown > 0 && budget < (hasMore ? h + moreHeight : h)) break;
                    list.Children.Add(EventTile(ev, EventStyle.Summary, showTravel: false, showLocation: loc));
                    budget -= h;
                    shown++;
                }
                int hiddenCount = timed.Count - shown;
                if (hiddenCount > 0 && shown > 0 && budget >= moreHeight - 2)
                    list.Children.Add(MoreIndicator(hiddenCount, timed[shown], now.Date));
            }
            else
            {
                // Only all-day events today.
                FillDayEvents(list, now.Date, remaining, ref budget, isCompactFuture: false, showLocation: false, allowOverflowIndicator: true);
            }
            left.Children.Add(list);
        }
        else
        {
            // Wide Summary left column: pack today's events without folding yet; overflow spills to right column first!
            var list = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
            double leftBudget = contentHeight - 38 - 7; // 105px
            for (int i = 0; i < remaining.Count; i++)
            {
                var ev = remaining[i];
                bool showTravel = ShouldShowTravelRow(ev, isCompactFuture: false);
                double h = EstimateSummaryTileHeight(ev, showTravel, showLocation: true);
                if (todayShownLeft > 0 && leftBudget < h) break;
                list.Children.Add(EventTile(ev, EventStyle.Summary, showTravel, showLocation: true));
                leftBudget -= h;
                todayShownLeft++;
            }
            left.Children.Add(list);
        }
        columns.Children.Add(left);

        if (wide)
        {
            var right = new StackPanel { Margin = new Thickness(10, 1, 0, 0), ClipToBounds = true };
            double budget = contentHeight - 1; // 149px
            bool todayOverflowedRight = false;

            // Phase A (Today Priority): First render any remaining today events that didn't fit in left column.
            if (todayShownLeft < remaining.Count)
            {
                var spilledToday = remaining.Skip(todayShownLeft).ToList();
                todayOverflowedRight = FillDayEvents(
                    right,
                    now.Date,
                    spilledToday,
                    ref budget,
                    isCompactFuture: false,
                    showLocation: true,
                    allowOverflowIndicator: true,
                    moreHeight: moreHeight);
            }

            // Phase B (Future Days): Only after ALL of today's events are displayed, fill remaining height with tomorrow & future days.
            if (!todayOverflowedRight)
            {
                bool firstFuture = right.Children.Count == 0;
                foreach (var day in FutureGroups())
                {
                    var group = Day(day).ToList();
                    if (group.Count == 0) continue;

                    double labelTop = firstFuture ? 0 : 7;
                    double labelHeight = labelTop + 15 + 3;
                    bool firstTravel = ShouldShowTravelRow(group[0], isCompactFuture: true);
                    double firstTileHeight = EstimateSummaryTileHeight(group[0], firstTravel, showLocation: false);

                    if (budget < labelHeight + firstTileHeight) break;

                    var label = Text(DayName(day), 11, muted, FontWeights.Medium);
                    label.Margin = new Thickness(2, labelTop, 0, 3);
                    right.Children.Add(label);
                    budget -= labelHeight;
                    firstFuture = false;

                    bool overflowed = FillDayEvents(
                        right,
                        day,
                        group,
                        ref budget,
                        isCompactFuture: true,
                        showLocation: false,
                        allowOverflowIndicator: true,
                        moreHeight: moreHeight);
                    if (overflowed) break;
                }
            }

            Grid.SetColumn(right, 1);
            columns.Children.Add(right);
        }

        body.Children.Add(columns);
    }

    private bool FillDayEvents(
        StackPanel container,
        DateTime day,
        IReadOnlyList<Entry> group,
        ref double budget,
        bool isCompactFuture,
        bool showLocation,
        bool allowOverflowIndicator,
        double moreHeight = 18)
    {
        int shown = 0;
        for (int i = 0; i < group.Count; i++)
        {
            var ev = group[i];
            bool showTravel = ShouldShowTravelRow(ev, isCompactFuture);
            double tileHeight = EstimateSummaryTileHeight(ev, showTravel, showLocation);
            bool hasMoreAfter = i < group.Count - 1;

            double required = hasMoreAfter && allowOverflowIndicator ? tileHeight + moreHeight : tileHeight;
            if (shown > 0 && budget < required)
                break;

            if (shown == 0 && budget < tileHeight)
                break;

            container.Children.Add(EventTile(ev, EventStyle.Summary, showTravel, showLocation));
            budget -= tileHeight;
            shown++;
        }

        int hiddenCount = group.Count - shown;
        if (allowOverflowIndicator && hiddenCount > 0 && shown > 0 && budget >= moreHeight - 2)
        {
            container.Children.Add(MoreIndicator(hiddenCount, group[shown], day));
            budget -= moreHeight;
            return true;
        }
        return hiddenCount > 0;
    }

    private static double EstimateListTileHeight(Entry ev, bool showTravel, bool showLocation)
    {
        if (ev.AllDay) return 29 + 5;
        int rows = (showTravel && ev.Travel > TimeSpan.Zero ? 1 : 0)
                 + 1
                 + (showLocation && !string.IsNullOrWhiteSpace(ev.Location) ? 1 : 0);
        return (rows >= 3 ? 66 : 48) + 5;
    }

    private void Agenda()
    {
        double pageHeight = Sizes[(int)Variant.Agenda].Height - Root.Padding.Top - Root.Padding.Bottom - 2;
        var scroll = new StackPanel();
        var firstPage = new StackPanel { MinHeight = pageHeight };
        scroll.Children.Add(firstPage);

        double used = 0;
        bool onFirstPage = true;
        int groups = 0;
        const double dividerHeight = 20;
        const double moreHeight = 19;

        foreach (var day in FutureGroups().Prepend(now.Date))
        {
            bool today = day == now.Date;
            // Match Apple Calendar Agenda: today only lists all-day + upcoming/ongoing events.
            var items = today
                ? Day(day).Where(e => e.AllDay || e.End > now).ToList()
                : Day(day).ToList();

            double divH = groups > 0 ? dividerHeight : 0;
            double captionHeight = (today ? 1 : 0) + 18 + 6;
            double firstItemHeight = items.Count == 0
                ? 34
                : EstimateListTileHeight(items[0], ShouldShowTravelRow(items[0], false), true);

            if (onFirstPage && groups > 0)
            {
                double remainingSpace = pageHeight - used;
                if (remainingSpace < divH + captionHeight + firstItemHeight)
                {
                    // If there isn't room for a full event card, check if we can show "Date Header + 其他 N 个日程"
                    // cleanly on the first page (matching Apple 1126 Agenda), while placing full items in scroll below.
                    if (items.Count > 0 && remainingSpace >= divH + captionHeight + moreHeight)
                    {
                        firstPage.Children.Add(new Border
                        {
                            Height = 0.7,
                            Background = rule,
                            Opacity = 0.65,
                            Margin = new Thickness(0, 9, 0, 9)
                        });
                        var summaryCap = Caption(DayName(day), Dates.LunarDate(day), 13, false);
                        summaryCap.Margin = new Thickness(0, 0, 0, 5);
                        firstPage.Children.Add(summaryCap);
                        firstPage.Children.Add(MoreIndicator(items.Count, items[0], day, leftInset: 2));
                    }
                    onFirstPage = false;
                }
            }

            if (onFirstPage)
            {
                if (groups > 0)
                {
                    firstPage.Children.Add(new Border
                    {
                        Height = 0.7,
                        Background = rule,
                        Opacity = 0.65,
                        Margin = new Thickness(0, 9, 0, 9)
                    });
                    used += divH;
                }

                var caption = Caption(today ? day.ToString("M月d日 dddd", Dates.Chinese) : DayName(day), Dates.LunarDate(day), 13, today);
                caption.Margin = new Thickness(0, today ? 1 : 0, 0, 6);
                firstPage.Children.Add(caption);
                used += captionHeight;

                if (items.Count == 0)
                {
                    var empty = Text("今天无日程", 14.5, muted);
                    empty.Margin = new Thickness(0, 8, 0, 6);
                    firstPage.Children.Add(empty);
                    used += 34;
                }
                else
                {
                    int shown = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var ev = items[i];
                        bool showTravel = ShouldShowTravelRow(ev, isCompactFuture: false);
                        double itemH = EstimateListTileHeight(ev, showTravel, true);
                        bool hasMore = i < items.Count - 1;
                        if (shown > 0 && used + (hasMore ? itemH + moreHeight : itemH) > pageHeight)
                        {
                            firstPage.Children.Add(MoreIndicator(items.Count - shown, items[shown], day, leftInset: 2));
                            used += moreHeight;
                            onFirstPage = false;
                            break;
                        }
                        firstPage.Children.Add(EventTile(ev, EventStyle.List, showTravel, showLocation: true));
                        used += itemH;
                        shown++;
                    }
                }
            }
            else
            {
                // Subsequent pages inside the ScrollViewer render full groups
                scroll.Children.Add(new Border
                {
                    Height = 0.7,
                    Background = rule,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 9, 0, 9)
                });
                var caption = Caption(DayName(day), Dates.LunarDate(day), 13, false);
                caption.Margin = new Thickness(0, 0, 0, 6);
                scroll.Children.Add(caption);
                foreach (var ev in items)
                {
                    bool showTravel = ShouldShowTravelRow(ev, isCompactFuture: false);
                    scroll.Children.Add(EventTile(ev, EventStyle.List, showTravel, showLocation: true));
                }
            }

            groups++;
        }

        body.Children.Add(new ScrollViewer { Content = scroll, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
    }
}
