using Avalonia;
using Avalonia.Controls;

namespace ManiaReplayMaster.Desktop.Controls;

/// <summary>Equal-width form fields that reflow using logical pixels, independent of monitor DPI.</summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly StyledProperty<double> MinimumItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(MinimumItemWidth), 210);
    public static readonly StyledProperty<int> MaximumColumnsProperty =
        AvaloniaProperty.Register<AdaptiveGrid, int>(nameof(MaximumColumns), 3);
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(ColumnSpacing), 12);
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(RowSpacing), 6);

    static AdaptiveGrid() => AffectsMeasure<AdaptiveGrid>(MinimumItemWidthProperty,
        MaximumColumnsProperty, ColumnSpacingProperty, RowSpacingProperty);

    public double MinimumItemWidth { get => GetValue(MinimumItemWidthProperty); set => SetValue(MinimumItemWidthProperty, value); }
    public int MaximumColumns { get => GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    private int Columns(double width) => double.IsPositiveInfinity(width)
        ? Math.Max(1, MaximumColumns)
        : Math.Clamp((int)Math.Floor((width + ColumnSpacing) / Math.Max(1, MinimumItemWidth + ColumnSpacing)),
            1, Math.Max(1, MaximumColumns));

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(c => c.IsVisible).ToArray();
        var columns = Columns(availableSize.Width);
        var itemWidth = double.IsPositiveInfinity(availableSize.Width) ? MinimumItemWidth
            : Math.Max(0, (availableSize.Width - (columns - 1) * ColumnSpacing) / columns);
        double height = 0;
        double width = 0;
        for (var row = 0; row < children.Length; row += columns)
        {
            double rowHeight = 0;
            for (var i = row; i < Math.Min(row + columns, children.Length); i++)
            {
                children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
                width = Math.Max(width, children[i].DesiredSize.Width);
            }
            height += rowHeight + (row == 0 ? 0 : RowSpacing);
        }
        return new Size(Math.Min(availableSize.Width, width * columns + (columns - 1) * ColumnSpacing), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children.Where(c => c.IsVisible).ToArray();
        var columns = Columns(finalSize.Width);
        var itemWidth = Math.Max(0, (finalSize.Width - (columns - 1) * ColumnSpacing) / columns);
        double y = 0;
        for (var row = 0; row < children.Length; row += columns)
        {
            var rowHeight = children.Skip(row).Take(columns).Max(c => c.DesiredSize.Height);
            for (var i = row; i < Math.Min(row + columns, children.Length); i++)
                children[i].Arrange(new Rect((i - row) * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
            y += rowHeight + RowSpacing;
        }
        return finalSize;
    }
}
