using System.Runtime.InteropServices;
using System.Text;

namespace Quickening.Core.Shell;

/// <summary>
/// Resolves the friendly name of the application Windows would launch for a
/// file's type (e.g. "Adobe Photoshop", "Photos"), so a confirmation prompt can
/// name the app the file is about to open in. Best-effort: returns null when no
/// handler is registered or the lookup fails, and the caller falls back to
/// generic wording ("its default app").
/// </summary>
public static class DefaultAppResolver
{
    private const int AssocfNone = 0;
    private const int AssocStrExecutable = 2; // ASSOCSTR_EXECUTABLE
    private const int AssocStrFriendlyAppName = 4; // ASSOCSTR_FRIENDLYAPPNAME
    private const int SOk = 0;

    // Shlwapi's association query. CharSet.Unicode binds the W variant, so the
    // returned name comes back as a Unicode string.
    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryString(
        int flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

    public static string? FriendlyAppName(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        // An extension with no registered handler still "resolves" - to the
        // Open With picker, whose friendly name is "Pick an app", which read
        // as `Open "report.bin" in Pick an app?` (QA-9). That isn't an app the
        // file will open in, so report no name and let the caller say
        // "its default app".
        var executable = Query(ext, AssocStrExecutable);
        if (executable is not null
            && Path.GetFileName(executable).Equals("OpenWith.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Query(ext, AssocStrFriendlyAppName);
    }

    private static string? Query(string ext, int str)
    {
        try
        {
            // Two-call idiom: the first call (null buffer) sets pcchOut to the
            // required length in characters; the second fills the buffer.
            uint length = 0;
            AssocQueryString(AssocfNone, str, ext, null, null, ref length);
            if (length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            var hr = AssocQueryString(AssocfNone, str, ext, null, buffer, ref length);
            if (hr != SOk)
            {
                return null;
            }

            var value = buffer.ToString().Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
