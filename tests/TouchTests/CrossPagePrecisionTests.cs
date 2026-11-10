using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliTouch;
using BiliTouch.Helpers;
using BiliTouch.ViewModels;
using BiliTouch.Views.Pages;

internal static class CrossPagePrecisionTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> ready, int timeout = 1200)
    {
        var start = Environment.TickCount64;
        while (!ready())
        {
            if (Environment.TickCount64 - start > timeout) break;
            Pump(20);
        }
    }
    internal static void Run(Action<bool, string> check)
    {
        var filter = new PrecisionTouchpadInput.ProgressFilter();
        check(filter.Accept(.2) && !filter.Accept(.2) && filter.Accept(.201), "stationary reports skip preview work without discarding real movement");
        filter.Reset(); check(filter.Accept(.2), "a new contact always receives its first position even if it equals the old one");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var main = new MainWindow(); main.Show();
        try
        {
            foreach (var key in new[] { PageKey.Home, PageKey.Mine, PageKey.Follow, PageKey.Settings, PageKey.Messages, PageKey.Search })
            {
                App.MainVm.Navigate(key); Pump(400);
                var page = (UserControl)typeof(MainWindow).GetMethod("PageOf", Hidden)!.Invoke(main, new object[] { key })!;
                var module = page.GetType().GetProperty("ModuleGestures", Hidden)?.GetValue(page) as TabNavigationGestures;
                RadioButton[]? buttons = module != null ? (RadioButton[])typeof(TabNavigationGestures).GetProperty("Buttons", Hidden)!.GetValue(module)! : null;
                void Reset()
                {
                    if (page is HomePage h) ((RadioButton)h.FindName("RecommendTab")).IsChecked = true;
                    else if (page is MinePage m) ((RadioButton)m.FindName("OverviewTab")).IsChecked = true;
                    else if (page.DataContext is FollowViewModel f) f.ShowAllFeedCommand.Execute(null);
                    else buttons![0].IsChecked = true;
                    Pump(240);
                }
                int Index() => page is HomePage h ? h.CurrentTabIndex : page is MinePage m ? m.CurrentTabIndex :
                    page.DataContext is FollowViewModel f ? (f.VideoOnly ? 1 : 0) : module!.SelectedIndex;
                Reset();
                var retained = key == PageKey.Settings ? (RetainedTemplateContent)page.FindName("SettingsTabContent") : null;
                // Preparation is deliberately scheduled behind input/rendering;
                // observe its completion rather than assuming 640ms is enough.
                var preparationStarted = Environment.TickCount64;
                while (retained != null && retained.CachedViewCount < 4 && Environment.TickCount64 - preparationStarted < 3000) Pump(20);
                var originalSettingsView = retained?.ActiveView;
                if (retained != null)
                    check(retained.CachedViewCount == 4 && originalSettingsView?.DataContext == page.DataContext,
                        $"Settings: idle templates retain the real view model and all four cached pages (cached={retained.CachedViewCount}, context={originalSettingsView?.DataContext?.GetType().Name}, interacting={module?.IsInteracting}, visible={page.IsVisible})");
                var name = key switch { PageKey.Home => "TabHost", PageKey.Mine => "SectionHost", PageKey.Follow => "FollowFeedHost", PageKey.Search => "ResultArea", _ => "ModuleContent" };
                var viewport = (FrameworkElement)page.FindName(name);
                if (!viewport.IsVisible)
                {
                    check(main.ResolvePrecisionTouchpadTarget(main.PointToScreen(new Point(600, 350))) == null,
                        $"{key}: hidden/logged-out content does not claim a physical gesture");
                    // Exercise the layout without copying an account or making
                    // a fake authenticated session. Only this fixture's hidden
                    // content ancestors are exposed; the VM remains logged out.
                    for (DependencyObject? item = viewport; item != null && item != page; item = System.Windows.Media.VisualTreeHelper.GetParent(item))
                        if (item is FrameworkElement element) element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                    page.UpdateLayout(); Pump(100);
                }
                // Use a fixed screen position over the entering neighbour. It
                // remains inside the viewport even while its old page moves out.
                var point = viewport.PointToScreen(new Point(viewport.ActualWidth * .9, 80));
                PrecisionTouchpadInput.Target Target()
                {
                    var target = main.ResolvePrecisionTouchpadTarget(point);
                    check(target != null, $"{key}: stationary viewport accepts a contact during settle");
                    return target!;
                }
                var t = Target(); t.Begin(); t.Preview(.1); t.End(true); Pump(240);
                check(Index() == 0, $"{key}: short fast physical contact stays on its page");
                t = Target(); t.Begin(); t.Preview(.7); Pump(16);
                check(Index() == 0, $"{key}: page waits for physical lift");
                if (page is HomePage)
                {
                    var activeScroll = (TabViewportScrollViewer)page.FindName("RecommendScroll");
                    var peerScroll = (TabViewportScrollViewer)page.FindName("PopularScroll");
                    var activePeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(activeScroll)!;
                    var decorativePeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(peerScroll)!;
                    check(!activeScroll.IsGesturePreview && peerScroll.IsGesturePreview && (activePeer.GetChildren()?.Count ?? 0) > 0 && (decorativePeer.GetChildren()?.Count ?? 0) == 0,
                        "Home: active scroll content stays accessible while the incoming preview has no duplicate controls");
                }
                t.End(true); Pump(25);
                t = Target(); t.Begin(); t.Preview(-.1); t.End(true); Until(() => Index() == 1); Pump(280);
                check(Index() == 1, $"{key}: short reversal cannot undo the previous valid release");
                if (page is HomePage)
                {
                    var activeScroll = (TabViewportScrollViewer)page.FindName("PopularScroll");
                    var hiddenScroll = (TabViewportScrollViewer)page.FindName("RecommendScroll");
                    var activePeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(activeScroll)!;
                    var hiddenPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(hiddenScroll)!;
                    check(!activeScroll.IsGesturePreview && (activePeer.GetChildren()?.Count ?? 0) > 0 && (hiddenPeer.GetChildren()?.Count ?? 0) == 0,
                        "Home: settled page restores accessibility and the outgoing page no longer exposes hidden controls");
                }
                t = Target(); t.Begin(); t.Preview(-.7); t.End(true); Until(() => Index() == 0);
                check(Index() == 0, $"{key}: over-half reverse returns exactly one page");
                if (retained != null)
                {
                    check(ReferenceEquals(retained.ActiveView, originalSettingsView), "Settings: returning to a tab reuses its live controls instead of rebuilding XAML");
                    var preview = (FrameworkElement)typeof(TabNavigationGestures).GetField("_peerHost", Hidden)!.GetValue(module!)!;
                    var previewPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(preview)!;
                    var realPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement((FrameworkElement)page.FindName("ModuleContent"))!;
                    check(!previewPeer.IsControlElement() && (previewPeer.GetChildren()?.Count ?? 0) == 0 && (realPeer.GetChildren()?.Count ?? 0) > 0,
                        "Settings: decorative copies are excluded from accessibility while actual controls remain exposed");
                }
                t = Target(); t.Begin();
                foreach (var p in new[] { 4d, -8d, 12d, .8d }) { t.Preview(p); Pump(16); check(Index() == 0, $"{key}: a held contact cannot traverse several tabs"); }
                t.End(true); Until(() => Index() == 1);
                check(Index() == 1, $"{key}: one physical session ends on its original adjacent tab");
                t = Target(); t.Begin(); t.Preview(-.8); t.End(false); Pump(260);
                check(Index() == 1, $"{key}: cancelled contact returns without committing");
                if (key is PageKey.Home or PageKey.Mine or PageKey.Settings)
                {
                    Reset();
                    t = Target(); t.Begin(); t.Preview(.7); t.End(true); Pump(25);
                    t = Target(); t.Begin(); t.Preview(.7); t.End(true); Until(() => Index() == 2);
                    check(Index() == 2, $"{key}: two same-direction contacts overlapping settle each advance one page");
                }
            }
            check(main.ResolvePrecisionTouchpadTarget(main.PointToScreen(new Point(20, 200))) == null,
                "sidebar is outside every page gesture's viewport");
        }
        finally { App.IsExiting = true; main.Close(); App.IsExiting = false; }
    }
}
