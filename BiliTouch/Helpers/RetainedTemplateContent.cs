using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BiliTouch.Helpers;

/// <summary>Retains settings tab controls instead of rebuilding their XAML on every takeover.</summary>
public sealed class RetainedTemplateContent : ContentControl
{
    private const string HostName = "PART_RetainedHost";
    private readonly Dictionary<DataTemplate, TemplateSlot> _views = new();
    private Grid? _host;
    internal FrameworkElement? ActiveView { get; private set; }
    internal int CachedViewCount => _views.Count;

    public RetainedTemplateContent()
    {
        Template = new ControlTemplate(typeof(RetainedTemplateContent))
        {
            VisualTree = new FrameworkElementFactory(typeof(Grid), HostName)
        };
    }

    public override void OnApplyTemplate()
    {
        _host?.Children.Clear();
        base.OnApplyTemplate();
        _host = GetTemplateChild(HostName) as Grid;
        if (_host != null) foreach (var view in _views.Values) _host.Children.Add(view);
        ShowSelectedTemplate();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        foreach (var view in _views.Values) view.DataContext = newContent;
        ShowSelectedTemplate();
    }

    protected override void OnContentTemplateChanged(DataTemplate oldTemplate, DataTemplate newTemplate)
    {
        base.OnContentTemplateChanged(oldTemplate, newTemplate);
        ShowSelectedTemplate();
    }

    private TemplateSlot? GetView(DataTemplate template)
    {
        if (_host == null) return null;
        if (_views.TryGetValue(template, out var slot)) return slot;
        if (template.LoadContent() is not FrameworkElement content) return null;
        slot = new TemplateSlot { Child = content, DataContext = Content, Visibility = Visibility.Collapsed };
        _views.Add(template, slot); _host.Children.Add(slot);
        return slot;
    }

    private void ShowSelectedTemplate()
    {
        if (ContentTemplate == null || GetView(ContentTemplate) is not { } active) return;
        foreach (var slot in _views.Values)
        {
            var visible = ReferenceEquals(slot, active) ? Visibility.Visible : Visibility.Collapsed;
            if (slot.Visibility != visible)
            {
                slot.Visibility = visible;
                UIElementAutomationPeer.FromElement(slot)?.InvalidatePeer();
            }
        }
        ActiveView = active.Child as FrameworkElement;
    }

    internal void PrepareTemplate(DataTemplate template)
    {
        if (GetView(template) is not { } slot || slot.Visibility == Visibility.Visible) return;
        // Hidden permits measurement without drawing; collapse again before the
        // next frame so inactive tabs never affect the real scroll extent.
        slot.Visibility = Visibility.Hidden;
        slot.Measure(new Size(Math.Max(1, ActualWidth), double.PositiveInfinity));
        slot.Arrange(new Rect(new Point(), slot.DesiredSize));
        slot.Visibility = Visibility.Collapsed;
    }

    private sealed class TemplateSlot : Decorator
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new SlotPeer(this);
    }
    private sealed class SlotPeer : FrameworkElementAutomationPeer
    {
        internal SlotPeer(TemplateSlot owner) : base(owner) { }
        protected override List<AutomationPeer>? GetChildrenCore()
            => Owner.Visibility == Visibility.Visible ? base.GetChildrenCore() : null;
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
