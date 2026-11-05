using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.ViewModels;
namespace BiliDesk.Views.Pages;
public partial class UserRelationsPage : UserControl
{
 internal UserRelationsViewModel ViewModel {get;}
 public UserRelationsPage(long mid,bool followers)
 {InitializeComponent();ViewModel=new(mid,followers);DataContext=ViewModel;Loaded+=async(_,_)=>{if(ViewModel.Users.Count==0)await ViewModel.LoadAsync(true);};ContactSelection.Install(PeopleList,Open);}
 private void Open(object item){if(item is FollowUser user)Svc.Navigate?.OpenUserSpace(user.Mid.ToString());}
 private void OnUser(object sender,MouseButtonEventArgs e){if(e.ChangedButton==MouseButton.Left && sender is FrameworkElement f && f.DataContext is {} item)Open(item);}
 private async void OnRetry(object sender,RoutedEventArgs e)=>await ViewModel.LoadAsync(true);
 private void OnScroll(object sender,ScrollChangedEventArgs e){if(e.VerticalChange>0 && e.OriginalSource is ScrollViewer scroll && scroll.ScrollableHeight-scroll.VerticalOffset<240)_=ViewModel.LoadAsync();}
}
