using Avalonia;
using Avalonia.Controls;

namespace ManiaReplayMaster.Desktop.Controls;

/// <summary>A label, an editor and actions; narrow windows move the label above the editor.</summary>
public sealed class FormRow : Grid
{
    public static readonly StyledProperty<bool> IsStackedProperty =
        AvaloniaProperty.Register<FormRow, bool>(nameof(IsStacked));
    public static readonly StyledProperty<bool> StackActionsProperty =
        AvaloniaProperty.Register<FormRow, bool>(nameof(StackActions));

    public bool IsStacked { get => GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }
    public bool StackActions { get => GetValue(StackActionsProperty); set => SetValue(StackActionsProperty, value); }

    public FormRow()
    {
        Children.CollectionChanged += (_, _) => UpdateLayoutMode();
        UpdateLayoutMode();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsStackedProperty || change.Property == StackActionsProperty)
            UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        ColumnDefinitions = new ColumnDefinitions(IsStacked ? (StackActions ? "*" : "*,Auto")
            : (StackActions ? "152,*,*" : "152,*,Auto"));
        RowDefinitions = new RowDefinitions(IsStacked ? (StackActions ? "Auto,Auto,Auto" : "Auto,Auto") : "Auto");
        RowSpacing = IsStacked ? 3 : 0;
        ColumnSpacing = StackActions && !IsStacked ? 6 : 0;
        for (var i = 0; i < Children.Count; i++)
        {
            SetRow(Children[i], IsStacked ? (StackActions ? i : Math.Min(i, 1)) : 0);
            SetColumn(Children[i], IsStacked ? (StackActions ? 0 : Math.Max(0, i - 1)) : i);
            SetColumnSpan(Children[i], IsStacked && !StackActions && i == 0 ? 2 : 1);
        }
    }
}
