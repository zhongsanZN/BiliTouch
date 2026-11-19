using System.Windows;
using System.Windows.Controls;
using BiliTouch.Helpers;
using BiliTouch.Services;
using BiliTouch.ViewModels;

namespace BiliTouch.Views.Pages;

public partial class MineOverviewPage : UserControl
{
    internal PersonalHomeViewModel ViewModel { get; }=new();
    public MineOverviewPage()
    {
        InitializeComponent();DataContext=ViewModel;
        Loaded+=OnLoaded;Unloaded+=(_,_)=>{Svc.Session.Changed-=OnSessionChanged;Responsive.PostureChanged-=OnOverviewPostureChanged;};
        ModuleGestures.Install(this,()=>ViewModel.RefreshAsync());
    }
    private void OnOverviewPostureChanged(FrameworkElement _,LayoutPosture posture)=>ApplyTabletLayout(posture);
    internal void ApplyTabletLayout(LayoutPosture posture)
    {
        var tablet=posture==LayoutPosture.TabletPortrait;
        if(OverviewStack!=null)OverviewStack.Margin=tablet?new Thickness(16,8,16,20):new Thickness(36,8,36,28);
        if(ProfileCardGrid!=null)ProfileCardGrid.Margin=tablet?new Thickness(16):new Thickness(26);
        if(ProfileAvatarBox!=null){ProfileAvatarBox.Width=ProfileAvatarBox.Height=tablet?64:88;ProfileAvatarBox.CornerRadius=new CornerRadius(tablet?32:44);}
        if(ProfileAvatarInner!=null)ProfileAvatarInner.CornerRadius=new CornerRadius(tablet?32:44);
        if(ProfileInfoStack!=null)ProfileInfoStack.Margin=tablet?new Thickness(14,0,0,0):new Thickness(22,0,0,0);
        if(ProfileNameText!=null)ProfileNameText.FontSize=tablet?20:26;
    }
    private void OnFollowing(object sender,RoutedEventArgs e)=>Svc.Navigate?.OpenRelations(ViewModel.Mid,false);
    private void OnFollowers(object sender,RoutedEventArgs e)=>Svc.Navigate?.OpenRelations(ViewModel.Mid,true);
    private void OnCreations(object sender,RoutedEventArgs e)=>CreationsTitle.BringIntoView();
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        Responsive.PostureChanged-=OnOverviewPostureChanged;Responsive.PostureChanged+=OnOverviewPostureChanged;
        ApplyTabletLayout(Responsive.GetPosture(this));
        Svc.Session.Changed-=OnSessionChanged;Svc.Session.Changed+=OnSessionChanged;
        await ViewModel.RefreshAsync();
    }
    private void OnSessionChanged()=>Dispatcher.InvokeAsync(async()=>await ViewModel.RefreshAsync());
    private void OnScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(e.VerticalChange>0 && ProfileScroll.ScrollableHeight-ProfileScroll.VerticalOffset<240)
            _=ViewModel.LoadMoreAsync();
    }
}
