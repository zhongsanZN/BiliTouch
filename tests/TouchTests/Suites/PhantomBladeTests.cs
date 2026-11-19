using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.Models;
using BiliTouch.Services;
using BiliTouch.ViewModels;
using BiliTouch.Views;
using BiliTouch.Views.Pages;
using BiliTouch.Views.Controls;

internal static partial class StarPartyTests
{
 public static void RunPhantom(Action<bool,string> check,string artifacts)
 {
  var oldHttp=typeof(ApiClient).GetField("_http",Hidden)!.GetValue(Svc.Api);
  var skin=Svc.Settings.SkinId;var mode=Svc.Theme.Mode;var accent=Svc.Settings.AccentColor;
  var preference=Svc.Settings.PreferredAudioQuality;var context=SynchronizationContext.Current;
  var effects=Svc.Settings.SkinEffects;var useAccent=Svc.Settings.SkinUseAccent;
  var oldScale=Svc.Settings.UiScalePercent;
  var mute=App.MuteForThisRun;
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
  using var http=new HttpClient(new Fixture());typeof(ApiClient).GetField("_http",Hidden)!.SetValue(Svc.Api,http);
  var windows=new List<Window>();
  try {
   check(ApiClient.QnToLabel(126)=="杜比视界" && ApiClient.QnToLabel(127)=="8K","video quality labels distinguish Vision, 8K and audio");
   var vm=new SettingsViewModel();vm.SkinSelected=vm.SkinOptions[2];
   vm.SkinEffects=true;
   check(ThemeAudio.Enabled && vm.SkinEffectsDescription.Contains("低音量武器升级音效"),"Phantom Blade effects include the sound pack through the existing enabled-by-default toggle");
   using(var audio=Application.GetResourceStream(new Uri("pack://application:,,,/BiliTouch;component/Assets/Audio/PhantomBladeZero/like.wav"))!.Stream)
   using(var sound=new System.Media.SoundPlayer(audio)) { sound.Load();check(sound.IsLoadCompleted,"the embedded quiet PCM is a valid SoundPlayer WAV"); }
   typeof(App).GetProperty(nameof(App.MuteForThisRun))!.SetValue(null,true);
   check(!ThemeAudio.PlayLike(),"silent diagnostics suppress theme sounds without changing saved settings");
   check(SkinService.Instance.IsPhantomBlade && !SkinService.Instance.IsStarParty,"new skin has an independent identity");
   var settingsPage=new SettingsPage{DataContext=vm};var settingsWindow=new Window{Title="BiliTouch 0.2.5 · 装扮入口核验",Width=1050,Height=850,Content=settingsPage,ShowActivated=false};windows.Add(settingsWindow);settingsWindow.Show();Pump(160);
   var picker=Descendants<ComboBox>(settingsPage).Single(c=>ReferenceEquals(c.ItemsSource,vm.SkinOptions));
   check(picker.Items.Count==3 && picker.Items.Contains("影之刃零 · 影境") && (string)picker.SelectedItem=="影之刃零 · 影境","the actual settings picker visibly exposes and selects Phantom Blade alongside both other themes");
   Save(settingsWindow,Path.Combine(artifacts,"phantom-settings-0.2.5.png"));settingsWindow.Close();
   var snapshot=(AppSettings)Call(Svc.Settings,"Snapshot")!;Svc.Settings.SkinId="";Call(Svc.Settings,"ApplySanitized",snapshot);
   check(SkinService.Instance.IsPhantomBlade,"new decoration survives settings normalization");
   foreach(var name in new[]{"head_myself_bg","brush","paper","divider","weave","foil","memory_flower","jade","play","like_off","like_on"})check(SkinService.Instance.Image(name).IsFrozen,"loaded original artwork: "+name);
   var main=new MainWindow{Width=1320,Height=820};windows.Add(main);main.Show();Pump(250);
   var home=Descendants<HomePage>(main).First();var homeVm=(HomeViewModel)home.DataContext;
   typeof(HomeViewModel).GetProperty("Error")!.SetValue(homeVm,"");
   var research=Path.GetFullPath(Path.Combine(artifacts,"../../../theme-research/phantom-blade-zero"));
   Directory.CreateDirectory(AppPaths.CacheDir);
   var titles=new[]{"影之刃零 · 江湖夜雨","竹林深处的光与影","雪中寺院 · 场景巡礼","落日下的影境江湖","山门之内 · 战斗实机","剑客与刀光","冷月映照的古街","以身证道，以命寻心"};
   var pictures=new[]{4,5,6,8,7,3,9,2};
   for(int i=0;i<titles.Length;i++) {
    var url=$"https://example.invalid/pbz-cover-{i}.jpg";
    File.Copy(Path.Combine(research,$"assets/media/screenshots/{pictures[i]:000}.jpg"),Path.Combine(AppPaths.CacheDir,Hashing.Md5Hex(url)+".jpg"),true);
    homeVm.RecommendItems.Add(new VideoItem{Bvid="BVpreview"+i,Title=titles[i],Cover=url,Author="装扮预览 · 示例内容",Duration="03:42",ViewCount=12000+i*300});
   }
   Pump(500);
   var cardPlayIcons=Descendants<SkinActionIcon>(home).Where(i=>i.Kind=="Play").ToArray();
   check(cardPlayIcons.Length>=8 && cardPlayIcons.All(i=>i.ThemedAsset=="play"),"every visible home video card uses the supplied play artwork");
   var rail=(Border)main.FindName("NavRail");var railArt=Descendants<SkinSurface>(rail).Single(s=>s.Kind=="Rail");
   var shared=(SkinSurface)main.FindName("SharedHeaderSurface");
   foreach(var id in new[]{"",SkinService.StarParty,SkinService.PhantomBlade})
    foreach(var scale in new[]{100,125,150})
     foreach(var color in new[]{AppTheme.Light,AppTheme.Dark}){
      SkinService.Instance.Configure(id,true,true);Svc.Settings.SetUiScalePercent(scale);Svc.Theme.SetMode(color);Pump(80);
      var edge=railArt.TransformToAncestor(main).Transform(new Point(railArt.ActualWidth,0)).X;
      var start=shared.TransformToAncestor(main).Transform(new Point()).X;
      check(Math.Abs(edge-start)<.1,$"rail artwork meets the header with no reserved border gap: {id}/{scale}/{color}");
     }
   SkinService.Instance.Configure(SkinService.PhantomBlade,true,true);Svc.Settings.SetUiScalePercent(oldScale);Pump(100);
   foreach(var colorMode in new[]{AppTheme.Dark,AppTheme.Light}) {
    Svc.Theme.SetMode(colorMode);Pump(100);typeof(HomeViewModel).GetProperty("Error")!.SetValue(homeVm,"");Pump(60);
    foreach(var text in Descendants<TextBlock>(home).Where(t=>t.Text=="fixture: no data"))
     for(DependencyObject? parent=text;parent!=null;parent=System.Windows.Media.VisualTreeHelper.GetParent(parent))
      if(parent is Border border && border.BorderThickness.Left>0){border.Visibility=Visibility.Collapsed;break;}
    Save(main,Path.Combine(artifacts,$"phantom-home-{colorMode}.png"));
   }
   check(((Button)main.FindName("StarPartyWebsite")).Visibility==Visibility.Collapsed,"Star Party website is not exposed by another skin");
   main.Width=860;Pump(100);Save(main,Path.Combine(artifacts,"phantom-home-compact.png"));main.Hide();
   var profile=new MineOverviewPage();var host=new Window{Width=1120,Height=740,Content=profile};windows.Add(host);host.Show();Pump(80);
   profile.DataContext=new {IsLoggedIn=true,Name="影境旅人",Mid=42,Face="",Sign="以身证道，以命寻心",FollowingText="128",FollowersText="256",VideoCountText="12",Error="",ShowEmpty=false,Loading=false,Videos=Array.Empty<VideoItem>()};
   foreach(var width in new[]{1120,700}) {host.Width=width;Pump(80);Save(host,Path.Combine(artifacts,$"phantom-profile-{width}.png"));}host.Close();
   var player=new PlayerWindow();windows.Add(player);Set(player,"_actualAudio",AudioQuality.Dolby);
   Call(player,"FillQualityMenu",126);
   check(((TextBlock)player.FindName("AudioBadgeText")).Text=="杜比音轨","audio badge describes selected source rather than verified Atmos output");
   check(((Border)player.FindName("BtnAudioBadge")).ToolTip.ToString()!.Contains("未验证"),"output capability limitation is visible");
   check(((Border)player.FindName("BtnAudioBadge")).ContextMenu!.Items.Count==1,"compatibility recovery is separate from the regular quality list");
   vm.SkinEffects=true;player.Width=1320;player.Height=860;player.Show();Pump(180);
   foreach(var name in new[]{"BtnLike","BtnCoin","BtnFavorite"})((Button)player.FindName(name)).IsEnabled=true;
   CheckSelectedUi(check,artifacts,main,player);
   var actions=Descendants<SkinActionIcon>(player).Where(i=>i.Width==36).ToArray();
   check(actions.Any(i=>i.Kind=="Like" && i.ThemedAsset=="ui_0395") && actions.Any(i=>i.Kind=="Coin" && i.ThemedAsset=="ui_1327") && actions.Any(i=>i.Kind=="Favorite" && i.ThemedAsset==PhantomUiArtwork.Asset("Favorite",Svc.Theme.IsDark)),"the actual action row maps fire, bell and flower to distinct actions");
   var likedIcon=(SkinActionIcon)player.FindName("ThemedLikeIcon");
   Set(player,"_isLiked",true);Call(player,"UpdateActionCounts");
   check(likedIcon.IsActive && likedIcon.ThemedAsset=="ui_0397","a liked video shows the purple flame");
   Save(player,Path.Combine(artifacts,"phantom-player-liked.png"));
   Set(player,"_isLiked",false);Call(player,"UpdateActionCounts");
   check(!likedIcon.IsActive && likedIcon.ThemedAsset=="ui_0395","canceling a video like restores the white flame");
   var playButton=(Button)player.FindName("BtnPlay");var mini=(SkinActionIcon)player.FindName("WebMiniPlayGlyph");
   Call(player,"ShowControls");playButton.Content=PlayerGlyphs.Play();Pump(80);
   check(((SkinActionIcon)playButton.Content).ThemedAsset=="play" && mini.ThemedAsset=="play","main player and mini player share the themed play state");
   // VideoView owns the real control bar in a separate HWND overlay which is not
   // mounted before media starts. Mount the same factory/style in a real viewport.
   var previewPlay=new Button{Width=36,Height=36,Style=playButton.Style,Content=PlayerGlyphs.Play(),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
   var previewWindow=new Window{Width=140,Height=140,Background=System.Windows.Media.Brushes.Black,Content=previewPlay};windows.Add(previewWindow);previewWindow.Show();Pump(100);
   var previewGlyph=(SkinActionIcon)previewPlay.Content;
   var buttonVisual=new DrawingVisual();using(var drawing=buttonVisual.RenderOpen())drawing.DrawRectangle(new VisualBrush(previewPlay){Stretch=Stretch.Fill},null,new Rect(0,0,previewPlay.ActualWidth,previewPlay.ActualHeight));
   var buttonImage=new RenderTargetBitmap(120,120,288,288,PixelFormats.Pbgra32);buttonImage.Render(buttonVisual);
   var buttonPng=new PngBitmapEncoder();buttonPng.Frames.Add(BitmapFrame.Create(buttonImage));using(var output=File.Create(Path.Combine(artifacts,"phantom-play-button.png")))buttonPng.Save(output);
   var pixels=new byte[120*120*4];buttonImage.CopyPixels(pixels,120*4,0);check(Enumerable.Range(0,120*120).Count(i=>pixels[i*4+3]>64)>500,"the enlarged circular play artwork actually renders into the button viewport");
   Console.WriteLine($"PLAY glyph={previewGlyph.Width}/{previewGlyph.ActualWidth} button={previewPlay.ActualWidth}x{previewPlay.ActualHeight} loaded={previewGlyph.IsLoaded} parent={System.Windows.Media.VisualTreeHelper.GetParent(previewGlyph)?.GetType().Name} asset={previewGlyph.ThemedAsset}");
   check(previewGlyph.ActualWidth>=previewPlay.ActualWidth*.85,"the official circular play icon fills a loaded player button instead of retaining a tiny 15px box");
   previewWindow.Close();
   playButton.Content=PlayerGlyphs.Pause();Pump(20);
   check(((SkinActionIcon)playButton.Content).ThemedAsset=="ui_1316" && mini.ThemedAsset=="ui_1316" && mini.Data.ToString()==PlayerGlyphs.Pause().Data.ToString(),"switching to pause keeps pause semantics in both controls");
   check(((SkinActionIcon)playButton.Content).Width is >=14 and <=40,"selected pause artwork remains legible within the control button");
   playButton.Content=PlayerGlyphs.Play();
   var live=new LiveRoomWindow();windows.Add(live);Call(live,"SetPauseGlyph",false);
   check(((SkinActionIcon)live.FindName("PauseGlyph")).ThemedAsset=="play","live-room playback uses the same original play artwork");
   Call(live,"SetPauseGlyph",true);check(((SkinActionIcon)live.FindName("PauseGlyph")).ThemedAsset=="ui_1316","live-room pause is not replaced by play artwork");
   var dynamicCard=new DynamicFeedCard{DataContext=new DynamicItem{Kind=DynamicKind.Video,AuthorName="示例作者",Video=new VideoItem{Title="示例投稿",ViewCount=1200}}};
   var dynamicHost=new Window{Width=700,Height=650,Content=dynamicCard};windows.Add(dynamicHost);dynamicHost.Show();Pump(80);
   check(Descendants<SkinActionIcon>(dynamicCard).Any(i=>i.Kind=="Play" && i.ThemedAsset=="play"),"dynamic video-card playback also uses the themed asset");
   var dynamicLike=Descendants<SkinActionIcon>(dynamicCard).Single(i=>i.Kind=="Like");
   ((DynamicItem)dynamicCard.DataContext).SetLiked(true);Pump(30);check(dynamicLike.ThemedAsset=="ui_2114" && dynamicLike.IsActive,"dynamic like notifications select the compact active flame");
   ((DynamicItem)dynamicCard.DataContext).SetLiked(false);Pump(30);check(dynamicLike.ThemedAsset=="ui_2114" && !dynamicLike.IsActive,"canceling a dynamic like restores its white flame");dynamicHost.Close();
   ((TextBlock)player.FindName("VideoTitleText")).Text="影之刃零 · 影境装扮预览";
   var reaction=(ThemeReactionHost)player.FindName("ThemeReactions");
   check(!reaction.Show(ThemeReaction.Like,(Button)player.FindName("BtnLike")),"material themes never spawn a second copper sprite");
   Call(player,"PlayThemedAction",ThemeReaction.Like,(Button)player.FindName("BtnLike"),(Grid)player.FindName("LikeIconBox"),true);
   check(likedIcon.Effect is DropShadowEffect && !reaction.IsPlaying,"successful like illuminates the existing copper icon");
   Pump(200);Save(player,Path.Combine(artifacts,"phantom-player-reaction.png"));
   Save((FrameworkElement)player.FindName("ActionRowMark"),Path.Combine(artifacts,"phantom-action-row.png"),3);
   Pump(800);Call(player,"PlayThemedAction",ThemeReaction.Favorite,(Button)player.FindName("BtnFavorite"),(Grid)player.FindName("FavIconBox"),true);
   var flower=Descendants<SkinActionIcon>((Grid)player.FindName("FavIconBox")).Single();
   check(flower.Effect is DropShadowEffect && !reaction.IsPlaying,"successful favorite lights the existing flower without a second flower");
   Pump(200);Save(player,Path.Combine(artifacts,"phantom-player-memory-flower.png"));
   Pump(800);Call(player,"PlayThemedAction",ThemeReaction.Coin,(Button)player.FindName("BtnCoin"),(Grid)player.FindName("CoinIconBox"),true);
   var jade=(SkinActionIcon)player.FindName("ThemedCoinIcon");
   check(jade.Effect is DropShadowEffect && !reaction.IsPlaying,"successful coin lights the existing jade without a second jade");
   Pump(200);Save(player,Path.Combine(artifacts,"phantom-player-jade.png"));
   vm.SkinEffects=false;check(jade.Effect==null && !ThemeAudio.Enabled,"turning off effects removes glow and disables the sound pack together");
   Call(player,"PlayThemedAction",ThemeReaction.Coin,(Button)player.FindName("BtnCoin"),(Grid)player.FindName("CoinIconBox"),true);
   check(jade.Effect==null,"disabled theme effects do not fall back to material pop animations");
   vm.SkinEffects=true;
   Call(player,"PlayThemedAction",ThemeReaction.Favorite,(Button)player.FindName("BtnFavorite"),(Grid)player.FindName("FavIconBox"),false);
   check(flower.Effect==null,"canceling favorite does not illuminate the flower");
   var coinPicker=new CoinWindow();windows.Add(coinPicker);coinPicker.Show();Pump(80);
   var coinIcons=Descendants<SkinActionIcon>(coinPicker).Where(i=>i.Kind=="Coin").ToArray();
   check(coinIcons.Length==2 && coinIcons.Any(i=>i.Count==1) && coinIcons.Any(i=>i.Count==2) && coinIcons.All(i=>i.ThemedAsset=="ui_1325"),"coin picker retains separate one- and two-coin options with compact bell art");
   Save(coinPicker,Path.Combine(artifacts,"phantom-coin-picker.png"));coinPicker.Close();
   using var audioHttp=new HttpClient(new AudioImagesFontsTests.AudioFixture());
   typeof(ApiClient).GetField("_http",Hidden)!.SetValue(Svc.Api,audioHttp);
   Set(player,"_currentBvid","BVfixture");Set(player,"_currentCid",42L);Set(player,"_currentQn",80);
   var retry=(Task)Call(player,"SwitchAudioQualityAsync",AudioQuality.High,false)!;
   Until(()=>retry.IsCompleted);retry.GetAwaiter().GetResult();
   check(Field<AudioQuality>(player,"_actualAudio")==AudioQuality.High && Field<int>(player,"_currentQn")==80,"temporary compatibility retry uses AAC while preserving video resolution");
   check(Svc.Settings.PreferredAudioQuality==preference,"compatibility retry leaves the saved premium preference unchanged");
   typeof(ApiClient).GetField("_http",Hidden)!.SetValue(Svc.Api,http);
   check(Svc.Settings.AccentColor==accent && Svc.Settings.PreferredAudioQuality==preference,"decoration and diagnostics preserve saved preferences");
   vm.SkinSelected=vm.SkinOptions[1];check(SkinService.Instance.Image("head_myself_bg").PixelWidth!=0 && mini.ThemedAsset==null,"switching to Star Party restores its own artwork and standard playback");
   vm.SkinSelected=vm.SkinOptions[0];check(actions.All(i=>i.ThemedAsset==null) && mini.ThemedAsset==null,"removing the theme restores the original icons in already-created views");
  } finally { typeof(App).GetProperty(nameof(App.MuteForThisRun))!.SetValue(null,mute);foreach(var w in windows)try{w.Close();}catch{} typeof(ApiClient).GetField("_http",Hidden)!.SetValue(Svc.Api,oldHttp);SkinService.Instance.Configure(skin,effects,useAccent);Svc.Settings.SetUiScalePercent(oldScale);Svc.Theme.SetMode(mode);SynchronizationContext.SetSynchronizationContext(context); }
 }
}
