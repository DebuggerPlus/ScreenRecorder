using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.App.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null && !string.IsNullOrEmpty(value.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ViewConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PosToAlignConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mode = parameter?.ToString() ?? "H";
        if (value is LogoPosition pos)
        {
            if (mode == "H") {
                return pos switch {
                    LogoPosition.TopLeft => HorizontalAlignment.Left,
                    LogoPosition.BottomLeft => HorizontalAlignment.Left,
                    LogoPosition.TopRight => HorizontalAlignment.Right,
                    LogoPosition.BottomRight => HorizontalAlignment.Right,
                    LogoPosition.Center => HorizontalAlignment.Center,
                    _ => HorizontalAlignment.Right
                };
            } else {
                return pos switch {
                    LogoPosition.TopLeft => VerticalAlignment.Top,
                    LogoPosition.TopRight => VerticalAlignment.Top,
                    LogoPosition.BottomLeft => VerticalAlignment.Bottom,
                    LogoPosition.BottomRight => VerticalAlignment.Bottom,
                    LogoPosition.Center => VerticalAlignment.Center,
                    _ => VerticalAlignment.Top
                };
            }
        }
        return mode == "H" ? HorizontalAlignment.Right : VerticalAlignment.Top;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

