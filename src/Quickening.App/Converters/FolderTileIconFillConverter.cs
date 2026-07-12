using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Quickening.App.Converters;

/// <summary>
/// Maps a KnownFolderTile.IconTemplateKey (e.g. "IconDesktop") to the
/// tinted 44x44 icon-container background screen 2a actually uses behind
/// each folder icon - a per-folder rgba tint at its own specific alpha
/// (0.12-0.18), distinct from (and not always the same alpha as)
/// Theme.xaml's Category*FillBrush tokens, which Task 1/2 derived from
/// different screens (Results-row chips, not these Home tiles). Values
/// below are transcribed directly from
/// design-handoff/Quickening Screens.dc.html screen 2a's six icon-box
/// <div style="background: rgba(...)"> declarations rather than
/// approximated from the nearest existing token, per Task 4's
/// design-fidelity requirement. KnownFolderTile itself can't carry this
/// as a bound property (its shape is fixed by the implementation plan),
/// so the lookup lives here instead.
/// </summary>
public sealed class FolderTileIconFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = (value as string) switch
        {
            "IconDesktop" => Color(0x24, 0x40, 0xC4, 0xFF),    // rgba(64,196,255,0.14)
            "IconDocuments" => Color(0x21, 0xFF, 0xD6, 0x66),  // rgba(255,214,102,0.13)
            "IconDownloads" => Color(0x2E, 0x7F, 0xAD, 0xFF),  // rgba(127,173,255,0.18)
            "IconPictures" => Color(0x21, 0xFF, 0x7A, 0xB6),   // rgba(255,122,182,0.13)
            "IconVideos" => Color(0x24, 0x8C, 0x6E, 0xFF),     // rgba(140,110,255,0.14)
            "IconMusic" => Color(0x1F, 0x5E, 0xE7, 0xB7),      // rgba(94,231,183,0.12)
            _ => Colors.Transparent,
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) =>
        Windows.UI.Color.FromArgb(a, r, g, b);
}
