using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BiliTouch.Helpers;

/// <summary>A retained tab exposes accessibility controls only while it is the active page.</summary>
public sealed class TabViewportScrollViewer : ScrollViewer
{
    private bool _keepPreparedLayout;
    internal Func<bool>? PreparationSuspended;
    internal bool CanPrepareHidden => _keepPreparedLayout && Visibility == Visibility.Hidden;
    internal bool IsPreparationSuspended => PreparationSuspended?.Invoke() == true;

    static TabViewportScrollViewer()
    {
        VisibilityProperty.OverrideMetadata(typeof(TabViewportScrollViewer),
            new FrameworkPropertyMetadata(Visibility.Visible, null, (owner, value) =>
                (Visibility)value == Visibility.Collapsed && ((TabViewportScrollViewer)owner)._keepPreparedLayout ? Visibility.Hidden : value));
    }

    internal void KeepPreparedLayout()
    {
        if (_keepPreparedLayout) return;
        _keepPreparedLayout = true;
        CoerceValue(VisibilityProperty);
    }
    private bool _isGesturePreview;
    internal bool IsGesturePreview
    {
        get => _isGesturePreview;
        set
        {
            if (_isGesturePreview == value) return;
            _isGesturePreview = value;
            UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
        }
    }

    public TabViewportScrollViewer()
    {
        SetResourceReference(StyleProperty, typeof(ScrollViewer));
        IsVisibleChanged += (_, _) => UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new TabViewportPeer(this);
    private sealed class TabViewportPeer : ScrollViewerAutomationPeer
    {
        internal TabViewportPeer(TabViewportScrollViewer owner) : base(owner) { }
        private bool Active => Owner.IsVisible && !((TabViewportScrollViewer)Owner).IsGesturePreview;
        protected override List<AutomationPeer>? GetChildrenCore() => Active ? base.GetChildrenCore() : null;
        protected override bool IsControlElementCore() => Active && base.IsControlElementCore();
        protected override bool IsContentElementCore() => Active && base.IsContentElementCore();
    }
}
