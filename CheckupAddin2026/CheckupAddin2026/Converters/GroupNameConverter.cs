using System;
using System.Globalization;
using System.Windows.Data;
using CheckupAddIn.Services;

namespace CheckupAddIn.Converters
{
    /// <summary>
    /// Translates a field catalog group key (e.g. "Grp_SheetMetal") to the display string
    /// for the current language via LanguageLoader.Get(). Used in the ComboBox GroupStyle header.
    /// </summary>
    public class GroupNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => LanguageLoader.Get(value?.ToString() ?? "");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value;
    }
}
