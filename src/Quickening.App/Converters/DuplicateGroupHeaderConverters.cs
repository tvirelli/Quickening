using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;

namespace Quickening.App.Converters;

/// <summary>
/// Group-header derived display values for a Results (Duplicates mode) group
/// card (screen 2g) - each of these four converters reads directly off the
/// group's live Files collection rather than a separately-maintained string,
/// so a card can never show a stale count/size if a file is later removed
/// from the group (e.g. by ResultsViewModel.DeleteSelectedAsync's per-file
/// removal, or a filter narrowing the visible set in ApplyFilters).
/// Deliberately NOT added as computed properties on DuplicateGroupViewModel
/// itself (ResultsViewModel.cs is out of scope for Task 8 - see that task's
/// file list) - these converters get the same derived values purely from
/// XAML bindings against the existing Files collection instead.
/// </summary>
public sealed class GroupSwatchBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) =>
        (value as ObservableCollection<SelectableFile>)?.FirstOrDefault()?.CategoryBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// The group card header's representative filename (screen 2g shows the
/// KEPT file's own name here, not an arbitrary group member - group 1's
/// header reads "IMG_8842.jpg", matching its KEEP row's filename exactly,
/// not "IMG_8842 (1).jpg" or "IMG_8842 copy.jpg"). Falls back to the first
/// file only in the defensive case where no file is flagged (should not
/// happen - ResultsViewModel.ApplyFilters always marks exactly one file per
/// visible group as IsKeepRecommended).
/// </summary>
public sealed class GroupRepresentativeFileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var files = value as ObservableCollection<SelectableFile>;
        var representative = files?.FirstOrDefault(f => f.IsKeepRecommended) ?? files?.FirstOrDefault();
        return representative is null ? string.Empty : Path.GetFileName(representative.Path);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// "N identical copies · size each" - exact wording from screen 2g, computed
/// from the group's own Files rather than DuplicateGroupViewModel.GroupLabel
/// (whose existing "N copies - size each" wording is a pre-existing,
/// unrelated string this task doesn't touch).
/// </summary>
public sealed class GroupCopiesLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var files = value as ObservableCollection<SelectableFile>;
        if (files is null || files.Count == 0)
        {
            return string.Empty;
        }

        return $"{files.Count} identical copies · {FileSizeFormatter.Format(files[0].SizeBytes)} each";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// "frees X" - every file in the group except the one being kept, per
/// screen 2g. All files in a duplicate group are byte-identical, so any
/// member's SizeBytes is "size each".
/// </summary>
public sealed class GroupFreesLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var files = value as ObservableCollection<SelectableFile>;
        if (files is null || files.Count < 2)
        {
            return string.Empty;
        }

        var freedBytes = (long)(files.Count - 1) * files[0].SizeBytes;
        return $"frees {FileSizeFormatter.Format(freedBytes)}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
