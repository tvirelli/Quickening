using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Quickening.App.Services;

/// <summary>
/// Produces the real Windows shell icon for a file type (the icon Explorer shows),
/// so non-media result rows read as their actual type instead of a generic
/// category glyph. Uses SHGetFileInfo (by extension, no disk access) to get the
/// type's icon, then extracts its pixels with GDI (GetIconInfo + GetDIBits) into a
/// WriteableBitmap. All synchronous on the caller's (UI) thread and cached per
/// extension - the earlier attempts failed because System.Drawing on a threadpool
/// thread was unreliable, and WinRT GetThumbnailAsync(ListView) throws E_PENDING
/// for many types. Any failure returns null and the caller keeps the glyph.
/// </summary>
public static class ShellIconProvider
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    [DllImport("User32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("User32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("Gdi32.dll")] private static extern int GetObject(IntPtr h, int c, out BITMAP bm);
    [DllImport("Gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("Gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("User32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("User32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIconSource(string path)
    {
        var key = Path.GetExtension(path);
        if (string.IsNullOrEmpty(key))
        {
            key = " ";
        }

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? source = null;
        try
        {
            source = RenderIcon(key);
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Shell icon load failed for '{path}': {ex}");
        }

        // Cache even a null so a genuinely icon-less type isn't retried on every
        // row; per-extension, so one failure can't affect other types.
        Cache[key] = source;
        return source;
    }

    private static ImageSource? RenderIcon(string ext)
    {
        var info = new SHFILEINFO();
        // A path is only needed for its extension; USEFILEATTRIBUTES means the
        // file need not exist and the disk is never touched.
        SHGetFileInfo("f" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!GetIconInfo(info.hIcon, out var iconInfo))
            {
                return null;
            }

            try
            {
                if (GetObject(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var bitmap) == 0)
                {
                    return null;
                }

                var width = bitmap.bmWidth;
                var height = bitmap.bmHeight;
                var header = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                };

                var pixels = new byte[width * height * 4];
                var dc = GetDC(IntPtr.Zero);
                var scanned = GetDIBits(dc, iconInfo.hbmColor, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS);
                ReleaseDC(IntPtr.Zero, dc);
                if (scanned == 0)
                {
                    return null;
                }

                PremultiplyAlpha(pixels);

                var writeable = new WriteableBitmap(width, height);
                using (var stream = writeable.PixelBuffer.AsStream())
                {
                    stream.Write(pixels, 0, pixels.Length);
                }

                return writeable;
            }
            finally
            {
                DeleteObject(iconInfo.hbmColor);
                DeleteObject(iconInfo.hbmMask);
            }
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    // GDI hands back straight (non-premultiplied) BGRA; WriteableBitmap expects
    // premultiplied. Also fixes the rare legacy icon whose color bitmap carries
    // no alpha channel (all zero) by treating it as fully opaque.
    private static void PremultiplyAlpha(byte[] bgra)
    {
        long alphaSum = 0;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            alphaSum += bgra[i];
        }

        if (alphaSum == 0)
        {
            for (var i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }
            return;
        }

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }
}
