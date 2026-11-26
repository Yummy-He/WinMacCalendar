using System.Security.Cryptography;
using System.Text;

namespace WinMacCalendar;

public sealed record CalendarSource(string Scope, string Name, string Accent, bool Writable, CloudCalendar? Remote = null)
{
    public static readonly CalendarSource Local = new(CalendarStore.Local, "本地日历", "#278AD1", true);
    private static readonly string[] Fallback = ["#4679CD","#AE53AA","#198D78","#D76B30","#7763CF","#C64C63","#498B43","#337B98"];

    public static string ColorOf(CloudCalendar calendar)
    {
        string? hex = calendar.Color?.Trim();
        // Apple's eight-digit form is RRGGBBAA; WPF expects AARRGGBB.
        if (hex is { Length: 7 or 9 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit))
            return hex[..7].ToUpperInvariant();
        byte index = SHA256.HashData(Encoding.UTF8.GetBytes(calendar.Url))[0];
        return Fallback[index % Fallback.Length];
    }

    public static CalendarSource From(Account account, CloudCalendar calendar) =>
        new(account.ScopeFor(calendar), calendar.Name, ColorOf(calendar), calendar.Writable, calendar);
    public override string ToString() => Name;
}
