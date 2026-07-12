using Microsoft.UI.Xaml.Data;

namespace Quickening.App.Converters;

// Same instantiate-with-XAML-properties pattern as BoolToThicknessConverter/
// BoolToCornerRadiusConverter - used for the network-drive row's dimmed
// Opacity (new-screens 4i: "network row: 55% opacity").
public sealed class BoolToDoubleConverter : IValueConverter
{
    public double TrueValue { get; set; }
    public double FalseValue { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
