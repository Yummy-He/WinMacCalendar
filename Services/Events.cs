using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace WinMacCalendar;

public sealed record Entry(
    Resource File,
    string Title,
    DateTime Start,
    DateTime End,
    bool AllDay,
    bool Editable,
    TimeSpan Travel = default,
    bool Holiday = false,
    string Location = "",
    string Description = "",
    string RecurrenceSummary = "")
{
    public CalendarSource Source { get; init; } = CalendarSource.Local;
    public bool On(DateTime day) => Start < day.AddDays(1) && (End > day || Start.Date == day);
    public string TimeRange => AllDay ? "全天" : $"{Start:HH:mm} – {End:HH:mm}";
}

public static class Events
{
    public static bool SameEventVersion(string local, string remote)
    {
        var a = Calendar.Load(local)?.Events.SingleOrDefault();
        var b = Calendar.Load(remote)?.Events.SingleOrDefault();
        return a != null && b != null && a.Uid == b.Uid && a.Summary == b.Summary && a.Sequence == b.Sequence
            && Equals(a.DtStart, b.DtStart) && Equals(a.DtEnd, b.DtEnd);
    }

    private static DateTime Local(CalDateTime t) => !t.HasTime || t.TzId is null ? t.Value : t.AsUtc.ToLocalTime();
    private static CalDateTime Stamp(DateTime t, bool allDay) => allDay ? new(t.Year, t.Month, t.Day) : new(t.ToUniversalTime(), "UTC");

    private static string DescribeRecurrence(CalendarEvent ev)
    {
        var rrule = ev.RecurrenceRule;
        if (rrule != null)
        {
            string unit = rrule.Frequency switch
            {
                FrequencyType.Daily => "天",
                FrequencyType.Weekly => "周",
                FrequencyType.Monthly => "个月",
                FrequencyType.Yearly => "年",
                _ => ""
            };
            if (unit != "")
            {
                int interval = Math.Max(1, rrule.Interval);
                string freq = interval == 1 ? (unit == "个月" ? "每月" : "每" + unit) : $"每 {interval} {unit}";
                if (rrule.Until != null)
                    return $"{freq}重复，截至 {Local(rrule.Until):yyyy年M月d日}";
                if (rrule.Count is > 0)
                    return $"{freq}重复（共 {rrule.Count} 次）";
                return $"{freq}重复";
            }
            return "重复日程";
        }
        if (ev.Properties.Any(p => p.Name is "RDATE" or "RECURRENCE-ID")) return "重复日程";
        return "";
    }

