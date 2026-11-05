using System.Windows;
using System.Windows.Controls;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views;

namespace BiliDesk.Helpers;

public static class VideoDownloader
{
    public static async Task<string?> DownloadAsync(string bvid,string? title,Window? owner=null,long preferredCid=0)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(bvid))throw new InvalidOperationException("缺少视频信息");
            var(ok,err,video)=await Svc.Api.GetVideoAsync(bvid);
            if(!ok || video==null)throw new InvalidOperationException(err??"获取视频资料失败");
            video.Bvid=bvid;
            var pages=video.Pages.Count>0?video.Pages:new List<VideoPage>{new(){Cid=video.Cid,Page=1,Part=video.Title}};
            var selection=Select(video,pages,owner,preferredCid);
            if(selection==null)return null;
            OfflineDownloadService.Instance.Enqueue(video,selection.Value.pages,selection.Value.quality);
            Svc.Toast.Show("已加入离线缓存，可在「我的 → 缓存」查看进度");
            return OfflineDownloadService.Instance.Items.FirstOrDefault(e=>e.Bvid==bvid && e.Cid==selection.Value.pages[0].Cid && e.RequestedQuality==selection.Value.quality && e.State==OfflineState.Completed)?.FilePath;
        }
        catch(Exception ex){Svc.Toast.Show("无法添加缓存："+ex.Message);return null;}
    }
    internal static (List<VideoPage> pages,int quality)? Select(VideoDetail video,List<VideoPage> pages,Window? owner,long preferredCid)
    {
        var window=new FluentWindow { Title="缓存视频",Width=540,Height=560,MinWidth=400,MinHeight=360,Owner=owner,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner };
        window.SetResourceReference(Window.BackgroundProperty,"AppBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
        var grid=new Grid {Margin=new Thickness(24,44,24,24)};
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var title=new TextBlock {Text=video.Title,FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,18)};title.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");grid.Children.Add(title);
        var options=new StackPanel {Margin=new Thickness(0,0,0,12)};Grid.SetRow(options,1);grid.Children.Add(options);
        options.Children.Add(new TextBlock {Text="缓存画质（不可用时按视频权限降级）",Margin=new Thickness(0,0,0,8)});
        var quality=new ComboBox {ItemsSource=new[]{"720P","480P","360P"},SelectedIndex=0,MinHeight=40};options.Children.Add(quality);
        var all=new CheckBox {Content="全选分P",MinHeight=40};options.Children.Add(all);
        var list=new ListBox {HorizontalContentAlignment=HorizontalAlignment.Stretch};list.SetResourceReference(Control.BackgroundProperty,"CardBackgroundBrush");list.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);Grid.SetRow(list,2);grid.Children.Add(list);
        var selectedCid=pages.Any(p=>p.Cid==preferredCid)?preferredCid:pages[0].Cid;
        var checks=pages.Select(p=>new CheckBox {Content=new TextBlock {Text=$"P{p.Page}  {p.DisplayTitle}  {p.Duration}",TextWrapping=TextWrapping.Wrap},Tag=p,IsChecked=p.Cid==selectedCid,MinHeight=44}).ToList();
        foreach(var check in checks)list.Items.Add(check);
        all.Checked+=(_,_)=>{foreach(var check in checks)check.IsChecked=true;};all.Unchecked+=(_,_)=>{foreach(var check in checks)check.IsChecked=false;};
        var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};Grid.SetRow(footer,3);grid.Children.Add(footer);
        var cancel=new Button {Content="取消",MinHeight=44,MinWidth=88,IsCancel=true,Margin=new Thickness(0,0,12,0)};cancel.SetResourceReference(FrameworkElement.StyleProperty,"SubtleButtonStyle");cancel.Click+=(_,_)=>window.DialogResult=false;footer.Children.Add(cancel);
        var add=new Button {Content="开始缓存",MinHeight=44,MinWidth=112};add.SetResourceReference(FrameworkElement.StyleProperty,"AccentButtonStyle");footer.Children.Add(add);
        List<VideoPage>? selected=null;
        add.Click+=(_,_)=>{selected=checks.Where(c=>c.IsChecked==true).Select(c=>(VideoPage)c.Tag).ToList();if(selected.Count>0)window.DialogResult=true;};
        window.Content=grid;
        return window.ShowDialog()==true && selected!=null?(selected,quality.SelectedIndex switch {1=>32,2=>16,_=>64}):null;
    }
}
