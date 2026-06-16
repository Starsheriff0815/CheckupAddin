using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CheckupAddIn.Converters
{
    public class CardTypeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value as string) switch
            {
                "Dropdown"      => new SolidColorBrush(Color.FromRgb(0x3A, 0x7B, 0xD5)),
                "Sync"          => new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60)),
                "Link"          => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
                "Button"        => new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD)),
                "Search"        => new SolidColorBrush(Color.FromRgb(0x16, 0xA0, 0x85)),
                "Formula"       => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
                "MultiPick"     => new SolidColorBrush(Color.FromRgb(0x29, 0x80, 0xB9)),
                "PairTransform" => new SolidColorBrush(Color.FromRgb(0xD3, 0x54, 0x00)),
                _               => new SolidColorBrush(Color.FromRgb(0x55, 0x60, 0x70)),
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
