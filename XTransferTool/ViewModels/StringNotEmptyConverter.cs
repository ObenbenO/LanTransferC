using System;
using Avalonia;
using Avalonia.Data.Converters;

namespace XTransferTool.ViewModels;

public sealed class StringNotEmptyConverter : IValueConverter
{
    public static StringNotEmptyConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => AvaloniaProperty.UnsetValue;
}

