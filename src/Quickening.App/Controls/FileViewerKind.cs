using Quickening.Core.Models;

namespace Quickening.App.Controls;

public enum FileViewerKind { Image, Video, Audio, Code, PlainText, Markdown, Pdf, None }

/// <summary>
/// Chooses which compare-viewer tile to build for a file. Media categories win
/// outright; everything else is routed by file extension. Unknown / proprietary
/// types (.psd, .ai, binaries) get None ("no preview - open externally").
/// </summary>
public static class FileViewerRouter
{
    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".ts", ".jsx", ".tsx", ".cs", ".php", ".css", ".less", ".scss", ".html", ".htm",
        ".xml", ".json", ".yaml", ".yml", ".py", ".sql", ".sh", ".ps1", ".bat", ".c", ".cpp",
        ".h", ".hpp", ".java", ".go", ".rs", ".rb", ".swift", ".kt", ".lua", ".pl", ".r",
        ".ini", ".toml", ".gradle", ".vue", ".svelte",
    };

    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".cfg", ".conf", ".env", ".text",
    };

    public static FileViewerKind ForPath(string path, MimeCategory category)
    {
        switch (category)
        {
            case MimeCategory.Image: return FileViewerKind.Image;
            case MimeCategory.Video: return FileViewerKind.Video;
            case MimeCategory.Audio: return FileViewerKind.Audio;
        }

        var ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FileViewerKind.Pdf;
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)) return FileViewerKind.Markdown;
        if (Code.Contains(ext)) return FileViewerKind.Code;
        if (PlainText.Contains(ext)) return FileViewerKind.PlainText;
        return FileViewerKind.None;
    }
}
