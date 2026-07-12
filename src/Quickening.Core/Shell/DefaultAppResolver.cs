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

        try
        {
            // Two-call idiom: the first call (null buffer) sets pcchOut to the
            // required length in characters; the second fills the buffer.
            uint length = 0;
            AssocQueryString(AssocfNone, AssocStrFriendlyAppName, ext, null, null, ref length);
            if (length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            var hr = AssocQueryString(AssocfNone, AssocStrFriendlyAppName, ext, null, buffer, ref length);
            if (hr != SOk)
            {
                return null;
            }

            var name = buffer.ToString().Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
