using Quickening.App.Media;
using Quickening.Core.Audio;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.App.Tests;

public class AudioFingerprintServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qk-afs-{Guid.NewGuid():N}");

    public AudioFingerprintServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static FileRecord Record(string path) => new()
    {
        Path = path,
        SizeBytes = new FileInfo(path).Length,
        Category = MimeCategory.Audio,
        LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
    };

    // 44.1 kHz stereo 16-bit WAV of a tone melody, written by hand.
    private static string WriteWav(string path, double seconds)
    {
        const int rate = 44100;
        var n = (int)(seconds * rate);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + n * 4);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(n * 4);
        var rng = new Random(3);
        var freqs = Enumerable.Range(0, (int)(seconds * 4) + 1).Select(_ => 300 + rng.NextDouble() * 1500).ToArray();
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)rate;
            var s = (short)(Math.Sin(2 * Math.PI * freqs[(int)(t * 4)] * t) * 9000);
            w.Write(s);
            w.Write(s);
        }

        return path;
    }

    [Fact]
    public async Task TryGetSignatureAsync_DecodesAWav_ToTheFingerprintWindow()
    {
        var path = WriteWav(Path.Combine(_dir, "tone.wav"), 30);

        var signature = await AudioFingerprintService.TryGetSignatureAsync(Record(path), store: null, CancellationToken.None);

        Assert.NotNull(signature);
        Assert.InRange(signature!.DurationSeconds, 29.5, 30.5);
        Assert.InRange(signature.Frames.Length, (int)(29 * AcousticFingerprinter.FramesPerSecond), (int)(31 * AcousticFingerprinter.FramesPerSecond));
        Assert.Equal("WAV · 16-bit · 44.1 kHz", signature.Fidelity.Label);
    }

    [Fact]
    public async Task TryGetSignatureAsync_ReturnsNull_ForAFileThatIsNotAudio()
    {
        var path = Path.Combine(_dir, "fake.mp3");
        File.WriteAllText(path, "not audio at all");

        Assert.Null(await AudioFingerprintService.TryGetSignatureAsync(Record(path), store: null, CancellationToken.None));
    }
}
