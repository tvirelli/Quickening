using Quickening.App.Formatting;
using Quickening.Core.Audio;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Quickening.Core.Orchestration;
using Xunit;

namespace Quickening.App.Tests;

public class ScanSummaryTextTests
{
    private static FileRecord F(string n) => new() { Path = $@"C:\x\{n}", SizeBytes = 10, Category = MimeCategory.Audio, LastWriteTimeUtc = DateTime.UtcNow };

    private static DuplicateGroup Dup() => new() { FullHash = new byte[] { 1 }, Files = new() { F("a.bin"), F("b.bin") } };

    private static SoundGroup Sound()
    {
        var s = new AcousticSignature(F("a.wav"), 200, new uint[] { 1 }, AudioFidelity.Create("a.wav", 16, 44100, 0));
        return new SoundGroup(new[] { s, s with { File = F("b.wav") } }, 94);
    }

    [Fact]
    public void Reassurance_WithOnlyExactDuplicates_KeepsTheOriginalWording()
    {
        var r = new ScanResult { DuplicateGroups = new[] { Dup() }, TotalFilesScanned = 2 };

        Assert.Equal("Recommended keeps the newest copy in every group. You'll still confirm before anything moves.", ScanSummaryText.Reassurance(r));
    }

    [Fact]
    public void Reassurance_ListsEveryKindOfCloseMatch()
    {
        var r = new ScanResult
        {
            DuplicateGroups = new[] { Dup() }, TotalFilesScanned = 9,
            SimilarityGroups = new[] { new Quickening.Core.Similarity.SimilarityGroup { Files = new() { F("p.jpg"), F("q.jpg") }, MatchPercent = 90 } },
            BlurryPhotos = new[] { F("b1.jpg"), F("b2.jpg") },
        };
        r.SoundGroups = new[] { Sound(), Sound() };

        Assert.Equal(
            "Recommended keeps the newest copy in every group. There are also 1 similar-photo group, 2 same-song groups and 2 blurry photos worth a look in Review Results.",
            ScanSummaryText.Reassurance(r));
    }

    [Fact]
    public void Reassurance_WithoutExactDuplicates_SaysSo()
    {
        var r = new ScanResult { DuplicateGroups = Array.Empty<DuplicateGroup>(), TotalFilesScanned = 22 };
        r.SoundGroups = Enumerable.Range(0, 11).Select(_ => Sound()).ToList();

        Assert.Equal("No exact copies here, but there are 11 same-song groups worth a look in Review Results.", ScanSummaryText.Reassurance(r));
    }

    [Fact]
    public void ResultsHeader_WithExactDuplicates_ShowsTheCounts()
    {
        Assert.Equal("6 duplicate groups · 13 files · 151 KB reclaimable", ScanSummaryText.ResultsHeader(6, 13, 151 * 1024, hasCloseMatches: true));
        Assert.Equal("1 duplicate group · 2 files · 40 B reclaimable", ScanSummaryText.ResultsHeader(1, 2, 40, hasCloseMatches: false));
    }

    [Fact]
    public void ResultsHeader_WithOnlyCloseMatches_DoesNotSayZero()
    {
        Assert.Equal("No exact duplicates · close matches below", ScanSummaryText.ResultsHeader(0, 0, 0, hasCloseMatches: true));
        Assert.Equal("0 duplicate groups · 0 files · 0 B reclaimable", ScanSummaryText.ResultsHeader(0, 0, 0, hasCloseMatches: false));
    }

    // Final review: filters that hide every duplicate group made the header say
    // "No exact duplicates" although the scan found some - say the filters did it.
    [Fact]
    public void ResultsHeader_WhenFiltersHideEveryDuplicateGroup_SaysSo()
    {
        Assert.Equal("No duplicates match your filters", ScanSummaryText.ResultsHeader(0, 0, 0, hasCloseMatches: true, totalGroupCount: 4));
        Assert.Equal("No duplicates match your filters", ScanSummaryText.ResultsHeader(0, 0, 0, hasCloseMatches: false, totalGroupCount: 4));
    }
}
