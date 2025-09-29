using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace ViewTracker.Views.Converters;

public class EnumBooleanConverter<TEnum> : MarkupExtension, IValueConverter where TEnum : Enum
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TEnum valueEnum)
        {
            throw new ArgumentException($"{nameof(value)} is not type: {typeof(TEnum)}");
        }

        if (parameter is not TEnum parameterEnum)
        {
            throw new ArgumentException($"{nameof(parameter)} is not type: {typeof(TEnum)}");
        }

        return EqualityComparer<TEnum>.Default.Equals(valueEnum, parameterEnum);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not TEnum parameterEnum)
        {
            throw new ArgumentException($"{nameof(parameter)} is not type: {typeof(TEnum)}");
        }

        return parameterEnum;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return this;
    }
}