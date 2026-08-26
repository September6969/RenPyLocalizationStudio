using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RenPyLocalizationStudio.App.Behaviors;

public static class ScrollAnchorBehavior
{
    public static readonly DependencyProperty AnchorItemProperty = DependencyProperty.RegisterAttached(
        "AnchorItem", typeof(object), typeof(ScrollAnchorBehavior), new PropertyMetadata(null, OnAnchorItemChanged));

    public static void SetAnchorItem(DependencyObject element, object? value) => element.SetValue(AnchorItemProperty, value);
    public static object? GetAnchorItem(DependencyObject element) => element.GetValue(AnchorItemProperty);

    private static void OnAnchorItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || e.NewValue is null) return;
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!list.Items.Contains(e.NewValue)) return;
            list.ScrollIntoView(e.NewValue);
            list.UpdateLayout();
        });
    }
}