    private static bool TryExtractIsoDate(string ics, string propName, out DateTime date)
    {
        date = default;
        int idx = ics.IndexOf(propName, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        int colon = ics.IndexOf(':', idx + propName.Length);
        if (colon < 0 || colon + 9 > ics.Length) return false;
        int pos = colon + 1;
        // Expect YYYYMMDD (at least 8 digits)
        for (int i = 0; i < 8; i++)
            if (!char.IsAsciiDigit(ics[pos + i])) return false;
        int y = (ics[pos] - '0') * 1000 + (ics[pos + 1] - '0') * 100 + (ics[pos + 2] - '0') * 10 + (ics[pos + 3] - '0');
        int m = (ics[pos + 4] - '0') * 10 + (ics[pos + 5] - '0');
        int d = (ics[pos + 6] - '0') * 10 + (ics[pos + 7] - '0');
        if (y is < 1900 or > 2200 || m is < 1 or > 12 || d is < 1 or > 31) return false;
        try { date = new DateTime(y, m, d); return true; }
        catch { return false; }
    }

    // Fast allocation-free prefilter for non-recurring single VEVENTs far outside [from, to],
    // avoiding heavy Ical.Net deserialization on years of historical events.
    private static bool MightIntersect(string ics, DateTime from, DateTime to)
    {
        if (ics.IndexOf("RRULE", StringComparison.OrdinalIgnoreCase) >= 0
            || ics.IndexOf("RDATE", StringComparison.OrdinalIgnoreCase) >= 0
            || ics.IndexOf("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        int firstEvent = ics.IndexOf("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase);
        if (firstEvent < 0) return true;
        if (ics.IndexOf("BEGIN:VEVENT", firstEvent + 12, StringComparison.OrdinalIgnoreCase) >= 0)
            return true; // Multiple VEVENTs in one file: let Ical.Net parse fully.

        if (!TryExtractIsoDate(ics, "\nDTSTART", out var startDate) && !TryExtractIsoDate(ics, "DTSTART", out startDate))
            return true;
        if (startDate > to.Date.AddDays(2)) return false;

        if (TryExtractIsoDate(ics, "\nDTEND", out var endDate) || TryExtractIsoDate(ics, "DTEND", out endDate))
        {
            if (endDate < from.Date.AddDays(-2)) return false;
        }
        else if (ics.IndexOf("DURATION", StringComparison.OrdinalIgnoreCase) < 0 && startDate < from.Date.AddDays(-2))
        {
            return false;
        }
        return true;
    }

    public static List<Entry> Expand(Resource file, DateTime from, DateTime to)
    {
        if (!MightIntersect(file.Ics, from, to)) return [];
        var cal = Calendar.Load(file.Ics) ?? throw new FormatException("日历数据为空");
        var result = new List<Entry>();
        // The search bounds are local wall-clock dates. Ical.Net rejects DateTimeKind.Local
        // without a TZID, which DateTime.Today carries but a constructed month boundary does not.
        var lower = new CalDateTime(DateTime.SpecifyKind(from.AddDays(-1), DateTimeKind.Unspecified));
        var upper = new CalDateTime(DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Unspecified));
        foreach (var occurrence in cal.GetOccurrences(lower).TakeWhileBefore(upper))
        {
            if (occurrence.Source is not CalendarEvent ev || string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var period = occurrence.Period;
            var start = Local(period.StartTime);
            var end = period.EffectiveEndTime is { } finish ? Local(finish) : start;
            if (start >= to || (end <= from && start < from)) continue;
            bool editable = cal.Events.Count == 1 && !ev.Properties.Any(p => p.Name is "RRULE" or "RDATE" or "RECURRENCE-ID")
                && ev.Attendees.Count == 0 && ev.Organizer is null;
            var travel = TimeSpan.Zero;
            var property = ev.Properties.FirstOrDefault(p => p.Name == "X-APPLE-TRAVEL-DURATION");
            if (property?.Value is { } value)
            {
                try { travel = System.Xml.XmlConvert.ToTimeSpan(value.ToString()!); }
                catch (FormatException) { }
                catch (OverflowException) { }
            }
            if (travel < TimeSpan.Zero || travel > TimeSpan.FromDays(1)) travel = TimeSpan.Zero;
            bool holiday = ev.Categories.Any(c => string.Equals(c, "HOLIDAY", StringComparison.OrdinalIgnoreCase));
            string location = ev.Location?.Trim() ?? "";
            string description = ev.Description?.Trim() ?? "";
            string recurrence = DescribeRecurrence(ev);
            result.Add(new(file, ev.Summary ?? "无标题日程", start, end, !period.StartTime.HasTime, editable,
                travel, holiday, location, description, recurrence));
        }
        return result;
    }

    public static string Write(Resource? existing, string title, DateTime start, DateTime end, bool allDay,
        string? location = null, string? description = null)
    {
        var cal = existing is null ? new Calendar() : Calendar.Load(existing.Ics)!;
        var ev = existing is null ? new CalendarEvent { Uid = Guid.NewGuid().ToString("N") } : cal.Events.Single();
        if (existing is null) cal.Events.Add(ev);
        ev.Summary = title;
        ev.DtStart = Stamp(start, allDay);
        if (ev.Duration.HasValue) ev.Duration = null;
        ev.DtEnd = Stamp(end, allDay);
        if (location != null) ev.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        if (description != null) ev.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ev.DtStamp = CalDateTime.UtcNow;
        ev.Sequence++;
        return new CalendarSerializer().SerializeToString(cal)!;
    }
}
