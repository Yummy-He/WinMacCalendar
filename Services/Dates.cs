using System.Globalization;

namespace WinMacCalendar;

public static class Dates
{
    private static readonly ChineseLunisolarCalendar Lunar = new();
    private static readonly string[] Months = ["正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊"];
    private const string Digits = "一二三四五六七八九十";
    public static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    public static (string Month, string Day) Parts(DateTime date)
    {
        if (date < Lunar.MinSupportedDateTime || date > Lunar.MaxSupportedDateTime) return ("", "");
        int month = Lunar.GetMonth(date), day = Lunar.GetDayOfMonth(date);
        int leap = Lunar.GetLeapMonth(Lunar.GetYear(date));
        string name = (month == leap ? "闰" : "") + Months[month - 1 - (leap > 0 && month >= leap ? 1 : 0)] + "月";
        string number = day switch
        {
            10 => "初十",
            20 => "二十",
            30 => "三十",
            _ => (day < 10 ? "初" : day < 20 ? "十" : "廿") + Digits[(day - 1) % 10]
        };
        return (name, number);
    }
    public static string LunarDate(DateTime date) { var (m, d) = Parts(date); return m + d; }
    public static string Weekday(DateTime date) => "周" + "日一二三四五六"[(int)date.DayOfWeek];
    public static DateTime GridStart(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        return first.AddDays(-((int)first.DayOfWeek + 6) % 7);
    }
}
