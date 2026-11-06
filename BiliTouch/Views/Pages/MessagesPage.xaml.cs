using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
namespace BiliTouch.Views.Pages;
public partial class MessagesPage : UserControl
{
    internal TabNavigationGestures ModuleGestures {get;}
 private MessagesViewModel? Vm=>DataContext as MessagesViewModel;
 private bool _scrollScheduled;
 public MessagesPage()
 {
  InitializeComponent();
        ModuleGestures = new TabNavigationGestures(this,ModuleContent,ModuleTabs);
  ContactSelection.Install(SessionScroll,item=>{if(item is MsgSession session)_=OpenSessionAsync(session);});
  DataContextChanged+=(_,e)=>{if(e.OldValue is MessagesViewModel old)old.Messages.CollectionChanged-=OnMessagesChanged;if(e.NewValue is MessagesViewModel vm)vm.Messages.CollectionChanged+=OnMessagesChanged;};
  Loaded+=async(_,_)=>{if(Vm!=null && Vm.Sessions.Count==0)await Vm.LoadAsync();};
 }
 private void OnMessagesChanged(object? sender,NotifyCollectionChangedEventArgs e){if(Vm?.LoadingOlder!=true)ScrollToBottom();}
 private void OnSessionClick(object sender,MouseButtonEventArgs e){if(e.ChangedButton==MouseButton.Left && sender is FrameworkElement {DataContext:MsgSession session})_=OpenSessionAsync(session);}
 private async Task OpenSessionAsync(MsgSession session){if(Vm==null)return;await Vm.LoadThreadAsync(session);ScrollToBottom();}
 private void OnDraftKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter && Keyboard.Modifiers!=ModifierKeys.Shift){e.Handled=true;if(Vm?.SendCommand.CanExecute(null)==true)Vm.SendCommand.Execute(null);}}
 private void OnMessageImageClick(object sender,MouseButtonEventArgs e){e.Handled=true;if(sender is FrameworkElement {DataContext:MsgItem m} && m.ImageUrl.Length>0)MediaActions.ShowImage(m.ImageUrl,"私信图片",Window.GetWindow(this));}
 private void OnMessageCardClick(object sender,MouseButtonEventArgs e){e.Handled=true;if(sender is FrameworkElement {DataContext:MsgItem m})OpenCard(m);}
 private void OnMessageCardOpen(object sender,RoutedEventArgs e){if(sender is FrameworkElement {DataContext:MsgItem m})OpenCard(m);}
 private void OpenCard(MsgItem m){if(m.Bvid.Length>0)Svc.Navigate?.OpenVideoPlayer(m.Bvid);else if(m.Aid>0)OpenLink("https://www.bilibili.com/video/av"+m.Aid);else OpenLink(m.TargetUrl);}
 private void OnMessageAction(object sender,RoutedEventArgs e){if(sender is Button {Tag:string url})OpenLink(url);}
 private void OnNotificationUser(object sender,MouseButtonEventArgs e){if(sender is FrameworkElement{DataContext:MessageNotification n} && n.UserId>0)Svc.Navigate?.OpenUserSpace(n.UserId.ToString());}
 private void OnNotificationOpen(object sender,RoutedEventArgs e){if(sender is FrameworkElement{DataContext:MessageNotification n}){if(n.Url.Length>0)OpenLink(n.Url);else if(n.UserId>0)Svc.Navigate?.OpenUserSpace(n.UserId.ToString());}}
 private static void OpenLink(string url)
 {
  if(!Uri.TryCreate(url,UriKind.Absolute,out var uri))return;
  if(uri.Scheme=="bilibili"){Svc.Toast.Show("请在哔哩哔哩官方客户端查看此通知入口");return;}
  if(uri.Scheme!="https" && uri.Scheme!="http")return;
  var bvid=BiliTouch.Utils.UrlParser.ExtractBvid(url);
  if(!string.IsNullOrEmpty(bvid)){Svc.Navigate?.OpenVideoPlayer(bvid);return;}
  Svc.Navigate?.OpenWebViewWindow(url,"私信链接");
 }
 private async void OnThreadScroll(object sender,ScrollChangedEventArgs e)
 {
  if(!ReferenceEquals(e.OriginalSource,MsgScroll) || e.VerticalChange>=0 || MsgScroll.VerticalOffset>120 || Vm==null)return;
  var talker=Vm.Current?.TalkerId;var height=MsgScroll.ExtentHeight;var offset=MsgScroll.VerticalOffset;
  await Vm.LoadOlderAsync();
  if(Vm.Current?.TalkerId!=talker || MsgScroll.VerticalOffset>120)return;
  MsgScroll.UpdateLayout();MsgScroll.ScrollToVerticalOffset(offset+Math.Max(0,MsgScroll.ExtentHeight-height));
 }
 private void ScrollToBottom()
 {
  if(_scrollScheduled)return;_scrollScheduled=true;
  Dispatcher.BeginInvoke(new Action(()=>{_scrollScheduled=false;if(Vm?.LoadingOlder!=true)MsgScroll.ScrollToEnd();}),System.Windows.Threading.DispatcherPriority.Loaded);
 }
}
