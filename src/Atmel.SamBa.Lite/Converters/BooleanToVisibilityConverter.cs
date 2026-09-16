using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;


namespace Anp.Atmel.SamBa.Lite.Converters
{
    /// <summary>
    /// bool -> Visibility. Use ConverterParameter="invert" to invert.
    /// </summary>
    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = false;
            if (value is bool bb)
                b = bb;

            if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
                b = !b;

            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is Visibility v))
                return false;

            var b = v == Visibility.Visible;
            if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
                b = !b;

            return b;
        }
    }
}
