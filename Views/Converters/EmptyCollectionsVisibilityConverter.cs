﻿using System.Collections;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Visibility = System.Windows.Visibility;

namespace ViewTracker.Views.Converters;

public sealed class EmptyCollectionsVisibilityConverter : MarkupExtension, IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        foreach (var value in values)
        {
            switch (value)
            {
                case ICollection {Count: > 0}:
                case > 0:
                    return Visibility.Collapsed;
            }
        }

        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return this;
    }
}