using Microsoft.UI.Xaml.Data;

namespace Quickening.App.Converters;

// Plain bool negation - used to disable a network-drive row's CheckBox
// (IsEnabled="{Binding IsOnNetworkDrive, Converter={StaticResource
// InverseBoolConverter}}") without needing a Visibility conversion.
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
