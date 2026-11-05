using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class MineOverviewPage : UserControl
{
    internal PersonalHomeViewModel ViewModel { get; }=new();
    public MineOverviewPage()
    {
        InitializeComponent();DataContext=ViewModel;
        Loaded+=OnLoaded;Unloaded+=(_,_)=>Svc.Session.Changed-=OnSessionChanged;
        ModuleGestures.Install(this,()=>ViewModel.RefreshAsync());
    }
    private void OnFollowing(object sender,RoutedEventArgs e)=>Svc.Navigate?.OpenRelations(ViewModel.Mid,false);
    private void OnFollowers(object sender,RoutedEventArgs e)=>Svc.Navigate?.OpenRelations(ViewModel.Mid,true);
    private void OnCreations(object sender,RoutedEventArgs e)=>CreationsTitle.BringIntoView();
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
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
