using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Quickening.Core.Deletion;
using Quickening.Core.Scanning;

namespace Quickening.App.ViewModels;

/// <summary>
/// One tidy-up candidate (F5): an empty folder or a zero-byte file. Not a
/// record so mutating IsSelected raises PropertyChanged and the row's bound
/// checkbox stays in sync when Select-all/none flips it.
/// </summary>
public sealed class HousekeepingItem : INotifyPropertyChanged
{
    private bool _isSelected = true; // Pre-checked: these are empty/zero-byte, the whole point is to sweep them.

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Path { get; init; }
    public required bool IsFolder { get; init; }

    // "folder-name" (trailing separators trimmed) and its parent directory, for
    // the two-line row (name in bold over a muted path), same as the file rows
    // elsewhere in the app.
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public string ParentDirectory => System.IO.Path.GetDirectoryName(Path) ?? Path;

    // A plain emoji glyph rather than the shell/category icon machinery the file
    // rows use - these are empty folders and zero-byte files, so there's nothing
    // to thumbnail and the kind is all that matters.
    public string Glyph => IsFolder ? "\U0001F4C1" : "\U0001F4C4"; // 📁 : 📄

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>
/// Backs HousekeepingPage (F5): the empty folders and zero-byte files a scan
/// turned up, plus their removal to the Recycle Bin. Removal re-verifies each
/// folder is STILL empty at delete time (a file could have landed there since
/// the scan) and each zero-byte file is STILL zero bytes, so tidying can never
/// sweep away real data that appeared after the scan snapshot.
/// </summary>
public sealed class HousekeepingViewModel
{
    private readonly IRecycleBinService _recycleBinService;

    public ObservableCollection<HousekeepingItem> EmptyFolders { get; } = new();
    public ObservableCollection<HousekeepingItem> ZeroByteFiles { get; } = new();

    public HousekeepingViewModel(IRecycleBinService recycleBinService)
    {
        _recycleBinService = recycleBinService;
    }

    public void Load(EmptyItemScanner.Result result)
    {
        EmptyFolders.Clear();
        ZeroByteFiles.Clear();
        foreach (var folder in result.EmptyFolders)
        {
            EmptyFolders.Add(new HousekeepingItem { Path = folder, IsFolder = true });
        }

        foreach (var file in result.ZeroByteFiles)
        {
            ZeroByteFiles.Add(new HousekeepingItem { Path = file, IsFolder = false });
        }
    }

    public IEnumerable<HousekeepingItem> AllItems => EmptyFolders.Concat(ZeroByteFiles);

    public int SelectedCount => AllItems.Count(i => i.IsSelected);

    public void SelectAll()
    {
        foreach (var item in AllItems) item.IsSelected = true;
    }

    public void SelectNone()
    {
        foreach (var item in AllItems) item.IsSelected = false;
    }

    /// <summary>
    /// Recycles every selected item. Deepest paths go first so a parent empty
    /// folder recycled earlier can't strand a still-listed child (recycling a
    /// folder takes its subtree with it - a child already gone that way is
    /// counted as succeeded, not failed). Folders that gained content, and
    /// files no longer zero bytes, are refused (added to the failed list)
    /// rather than removed. Returns the paths that could not be recycled.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteSelectedAsync(
        List<HousekeepingItem> selected,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ordered = selected.OrderByDescending(i => i.Path.Length).ToList();
        var failed = new List<string>();
        var processed = 0;

        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (item.IsFolder)
                {
                    if (!Directory.Exists(item.Path))
                    {
                        // Already gone - almost certainly recycled as part of a
                        // parent folder processed earlier. Treat as success.
                    }
                    else if (IsReparsePoint(item.Path))
                    {
                        // A junction/symlink posing as an empty folder.
                        // RecycleBinService already rejects reparse ANCESTORS;
                        // this covers the item itself - deleting a link a scan
                        // classified as "empty" is not what the user reviewed.
                        failed.Add(item.Path);
                    }
                    else if (!IsStillEmptyFolder(item.Path))
                    {
                        failed.Add(item.Path); // gained content since the scan
                    }
                    else
                    {
                        await Task.Run(() => _recycleBinService.SendToRecycleBin(item.Path), cancellationToken);
                    }
                }
                else if (File.Exists(item.Path))
                {
                    // expectedSizeBytes: 0 makes SendToRecycleBin refuse the file
                    // if it's no longer zero bytes.
                    await Task.Run(() => _recycleBinService.SendToRecycleBin(item.Path, expectedSizeBytes: 0), cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(item.Path);
            }

            processed++;
            progress?.Report(new DeleteProgress(processed, ordered.Count, item.Path));
        }

        return failed;
    }

    // A folder still counts as empty if it holds no files and only (recursively)
    // empty subfolders - the same rule EmptyItemScanner used to list it. Any
    // read failure returns false so a folder we can't fully inspect is never
    // swept. Best-effort and shallow-cheap: most empty folders have nothing.
    // Fails CLOSED (treats an unreadable item as a reparse point) - if the
    // attributes can't be read, don't recycle it.
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsStillEmptyFolder(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                _ = file;
                return false;
            }

            foreach (var sub in Directory.EnumerateDirectories(path))
            {
                if (!IsStillEmptyFolder(sub)) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
