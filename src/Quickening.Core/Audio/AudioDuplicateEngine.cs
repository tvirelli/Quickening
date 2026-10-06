using System.Text.RegularExpressions;
using Quickening.Core.Models;

namespace Quickening.Core.Audio;

/// <summary>
/// Groups audio files that are the same song encoded differently (F10) - same
/// title + artist + (near-)duration, regardless of format or bitrate. This is
/// deliberately distinct from byte-identical duplicates (DuplicateEngine): two
/// copies of a track at 128 vs 320 kbps aren't byte-identical but ARE the same
/// song. Pure grouping over already-read tags, so it stays unit-testable.
/// </summary>
public sealed class AudioDuplicateEngine
{
    /// <summary>One encoding of a song: the file plus the bitrate/duration used
    /// to pick the "best" copy (highest bitrate, then largest file).</summary>
    public sealed record SongCopy(FileRecord File, int BitrateKbps, int DurationSeconds);

    /// <summary>A set of two or more files that are the same song.</summary>
    public sealed record MusicGroup(string SongLabel, IReadOnlyList<SongCopy> Copies);

    // Copies of the same track differ in duration only by encoder padding (a
    // second or two); a genuinely different version (a 3-min single vs a 6-min
    // extended mix) differs by far more. Cluster copies whose durations are
    // within this tolerance rather than bucketing on a fixed boundary (which
    // would split a 200s and a 201s copy into adjacent buckets).
    private const int DurationToleranceSeconds = 15;

    public IReadOnlyList<MusicGroup> FindDuplicateSongs(
        IReadOnlyList<(FileRecord File, AudioInfo Info)> audioFiles,
        CancellationToken cancellationToken = default)
    {
        var byTitleArtist = new Dictionary<string, List<(FileRecord File, AudioInfo Info)>>();
        foreach (var entry in audioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Guards against tag-poor false positives:
            // - No usable duration: unknown-length files would all land in the
            //   same duration run and "match" on nothing but their title.
            // - Generic title with NO artist: "Track 01" etc. is a CD-rip
            //   default, not a song identity - two different albums' untagged
            //   rips share it. With an artist present the title+artist key is
            //   discriminating enough to keep.
            if (entry.Info.DurationSeconds <= 0)
            {
                continue;
            }

            var title = Normalize(entry.Info.Title);
            var artist = Normalize(entry.Info.Artist);
            if (artist.Length == 0 && GenericTitle.IsMatch(title))
            {
                continue;
            }

            var key = title + "|" + artist;
            if (!byTitleArtist.TryGetValue(key, out var list))
            {
                byTitleArtist[key] = list = new List<(FileRecord, AudioInfo)>();
            }

            list.Add(entry);
        }

        var groups = new List<MusicGroup>();
        foreach (var members in byTitleArtist.Values)
        {
            if (members.Count < 2)
            {
                continue;
            }

            // Within one title+artist, split by duration: sort, then start a
            // new run whenever a copy is more than the tolerance away from the
            // RUN'S FIRST member (its anchor) - not from its immediate
            // predecessor. Consecutive-gap chaining let 200/214/228/242s copies
            // bridge into one "same song" with a 42s total spread, quietly
            // merging a radio edit into an album version.
            var sorted = members.OrderBy(m => m.Info.DurationSeconds).ToList();
            var run = new List<(FileRecord File, AudioInfo Info)> { sorted[0] };
            for (var i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Info.DurationSeconds - run[0].Info.DurationSeconds <= DurationToleranceSeconds)
                {
                    run.Add(sorted[i]);
                }
                else
                {
                    EmitGroup(run, groups);
                    run = new List<(FileRecord File, AudioInfo Info)> { sorted[i] };
                }
            }

            EmitGroup(run, groups);
        }

        return groups.OrderBy(g => g.SongLabel, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void EmitGroup(List<(FileRecord File, AudioInfo Info)> run, List<MusicGroup> groups)
    {
        if (run.Count < 2)
        {
            return;
        }

        var copies = run
            .Select(m => new SongCopy(m.File, m.Info.BitrateKbps, m.Info.DurationSeconds))
            .OrderByDescending(c => c.BitrateKbps)
            .ThenByDescending(c => c.File.SizeBytes)
            .ToList();

        var first = run[0].Info;
        var label = string.IsNullOrWhiteSpace(first.Artist) ? first.Title : $"{first.Artist} — {first.Title}";
        groups.Add(new MusicGroup(label, copies));
    }

    // Case/whitespace-insensitive so "  The  Song " and "the song" match.
    private static string Normalize(string value) =>
        Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");

    // CD-rip / recorder default titles ("Track 01", "AudioTrack 03", "Piste 2",
    // "01", …) - a bare number optionally prefixed by a "track" word in the
    // languages rippers commonly emit. Matched against the NORMALIZED title.
    private static readonly Regex GenericTitle = new(
        @"^(track|audiotrack|audio track|title|titel|piste|pista|faixa|traccia)?[ ._-]*\d+$",
        RegexOptions.Compiled);
}
