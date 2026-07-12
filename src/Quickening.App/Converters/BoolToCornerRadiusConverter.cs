using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Quickening.App.Converters;

public sealed class BoolToCornerRadiusConverter : IValueConverter
{
    public CornerRadius TrueValue { get; set; }
    public CornerRadius FalseValue { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
