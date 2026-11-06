using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views.Controls;

internal static class BiliTouchTests
{
    public static void Run(Action<bool, string> check, string artifacts)
    {
        check(UpdateChecker.IsConfigured && UpdateChecker.RepoOwner=="zhongsanZN" && UpdateChecker.RepoName=="BiliTouch", "upstream packages are never offered as BiliTouch updates");
        try { UpdateChecker.CheckAsync().GetAwaiter().GetResult(); check(false, "unconfigured updater rejects checks"); }
        catch (InvalidOperationException) { check(true, "unconfigured updater rejects checks without network"); }

        var filter = FilterService.Instance;
        filter.Clear();
        var videos = new ObservableCollection<VideoItem> {
            new() { OwnerMid=42, Author="小明", Title="测试" },
            new() { OwnerMid=43, Author="小明明", Title="其他" }
        };
        var dynamics = new ObservableCollection<DynamicItem> {
            new() { AuthorMid=42, AuthorName="改名后的昵称", Text="图文动态" },
            new() { AuthorMid=43, AuthorName="小明明", Video=videos[1] }
        };
        filter.Attach(videos); filter.Attach(dynamics);
        filter.BlockUp("小明",42);
        check(CollectionViewSource.GetDefaultView(videos).Cast<object>().Count()==1, "UP block does not hide similar names");
        check(CollectionViewSource.GetDefaultView(dynamics).Cast<object>().Count()==1, "UP block covers native mixed dynamics after rename");
        Svc.Settings.SaveNow(); Svc.Settings.Load(); filter.Reload();
        check(filter.Rules.Count==1, "block rule survives settings reload");
        filter.RemoveRule(filter.Rules[0]);
        check(CollectionViewSource.GetDefaultView(dynamics).Cast<object>().Count()==2, "removing rule restores mixed dynamics");
        filter.BlockTitle("其他");
        check(CollectionViewSource.GetDefaultView(dynamics).Cast<object>().Count()==1, "exact title block covers video dynamics");
        filter.Clear(); Svc.Settings.SaveNow();

        var progress=DownloadProgressService.Instance;
        using(var a=progress.Begin("A"))
        {
            a.Report(25,100);
            using(var b=progress.Begin("B"))
            {
                b.Report(50,100);
                check(progress.Count==2 && progress.Summary.Contains("25% / 50%"), "concurrent progress keeps stable task order");
            }
            check(progress.Count==1, "completed download leaves remaining job visible");
        }
        check(progress.Count==0 && progress.Summary=="", "download disposal clears progress");

        var emoji = new CommentText { Comment=new CommentItem { Content=string.Concat(Enumerable.Repeat("🖐️👍️",20))+"中文𠀀", }, Width=360 };
        emoji.Measure(new Size(360,2000)); emoji.Arrange(new Rect(0,0,360,emoji.DesiredSize.Height));
        var text = new TextRange(emoji.Document.ContentStart,emoji.Document.ContentEnd).Text;
        check(text.Contains("中文𠀀") && text.Contains("🖐️"), "emoji variation selectors and CJK survive rich text layout");
        var runs=emoji.Document.Blocks.OfType<Paragraph>().SelectMany(p=>p.Inlines.OfType<Run>()).ToList();
        check(runs.Any(r=>r.FontFamily.Source=="Segoe UI Symbol") && runs.Any(r=>r.Text.Contains("中文")), "emoji font workaround keeps normal text separate");

        var card = new VideoCard { DataContext=videos[0] };
        card.Measure(new Size(280,300)); card.Arrange(new Rect(0,0,280,300));
        var more=Descendants(card).OfType<Button>().Single(b=>b.Content?.ToString()=="⋯");
        var tap=typeof(TouchSupport).GetMethod("TapCard",BindingFlags.Static|BindingFlags.NonPublic)!;
        check(tap.Invoke(null,new object[]{more})==null, "tapping card action never forwards to video playback");

        var player = new BiliTouch.Views.PlayerWindow();
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var root=new CommentItem();
        root.Replies.Add(new CommentItem { Rpid=1, Ctime=100, LikeCount=10 });
        root.Replies.Add(new CommentItem { Rpid=2, Ctime=300, LikeCount=1 });
        root.Replies.Add(new CommentItem { Rpid=3, Ctime=200, LikeCount=20 });
        typeof(BiliTouch.Views.PlayerWindow).GetField("_detailThreadRoot",flags)!.SetValue(player,root);
        var detail=(FrameworkElement)player.FindName("CommentDetailPanel");
        detail.Visibility=Visibility.Visible;
        var sort=typeof(BiliTouch.Views.PlayerWindow).GetMethod("SetCommentSort",flags)!;
        sort.Invoke(player,new object[]{2});
        check(detail.Visibility==Visibility.Visible && root.Replies.Select(r=>r.Rpid).SequenceEqual(new long[]{2,3,1}), "time sort reorders replies without leaving detail");
        sort.Invoke(player,new object[]{3});
        check(detail.Visibility==Visibility.Visible && root.Replies.Select(r=>r.Rpid).SequenceEqual(new long[]{3,1,2}), "hot sort reorders replies without leaving detail");
        player.Close();

        var dict=new ResourceDictionary { Source=new Uri("/BiliTouch;component/Views/Pages/Settings/SettingsTabs.xaml",UriKind.Relative) };
        var template=(DataTemplate)dict["AboutTabTemplate"];
        foreach(var width in new[]{640,1100})
        {
            var content=(FrameworkElement)template.LoadContent();
            content.DataContext=new SettingsViewModel();
            var host=new Border { Background=(Brush)Application.Current.FindResource("AppBackgroundBrush"), Padding=new Thickness(24), Child=content };
            host.Measure(new Size(width,double.PositiveInfinity));
            host.Arrange(new Rect(0,0,width,host.DesiredSize.Height)); host.UpdateLayout();
            var labels=Descendants(host).OfType<TextBlock>().ToList();
            check(labels.Any(t=>t.Text=="BiliTouch") && labels.Any(t=>t.Text=="作者 · 中三") && labels.Any(t=>t.Text=="ABing"), $"about {width}px retains both authors in correct roles");
            var columns=(Grid)content;
            var left=(FrameworkElement)columns.Children[0]; var right=(FrameworkElement)columns.Children[1];
            check(left.TranslatePoint(new Point(left.ActualWidth,0),columns).X <= right.TranslatePoint(new Point(),columns).X, $"about {width}px columns never overlap");
            var bitmap=new RenderTargetBitmap(width,(int)Math.Ceiling(host.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(host); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(artifacts,$"bilitouch-about-{width}.png")); encoder.Save(file);
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var d in Descendants(child)) yield return d; }
    }
}
