using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DeskShell.Files;

/// <summary>
/// Same-size tiles in rows that wrap, creating only the tiles on screen, so a folder of thousands of
/// pictures scrolls smoothly. (WPF's WrapPanel creates every tile up front.)
/// </summary>
public sealed class TilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(TilePanel),
        new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(TilePanel),
        new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    Size _extent, _viewport;
    Point _offset;
    TranslateTransform _transform = new();

    public TilePanel() => RenderTransform = _transform;

    /// <summary>Tiles per row at the current width.</summary>
    public int Columns => Math.Max(1, (int)((_viewport.Width > 0 ? _viewport.Width : ActualWidth) / ItemWidth));

    int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        var count = ItemCount;
        var width = double.IsInfinity(available.Width) ? ItemWidth * 6 : available.Width;
        var height = double.IsInfinity(available.Height) ? ItemHeight * 4 : available.Height;
        var columns = Math.Max(1, (int)(width / ItemWidth));
        var rows = (count + columns - 1) / columns;
        UpdateScrollInfo(new Size(width, height), new Size(columns * ItemWidth, rows * ItemHeight));

        // The rows on screen, plus one either side for smooth keyboard movement.
        var firstRow = Math.Max(0, (int)(_offset.Y / ItemHeight) - 1);
        var lastRow = (int)((_offset.Y + height) / ItemHeight) + 1;
        var first = firstRow * columns;
        var last = Math.Min(count - 1, (lastRow + 1) * columns - 1);

        _ = InternalChildren; // makes sure the generator exists
        var generator = ItemContainerGenerator;
        if (count > 0 && first <= last)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (var i = first; i <= last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out var isNew) is not UIElement child) break;
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
        }
        CleanUp(first, last);
        return new Size(width, height);
    }

    void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            // Keep the keyboard-focused tile, or focus would jump out of the list.
            if (InternalChildren[i].IsKeyboardFocusWithin) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns;
        var generator = ItemContainerGenerator;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            InternalChildren[i].Arrange(new Rect(index % columns * ItemWidth, index / columns * ItemHeight, ItemWidth, ItemHeight));
        }
        return final;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.Position.Index >= 0 && args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    protected override void BringIndexIntoView(int index)
    {
        var row = index / Columns;
        var top = row * ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset.Y + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }

    void UpdateScrollInfo(Size viewport, Size extent)
    {
        if (viewport == _viewport && extent == _extent) return;
        _viewport = viewport;
        _extent = extent;
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > max) SetOffset(max);
        ScrollOwner?.InvalidateScrollInfo();
    }

    void SetOffset(double y)
    {
        _offset.Y = y;
        _transform.Y = -y;
    }

    // ---------- IScrollInfo ----------

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - ItemHeight / 3);
    public void LineDown() => SetVerticalOffset(_offset.Y + ItemHeight / 3);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - SystemParameters.WheelScrollLines * 40);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + SystemParameters.WheelScrollLines * 40);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _offset.Y) < 0.1) return;
        SetOffset(offset);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] != visual && !InternalChildren[i].IsAncestorOf(visual)) continue;
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) BringIndexIntoView(index);
            break;
        }
        return rectangle;
    }

    /// <summary>For the keyboard: the index of the tile above or below.</summary>
    public void ScrollToIndex(int index) => BringIndexIntoView(index);
}
