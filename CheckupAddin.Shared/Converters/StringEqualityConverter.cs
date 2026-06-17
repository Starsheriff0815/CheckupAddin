using System;
using System.Globalization;
using System.Windows.Data;

namespace CheckupAddIn.Converters
{
    /// <summary>
    /// IMultiValueConverter that returns true when all bound string values are equal.
    /// Used to highlight the active SPEZI tab filter button in the autocomplete popup.
    /// </summary>
    internal class StringEqualityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return false;
            string a = values[0] as string ?? "";
            string b = values[1] as string ?? "";
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
