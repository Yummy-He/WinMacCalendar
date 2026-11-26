using System.Windows;
using System.Windows.Media;

namespace WinMacCalendar;

internal static class ThemeResources
{
    private static readonly Uri StylesUri = new("/WinMacCalendar;component/Themes/ModernStyles.xaml", UriKind.Relative);
    private static ResourceDictionary? sharedStyles;
    private static ResourceDictionary? lightPalette;
    private static ResourceDictionary? darkPalette;

    private static ResourceDictionary SharedStyles => sharedStyles ??= new ResourceDictionary { Source = StylesUri };
    private static ResourceDictionary GetPalette(bool dark) =>
        dark ? (darkPalette ??= BuildPalette(true)) : (lightPalette ??= BuildPalette(false));

    public static void Apply(FrameworkElement target, bool dark)
    {
        target.Resources.MergedDictionaries.Clear();
        target.Resources.MergedDictionaries.Add(GetPalette(dark));
        target.Resources.MergedDictionaries.Add(SharedStyles);
        if (target is System.Windows.Controls.Control ctrl)
            ctrl.FontFamily = WidgetFonts.Ui;
    }

    public static ResourceDictionary BuildPalette(bool dark)
    {
        var dict = new ResourceDictionary();
        void Add(string key, string light, string darkHex) => dict[key] = Palette.Color(dark ? darkHex : light);
        void AddAlpha(string key, string light, byte la, string darkHex, byte da) =>
            dict[key] = dark ? Palette.Alpha(darkHex, da) : Palette.Alpha(light, la);

        Add("BgWindowBrush", "#F5F5F7", "#1C1C1E");
        Add("BgSidebarBrush", "#EFEFF4", "#242426");
        Add("BgCardBrush", "#FFFFFF", "#2C2C2E");
        Add("BgCardAltBrush", "#F9F9FB", "#262629");
        Add("BgInputBrush", "#FFFFFF", "#323236");
        Add("BgPopupBrush", "#FFFFFF", "#2C2C2E");
        Add("BgSecondaryBtnBrush", "#FFFFFF", "#3A3A3C");
        Add("BgHoverBrush", "#EAEAEF", "#3A3A3E");
        Add("BgPressedBrush", "#DFDFE6", "#48484C");

        Add("FgPrimaryBrush", "#1C1C1E", "#F5F5F7");
        Add("FgSecondaryBrush", "#48484A", "#D1D1D6");
        Add("FgMutedBrush", "#86868B", "#98989D");

        Add("BorderSubtleBrush", "#E5E5EA", "#3A3A3C");
        Add("BorderControlBrush", "#D1D1D6", "#48484A");
        Add("BorderHoverBrush", "#B0B0B8", "#636366");

        Add("AccentBrush", "#FF3B30", "#FF453A");
        Add("AccentHoverBrush", "#E8342A", "#FF5B52");
        Add("AccentPressedBrush", "#CC2B22", "#E03A30");
        AddAlpha("AccentSoftBrush", "#FF3B30", 28, "#FF453A", 42);

        Add("DangerBrush", "#D92D20", "#FF6961");
        AddAlpha("DangerBgBrush", "#FF3B30", 20, "#FF453A", 32);

        Add("SwitchOffBrush", "#D1D1D6", "#48484A");
        Add("SwitchOnBrush", "#34C759", "#30D158");
        AddAlpha("ScrollThumbBrush", "#000000", 48, "#FFFFFF", 56);

        return dict;
    }

    public static Color WindowCaptionColor(bool dark) =>
        ((SolidColorBrush)Palette.Color(dark ? "#1C1C1E" : "#F5F5F7")).Color;

    public static Brush Brush(FrameworkElement target, string key) =>
        (Brush)(target.TryFindResource(key) ?? Palette.Color("#86868B"));
}
