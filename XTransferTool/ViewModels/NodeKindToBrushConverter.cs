using System;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XTransferTool.ViewModels;

public sealed class NodeKindToBrushConverter : IValueConverter
{
    public static NodeKindToBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is NodeKind kind)
        {
            return kind switch
            {
                NodeKind.Tag => new SolidColorBrush(Color.Parse("#23D5FF")),
                NodeKind.User => new SolidColorBrush(Color.Parse("#2EE59D")),
                _ => Brushes.Gray
            };
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}

