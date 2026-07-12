using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Quickening.App.Formatting;
using Quickening.Core.Safety;

namespace Quickening.App.Converters;

/// <summary>
/// Per-file-row display converters for the Results grouped list (screen 2g).
/// Each takes a raw SelectableFile property value (Path/SizeBytes/
/// LastWriteTimeUtc) rather than the whole file object, so they're reusable
/// as-is by Task 9's flat Large Files list, which shares the same
/// SelectableFile row data.
/// </summary>
public sealed class FileNameFromPathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string path ? Path.GetFileName(path) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// The directory portion of a file's path, with a trailing backslash to
/// match screen 2g's row subtitle (e.g. "C:\Users\you\Downloads\camera roll\").
/// </summary>
public sealed class DirectoryFromPathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path)
        {
            return string.Empty;
        }

        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? string.Empty : directory + "\\";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// "Jun 28, 2026" - screen 2g's exact modified-date format, converted to
/// local time so the date shown matches what Explorer (and the History
/// screen, which already converts) show for the same file - raw UTC could
/// disagree with both by a day near midnight.
/// </summary>
public sealed class LastWriteDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTime dateTime
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, yyyy")
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is long bytes ? FileSizeFormatter.Format(bytes) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Drives the "⚠ PROGRAM FILE" risky-file pill (screens 2g/2h) from a row's
/// raw Path - Visible only when RiskyExtensions.IsRisky matches (VM disks,
/// databases, backups, encrypted containers, game saves, executables/
/// installers - see RiskyExtensions), the same check ResultsViewModel/
/// LargeFilesResultsViewModel use to gate the delete-confirmation dialog's
/// extra acknowledgment step, so a flagged row and a flagged confirm-dialog
/// entry are always the exact same set of files.
/// </summary>
public sealed class RiskyPathVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string path && RiskyExtensions.IsRisky(path) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
