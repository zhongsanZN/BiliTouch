using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BiliDesk.Helpers;

/// <summary>Small bounded renderer for real-time live messages; no video timeline or replay scheduling.</summary>
public sealed class LiveOverlayRenderer : IDisposable
{
    private readonly Canvas _canvas;
    private readonly List<(TextBlock text, TranslateTransform move, long start, double width)> _items = new();
    private readonly long[] _lanes = new long[12];
    private bool _attached;
    private double _fontSize = 20, _area = .35, _opacity = .8, _speed = 150;
    public LiveOverlayRenderer(Canvas canvas) { _canvas = canvas; canvas.Unloaded += (_, _) => Clear(); }
    public void Configure(double fontSize, double area, double opacity, int speedLevel)
    {
        Clear(); _fontSize = Math.Clamp(fontSize, 12, 60); _area = Math.Clamp(area, .25, 1); _opacity = Math.Clamp(opacity, 0, 1);
        _speed = 1200 / DanmakuMotion.ScrollSeconds(speedLevel);
    }
    public void Add(string message, uint color)
    {
        if (_canvas.ActualWidth < 1 || _items.Count >= 48) return;
        var now = Stopwatch.GetTimestamp();
        var lineHeight = _fontSize * 1.5;
        int lanes = Math.Clamp((int)(_canvas.ActualHeight * _area / lineHeight), 1, _lanes.Length);
        int lane = Array.FindIndex(_lanes, 0, lanes, released => released <= now);
        if (lane < 0) return;
        var move = new TranslateTransform(_canvas.ActualWidth, lane * lineHeight + 12);
        var text = new TextBlock { Text = message.Length > 500 ? message[..500] : message, FontSize = _fontSize, Opacity = _opacity, Foreground = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)), RenderTransform = move, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, ShadowDepth = 1, BlurRadius = 2, Opacity = .9 } };
        text.Measure(new Size(double.PositiveInfinity, lineHeight));
        double width = text.DesiredSize.Width;
        // Equal speed for all rolling messages, with enough release time to avoid chasing a long line.
        _lanes[lane] = now + (long)((width + 50) / _speed * Stopwatch.Frequency);
        _canvas.Children.Add(text); _items.Add((text, move, now, width));
        if (!_attached) { _attached = true; CompositionTarget.Rendering += Render; }
    }
    private void Render(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            var x = _canvas.ActualWidth - Stopwatch.GetElapsedTime(item.start, now).TotalSeconds * _speed;
            if (x < -item.width) { _canvas.Children.Remove(item.text); _items.RemoveAt(i); }
            else item.move.X = x;
        }
        if (_items.Count == 0) Detach();
    }
    private void Detach() { if (_attached) { CompositionTarget.Rendering -= Render; _attached = false; } }
    public void Clear() { Detach(); _items.Clear(); _canvas.Children.Clear(); Array.Clear(_lanes); }
    public void Dispose() => Clear();
}
