using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Quickening.App.Converters;

/// <summary>
/// Generic bool-to-brush swap, driven entirely by the two brushes set as
/// XAML object-element properties on the converter instance itself (see
/// HomePage.xaml's Page.Resources) rather than by ConverterParameter, so
/// each usage can reference different StaticResource brushes without
/// needing a parameter-parsing scheme. Used for the known-folder tiles'
/// selected-state Background/BorderBrush (bound to KnownFolderTile.IsSelected)
/// - see HomePage.xaml.cs's SelectTile, which is the only thing that
/// mutates IsSelected and therefore the only thing that re-triggers this
/// conversion via INotifyPropertyChanged.
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush? TrueBrush { get; set; }
    public Brush? FalseBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
