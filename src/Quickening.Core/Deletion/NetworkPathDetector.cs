using System.Runtime.InteropServices;

namespace Quickening.Core.Deletion;

/// <summary>
/// Shared by RecycleBinService.SendToRecycleBin (which refuses to delete a
/// network-located file - see its own doc comment) and Results/Large Files'
/// per-row "NETWORK DRIVE" treatment (new-screens 4i) - both need to answer
/// the same question about the same path, so this is a standalone static
/// helper rather than duplicated logic in each caller.
/// </summary>
public static class NetworkPathDetector
{
    private const uint DRIVE_REMOTE = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable path can't be on a network drive as far as this
            // check is concerned - let the caller's own validation (if any)
            // decide what to do with a malformed path.
            return false;
        }

        return !string.IsNullOrEmpty(root) && GetDriveTypeW(root) == DRIVE_REMOTE;
    }
}
