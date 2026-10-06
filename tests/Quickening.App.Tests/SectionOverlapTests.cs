using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.App.Tests;

/// <summary>
/// One physical file can sit in two results sections at once - in the QA arena
/// blurry-shot.png is both a Blurry photo and half of a similar-photo pair. Ticked
/// in both, it must count once in the footer and be recycled once (RC QA 4.2).
/// </summary>
public class SectionOverlapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qk-overlap-{Guid.NewGuid():N}");
    private readonly string _blurry;
    private readonly string _sharp;

    public SectionOverlapTests()
    {
        Directory.CreateDirectory(_dir);
        _blurry = Path.Combine(_dir, "blurry-shot.png");
        _sharp = Path.Combine(_dir, "sharp-keeper.png");
        File.WriteAllBytes(_blurry, new byte[300]);
        File.WriteAllBytes(_sharp, new byte[500]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Unlike FakeRecycleBinService this really removes the file, so the
    // "already recycled via another section" File.Exists skip is exercised.
    private sealed class DeletingRecycleBinService : IRecycleBinService
    {
        public bool AllowProtectedPaths { get; set; }
        public List<string> DeletedPaths { get; } = new();

        public void SendToRecycleBin(string path, long? expectedSizeBytes = null, DateTime? expectedLastWriteTimeUtc = null)
        {
            if (!File.Exists(path))
            {
                throw new IOException($"Already gone: {path}");
            }

            File.Delete(path);
            DeletedPaths.Add(path);
        }

        public (long ItemCount, long TotalSizeBytes) GetRecycleBinTotals() => (0, 0);
        public void EmptyRecycleBin() { }
        public bool TryRestore(string originalPath) => false;
    }

    private FileRecord Record(string path) => new()
    {
        Path = path,
        SizeBytes = new FileInfo(path).Length,
        LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
        Category = MimeCategory.Image,
    };

    private ResultsViewModel LoadOverlap(IRecycleBinService bin)
    {
        var viewModel = new ResultsViewModel(bin);
        viewModel.LoadSimilarityGroups(new[]
        {
            new SimilarityGroup { MatchPercent = 84, Files = new() { Record(_blurry), Record(_sharp) } },
        });
        viewModel.LoadBlurryPhotos(new[] { Record(_blurry) });
        viewModel.SimilarityGroups[0].Files.Single(f => f.Path == _blurry).IsSelected = true;
        viewModel.BlurryPhotos.Single().IsSelected = true;
        return viewModel;
    }

    [Fact]
    public void FileTickedInTwoSections_CountsOnceInSelectedSizeAndPaths()
    {
        var viewModel = LoadOverlap(new DeletingRecycleBinService());

        Assert.Equal(300, viewModel.GetSelectedSizeBytes());
        Assert.Equal(new[] { _blurry }, viewModel.GetSelectedFilePaths());
    }

    [Fact]
    public async Task FileTickedInTwoSections_IsRecycledOnce_NotReportedFailed_ProgressReachesTotal()
    {
        var bin = new DeletingRecycleBinService();
        var viewModel = LoadOverlap(bin);
        var reports = new List<DeleteProgress>();

        var failed = await viewModel.DeleteSelectedAsync(new SynchronousProgress(reports.Add));

        Assert.Empty(failed);
        Assert.Equal(new[] { _blurry }, bin.DeletedPaths);
        Assert.True(File.Exists(_sharp));
        Assert.Empty(viewModel.BlurryPhotos);
        var last = reports.Last();
        Assert.Equal(last.Total, last.Processed);
    }

    private sealed class SynchronousProgress(Action<DeleteProgress> handler) : IProgress<DeleteProgress>
    {
        public void Report(DeleteProgress value) => handler(value);
    }
}
