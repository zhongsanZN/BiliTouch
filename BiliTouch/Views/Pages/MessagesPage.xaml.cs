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
 private Window? _messageHost;
 public MessagesPage()
 {
  InitializeComponent();
        ModuleGestures = new TabNavigationGestures(this,ModuleContent,ModuleTabs,CreatePeerPreview);
        Helpers.ModuleGestures.Install(this,()=>Vm?.LoadAsync()??Task.CompletedTask,hintHost:ModuleContent,canPull:sv=>sv!=null && !ReferenceEquals(sv,MsgScroll));
  ContactSelection.Install(SessionScroll,item=>{if(item is MsgSession session)_=OpenSessionAsync(session);});
  DataContextChanged+=(_,e)=>{if(e.OldValue is MessagesViewModel old){old.Messages.CollectionChanged-=OnMessagesChanged;old.ChannelCacheUpdated-=OnChannelCacheUpdated;}if(e.NewValue is MessagesViewModel vm){vm.Messages.CollectionChanged+=OnMessagesChanged;vm.ChannelCacheUpdated+=OnChannelCacheUpdated;}ModuleGestures.InvalidatePeers();};
  Loaded+=async(_,_)=>{Svc.MessageUnread.PropertyChanged+=OnUnreadChanged;_messageHost=Window.GetWindow(this);if(_messageHost!=null)_messageHost.Activated+=OnMessageHostActivated;if(Vm!=null)await Vm.EnsureLoadedAsync();if(Svc.MessageUnread.IsStarted)await Svc.MessageUnread.RefreshAsync(true);};
  Unloaded+=(_,_)=>{Svc.MessageUnread.PropertyChanged-=OnUnreadChanged;if(_messageHost!=null)_messageHost.Activated-=OnMessageHostActivated;_messageHost=null;};
 }

 private void OnChannelCacheUpdated() => Dispatcher.BeginInvoke(new Action(() => ModuleGestures.InvalidatePeers()));

 private UIElement CreatePeerPreview(int targetIndex)
 {
  var root = new Grid { Background = TryFindResource("AppBackgroundBrush") as System.Windows.Media.Brush };
  var vm = Vm;
  if (targetIndex == 0)
  {
   root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(252) });
   root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
   var leftStack = new StackPanel { Margin = new Thickness(20, 4, 8, 20) };
   if (vm != null && vm.Sessions.Count > 0)
   {
    foreach (var session in vm.Sessions.Take(12))
    {
     var avatar = new Border
     {
      Width = 36,
      Height = 36,
      CornerRadius = new CornerRadius(18),
      VerticalAlignment = VerticalAlignment.Center
     };
     if (!string.IsNullOrEmpty(session.Face)) Cover.SetSource(avatar, session.Face);

     var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
     info.Children.Add(new TextBlock
     {
      Text = session.DisplayName,
      FontSize = 13,
      FontWeight = FontWeights.SemiBold,
      TextTrimming = TextTrimming.CharacterEllipsis,
      Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
     });
     if (!string.IsNullOrEmpty(session.Preview))
     {
      info.Children.Add(new TextBlock
      {
       Text = session.Preview,
       FontSize = 11.5,
       Margin = new Thickness(0, 2, 0, 0),
       TextTrimming = TextTrimming.CharacterEllipsis,
       Foreground = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush
      });
     }

     var rowGrid = new Grid();
     rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
     rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
     Grid.SetColumn(avatar, 0);
     Grid.SetColumn(info, 1);
     rowGrid.Children.Add(avatar);
     rowGrid.Children.Add(info);

     leftStack.Children.Add(new Border
     {
      CornerRadius = new CornerRadius(10),
      Padding = new Thickness(10, 8, 10, 8),
      Margin = new Thickness(0, 0, 0, 4),
      Background = TryFindResource(session.IsSelected ? "CardBackgroundBrush" : "AppBackgroundBrush") as System.Windows.Media.Brush,
      Child = rowGrid
     });
    }
   }
   else
   {
    leftStack.Children.Add(new TextBlock
    {
     Text = Svc.Session.HasLogin ? "私信会话" : "查看私信需要先登录 B 站账号",
     Margin = new Thickness(8, 12, 8, 0),
     Foreground = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush,
     FontSize = 13,
     TextWrapping = TextWrapping.Wrap
    });
   }
   Grid.SetColumn(leftStack, 0);
   root.Children.Add(leftStack);

   var rightPanel = new StackPanel { Margin = new Thickness(12, 4, 24, 20) };
   rightPanel.Children.Add(new TextBlock
   {
    Text = vm?.CurrentTitle ?? "私信",
    FontSize = 15,
    FontWeight = FontWeights.SemiBold,
    Margin = new Thickness(8, 0, 8, 10),
    Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
   });
   if (vm != null && vm.Messages.Count > 0)
   {
    foreach (var msg in vm.Messages.TakeLast(6))
    {
     var bubble = new Border
     {
      CornerRadius = new CornerRadius(10),
      Padding = new Thickness(12),
      Margin = new Thickness(8, 0, 8, 10),
      HorizontalAlignment = msg.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
      Background = TryFindResource(msg.IsMine ? "AccentSoftFillBrush" : "CardBackgroundBrush") as System.Windows.Media.Brush,
      Child = new TextBlock
      {
       Text = msg.Text.Length > 0 ? msg.Text : msg.Title,
       FontSize = 13,
       TextWrapping = TextWrapping.Wrap,
       Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
      }
     };
     rightPanel.Children.Add(bubble);
    }
   }
   Grid.SetColumn(rightPanel, 1);
   root.Children.Add(rightPanel);
   return root;
  }

  var cached = vm?.TryGetCachedNotifications(targetIndex);
  if (cached != null)
  {
   if (cached.Count == 0)
   {
    root.Children.Add(new TextBlock
    {
     Text = "暂时没有这类消息",
     HorizontalAlignment = HorizontalAlignment.Center,
     VerticalAlignment = VerticalAlignment.Center,
     FontSize = 13,
     Foreground = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
    });
    return root;
   }
   var stack = new StackPanel { Margin = new Thickness(24, 8, 24, 20) };
   foreach (var item in cached.Take(8))
   {
    var row = new Grid();
    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    var avatar = new Border
    {
     Width = 40,
     Height = 40,
     CornerRadius = new CornerRadius(20),
     VerticalAlignment = VerticalAlignment.Top
    };
    if (!string.IsNullOrEmpty(item.Face)) Cover.SetSource(avatar, item.Face);
    Grid.SetColumn(avatar, 0);
    row.Children.Add(avatar);

    var textStack = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
    textStack.Children.Add(new TextBlock
    {
     Text = item.Title,
     FontSize = 14,
     FontWeight = FontWeights.SemiBold,
     TextWrapping = TextWrapping.Wrap,
     Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
    });
    textStack.Children.Add(new TextBlock
    {
     Text = item.TimeText,
     FontSize = 11,
     Margin = new Thickness(0, 5, 0, 8),
     Foreground = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush
    });
    if (!string.IsNullOrEmpty(item.Text))
    {
     textStack.Children.Add(new TextBlock
     {
      Text = item.Text,
      TextWrapping = TextWrapping.Wrap,
      Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
     });
    }
    if (!string.IsNullOrEmpty(item.TargetTitle))
    {
     textStack.Children.Add(new TextBlock
     {
      Text = item.TargetTitle,
      TextWrapping = TextWrapping.Wrap,
      Margin = new Thickness(0, 8, 0, 0),
      Foreground = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
     });
    }
    Grid.SetColumn(textStack, 1);
    row.Children.Add(textStack);

    stack.Children.Add(new Border
    {
     Background = TryFindResource("CardBackgroundBrush") as System.Windows.Media.Brush,
     BorderBrush = TryFindResource("CardBorderBrush") as System.Windows.Media.Brush,
     BorderThickness = new Thickness(1),
     CornerRadius = new CornerRadius(12),
     Padding = new Thickness(18),
     Margin = new Thickness(0, 0, 0, 12),
     Child = row
    });
   }
   root.Children.Add(stack);
   return root;
  }

  var channelTitle = vm != null && targetIndex >= 0 && targetIndex < vm.Channels.Length
   ? vm.Channels[targetIndex] : "消息";
  var pending = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
  pending.Children.Add(new BiliTouch.Views.Controls.LoadingRing { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center });
  pending.Children.Add(new TextBlock
  {
   Text = Svc.Session.HasLogin ? $"松手切换到「{channelTitle}」" : "查看消息需要先登录 B 站账号",
   Margin = new Thickness(0, 10, 0, 0),
   FontSize = 13,
   Foreground = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
  });
  root.Children.Add(pending);
  return root;
 }
 private async void OnMessageHostActivated(object? sender,EventArgs e)
 {if(IsVisible && Vm is {Loading:false,ThreadLoading:false,NotificationLoading:false} vm)await vm.EnsureLoadedAsync();}
 private async void OnUnreadChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
 {if(e.PropertyName==nameof(MessageUnreadService.Total) && IsVisible && _messageHost?.IsActive==true && Vm is {Loading:false,ThreadLoading:false,NotificationLoading:false} vm)await vm.EnsureLoadedAsync();}
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
