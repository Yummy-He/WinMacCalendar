using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinMacCalendar;

internal static partial class Program
{
    private static void CheckTimelineLayers(string? preview)
    {
        var start = Day.AddDays(1).AddHours(8).AddMinutes(55);
        var lesson = new Entry(new Resource("", ""), "架构评审例会", start, start.AddMinutes(95),
            false, true, TimeSpan.FromMinutes(15)) { Source = new("work","工作","#161616",true) };
        foreach (var variant in new[] { Variant.ShortTimeline, Variant.Week })
        foreach (bool dark in new[] { false, true })
        {
            Entry? selected = null;
            var card = new CalendarCard(variant, Day, Day.AddHours(8), [lesson], _=>{}, e=>selected=e, _=>{}, ()=>{}, dark).Root;
            var size = CalendarCard.Sizes[(int)variant];
            card.Measure(size);
            card.Arrange(new Rect(size));
            card.UpdateLayout();
            var block = Tree(card).OfType<Button>().Single(b=>AutomationProperties.GetName(b).Contains(lesson.Title));
            var plot = (Canvas)VisualTreeHelper.GetParent(block);
            var ticks = plot.Children.OfType<Border>().Select(Canvas.GetTop).ToList();
            double hour = ticks[1] - ticks[0];
            double top = Canvas.GetTop(block);
            Check(Math.Abs(top - (ticks[0] + hour * 40/60)) < .01, "08:40 行程起点");
            var rail = Tree(block).OfType<Border>().Single(b=>b.Width==2.5);
            var railOrigin = rail.TranslatePoint(new Point(), plot);
            Check(Math.Abs(railOrigin.Y - (ticks[0] + hour * 55/60)) < .01, "08:55 实线起点");
            foreach (var fraction in new[] { .1, .6 })
            {
                var point = block.TranslatePoint(new Point(block.Width/2, block.Height*fraction), plot);
                var hit = VisualTreeHelper.HitTest(plot, point)?.VisualHit;
                while (hit!=null && hit is not Button) hit=VisualTreeHelper.GetParent(hit);
                Check(hit==block, "行程段和日程段共用点击区域");
            }
            block.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(selected==lesson, "打开所属日程");

            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width*scale), (int)Math.Ceiling(size.Height*scale),
                    96*scale, 96*scale, PixelFormats.Pbgra32);
                image.Render(card);
                var pixels = new byte[image.PixelWidth*image.PixelHeight*4];
                image.CopyPixels(pixels, image.PixelWidth*4, 0);
                var origin = plot.TranslatePoint(new Point(), card);
                int Red(double x, double y, int offset = 0)
                {
                    int px = (int)Math.Floor((origin.X+x)*scale);
                    int py = (int)Math.Floor((origin.Y+y)*scale) + offset;
                    return pixels[(py*image.PixelWidth+px)*4+2];
                }
                double stripe = railOrigin.X + rail.ActualWidth/2;
                double blank = Canvas.GetLeft(block) + block.Width - 9;
                foreach (double tick in ticks.Skip(1).Take(2))
                {
                    int plain = Red(blank,tick-3);
                    bool visible = false;
                    for (int dy=-1; dy<=2; dy++)
                    {
                        Check(Red(stripe,tick,dy)==Red(stripe,tick-3), "小时线不能覆盖事件色条");
                        int contrast = Red(blank,tick,dy)-plain;
                        visible |= dark ? contrast>=2 : contrast<=-2;
                    }
                    Check(visible, "小时线透过事件底色可见");
                }
                if (preview!=null && !dark && scale==2 && variant==Variant.ShortTimeline)
                {
                    var crop = new Int32Rect((int)((origin.X-32)*scale),(int)((origin.Y+top-10)*scale),
                        (int)((block.Width+32)*scale),(int)((block.Height+20)*scale));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(image,crop)));
                    using var output = File.Create(preview);
                    encoder.Save(output);
                }
            }
        }
        var midnight = Day.AddDays(1);
        var overnight = lesson with { Start = midnight.AddMinutes(5), End = midnight.AddHours(1) };
        var late = new CalendarCard(Variant.ShortTimeline, Day, Day.AddHours(23), [overnight],
            _=>{}, _=>{}, _=>{}, ()=>{}, false).Root;
        var lateSize = CalendarCard.Sizes[(int)Variant.ShortTimeline];
        late.Measure(lateSize);
        late.Arrange(new Rect(lateSize));
        late.UpdateLayout();
        var segments = Tree(late).OfType<Button>().Where(b => AutomationProperties.GetName(b).Contains(lesson.Title)).ToList();
        Check(segments.Count==2, "跨午夜的行程在前一天也可见");
        Check(!Tree(segments[0]).OfType<TextBlock>().Any(t=>t.Text==lesson.Title) &&
            Tree(segments[1]).OfType<TextBlock>().Any(t=>t.Text==lesson.Title), "日程标题从实际开始时间显示");
    }
}
