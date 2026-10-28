using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Quickening.App.Controls;

/// <summary>One row of the Keep overrides list. Rank is 1-based and follows list order.</summary>
public sealed class KeepOverrideFolder : INotifyPropertyChanged
{
    private int _rank;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Path { get; init; }

    // "Keepers" for C:\...\Keepers; a drive root ("D:\") has no name, so the path itself.
    public string Name
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    public string RemoveLabel => $"Remove {Path} from keep overrides";

    public int Rank
    {
        get => _rank;
        set
        {
            if (_rank != value)
            {
                _rank = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rank)));
            }
        }
    }
}

/// <summary>
/// Edits App.Settings.KeepOverrideFolderPaths: add (folder picker), remove,
/// and drag to reorder. Every change saves immediately, like the rest of
/// Settings. KeeperChooser reads the list in order, so the top row wins.
/// </summary>
public sealed partial class KeepOverridesEditor : UserControl
{
    private readonly ObservableCollection<KeepOverrideFolder> _folders = new();

    public KeepOverridesEditor()
    {
        InitializeComponent();

        foreach (var path in App.Settings.KeepOverrideFolderPaths)
        {
            _folders.Add(new KeepOverrideFolder { Path = path });
        }

        FolderList.ItemsSource = _folders;
        // Fires once a drag reorder has landed (the ObservableCollection has
        // already been rearranged by then), so this is the one place to save.
        FolderList.DragItemsCompleted += (_, _) => Commit();
        Renumber();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var folder = await Views.SettingsDialog.PickFolderAsync();
        if (folder is null || _folders.Any(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _folders.Add(new KeepOverrideFolder { Path = folder });
        Commit();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && _folders.FirstOrDefault(f => f.Path == path) is { } folder)
        {
            _folders.Remove(folder);
            Commit();
        }
    }

    private void Commit()
    {
        App.Settings.KeepOverrideFolderPaths = _folders.Select(f => f.Path).ToList();
        App.SaveSettings();
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < _folders.Count; i++)
        {
            _folders[i].Rank = i + 1;
        }

        var hasFolders = _folders.Count > 0;
        EmptyText.Visibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
        FolderList.Visibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
    }
}
