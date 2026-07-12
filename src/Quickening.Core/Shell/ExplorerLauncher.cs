using System.Diagnostics;
using static Vanara.PInvoke.Shell32;

namespace Quickening.Core.Shell;

/// <summary>
/// Opens an Explorer window with a specific file selected. Uses
/// SHOpenFolderAndSelectItems (PIDL-based, so the path is passed as an
/// opaque item, never parsed) rather than `explorer.exe /select,"path"`,
/// whose comma-delimited argument syntax truncates at any comma that
/// legally appears in a folder or file name and opens the wrong location.
/// </summary>
public static class ExplorerLauncher
{
    public static void SelectInExplorer(string filePath)
    {
        try
        {
            SHParseDisplayName(filePath, IntPtr.Zero, out var pidl, 0, out _);
            if (pidl is not null && !pidl.IsInvalid)
            {
                using (pidl)
                {
                    SHOpenFolderAndSelectItems(pidl, 0, null, OFASI.OFASI_NONE).ThrowIfFailed();
                    return;
                }
            }
        }
        catch
        {
            // Fall through to the plain-folder fallback below - failing to
            // preselect the file should still land the user in the right
            // folder rather than doing nothing.
        }

        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Process.Start("explorer.exe", $"\"{folder}\"");
        }
    }
}
