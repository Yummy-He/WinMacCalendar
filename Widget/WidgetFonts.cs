using System.IO;
using System.Windows.Media;

namespace WinMacCalendar;

internal static class WidgetFonts
{
    private static readonly Uri? Folder = FindFolder();
    public static readonly FontFamily Display = Family("SF Pro Display");
    public static readonly FontFamily Text = Family("SF Pro Text");
    public static readonly FontFamily Ui = Family("SF Pro Text");

    private static Uri? FindFolder()
    {
        var appFonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (Directory.Exists(appFonts)) return new Uri(appFonts + Path.DirectorySeparatorChar);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "WinMacCalendar.csproj"))) continue;
            var slim = Path.Combine(dir.FullName, "Resources", "Fonts");
            if (Directory.Exists(slim)) return new Uri(slim + Path.DirectorySeparatorChar);
            var samples = Path.Combine(dir.FullName, "test", "fonts");
            if (Directory.Exists(samples)) return new Uri(samples + Path.DirectorySeparatorChar);
            break;
        }
        return null;
    }

    private static FontFamily Family(string latin)
    {
        string installed = latin + ", PingFang SC, Segoe UI Variable, Segoe UI, Microsoft YaHei UI";
        return Folder == null
            ? new FontFamily(installed)
            : new FontFamily(Folder, "./#" + latin + ", ./#PingFang SC, " + installed);
    }
}
