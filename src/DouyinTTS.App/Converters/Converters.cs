using DouyinTTS.Core.Live.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DouyinTTS.App.Converters;

public class BoolNegationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : value;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var boolValue = value is bool b && b;
        var invert = parameter?.ToString() == "Invert";
        if (invert) boolValue = !boolValue;
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && hex.Length == 9 && hex[0] == '#')
        {
            var r = System.Convert.ToByte(hex[1..3], 16);
            var g = System.Convert.ToByte(hex[3..5], 16);
            var b = System.Convert.ToByte(hex[5..7], 16);
            var a = System.Convert.ToByte(hex[7..9], 16);
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class ConnectionStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true
            ? new SolidColorBrush(Color.FromArgb(255, 76, 175, 80))
            : new SolidColorBrush(Color.FromArgb(255, 158, 158, 158));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class MessageTypeToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            LiveEventType.Danmaku => "弹幕",
            LiveEventType.Gift => "礼物",
            LiveEventType.Member => "进场",
            LiveEventType.Like => "点赞",
            _ => "系统"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
