using Quickening.Core.Models;

namespace Quickening.App.Formatting;

/// <summary>
/// Formats a category breakdown row's label (e.g. "Document  2 MB") for
/// display. Shared by the Scan-Completed donut chart's legend and the
/// Results sidebar so the two screens can never drift apart on wording.
/// </summary>
public static class CategoryLabelFormatter
{
    public static string Format(MimeCategory category, long bytes) =>
        $"{category}  {FileSizeFormatter.Format(bytes)}";

    /// <summary>
    /// The plural category name used by both the Scan-Completed donut
    /// legend (screen 2e) and segmented-bar legend (screen 2f) - e.g.
    /// "Images", "Documents" - read directly off those screens'
    /// markup rather than a naive category.ToString() + "s".
    /// </summary>
    public static string PluralName(MimeCategory category) => category switch
    {
        MimeCategory.Image => "Images",
        MimeCategory.Video => "Videos",
        MimeCategory.Audio => "Audio",
        MimeCategory.Document => "Documents",
        MimeCategory.Archive => "Archives",
        MimeCategory.Executable => "Executables",
        MimeCategory.Other => "Other",
        _ => category.ToString(),
    };
}
