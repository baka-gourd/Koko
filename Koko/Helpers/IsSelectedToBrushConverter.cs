using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

using System;
using Microsoft.UI.Xaml;

namespace Koko.Helpers;

public partial class IsSelectedToBrushConverter : DependencyObject, IValueConverter
{
    public static readonly DependencyProperty SelectedBrushProperty =
        DependencyProperty.Register(
            nameof(SelectedBrush),
            typeof(Brush),
            typeof(IsSelectedToBrushConverter),
            new PropertyMetadata(null));

    public static readonly DependencyProperty UnselectedBrushProperty =
        DependencyProperty.Register(
            nameof(UnselectedBrush),
            typeof(Brush),
            typeof(IsSelectedToBrushConverter),
            new PropertyMetadata(null));

    public Brush? SelectedBrush
    {
        get => (Brush?)GetValue(SelectedBrushProperty);
        set => SetValue(SelectedBrushProperty, value);
    }

    public Brush? UnselectedBrush
    {
        get => (Brush?)GetValue(UnselectedBrushProperty);
        set => SetValue(UnselectedBrushProperty, value);
    }

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? SelectedBrush! : UnselectedBrush!;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}