using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Quickening.Core.Models;

namespace Quickening.App.Converters;

/// <summary>
/// Maps a file's MimeCategory to the matching Icons.xaml (Task 2) DataTemplate
/// key for its Results-row icon fallback - shown whenever
/// SelectableFile.IsImage is false (every non-Image row, or an Image row
/// whose thumbnail decode failed - see ReportThumbnailFailed). Only five
/// per-category templates exist (IconFileImage/Video/Archive/Executable/
/// Generic) - Audio/Document/Other all share IconFileGeneric, matching
/// Icons.xaml's own comment on that template's judgment call.
/// </summary>
public sealed class CategoryIconTemplateConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var key = (value as MimeCategory?) switch
        {
            MimeCategory.Image => "IconFileImage",
            MimeCategory.Video => "IconFileVideo",
            MimeCategory.Archive => "IconFileArchive",
            MimeCategory.Executable => "IconFileExecutable",
            _ => "IconFileGeneric",
        };

        return Application.Current.Resources[key] as DataTemplate;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a file's MimeCategory to its Results-row icon-container tint
/// (screen 2g/2h's 44x44 rounded box behind the fallback icon) - reuses
/// Theme.xaml's existing Category*FillBrush tokens (Task 1) rather than a
/// one-off literal, since those tokens already carry the right hue at a
/// tile/thumb-fill alpha.
/// </summary>
public sealed class CategoryFillBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var key = (value as MimeCategory?) switch
        {
            MimeCategory.Image => "CategoryImageFillBrush",
            MimeCategory.Video => "CategoryVideoFillBrush",
            MimeCategory.Audio => "CategoryAudioFillBrush",
            MimeCategory.Document => "CategoryDocumentFillBrush",
            MimeCategory.Archive => "CategoryArchiveFillBrush",
            MimeCategory.Executable => "CategoryExecutableFillBrush",
            _ => "CategoryOtherFillBrush",
        };

        return Application.Current.Resources[key] as SolidColorBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
