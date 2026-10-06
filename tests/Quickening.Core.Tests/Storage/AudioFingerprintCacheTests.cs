using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests.Storage;

public class AudioFingerprintCacheTests : IDisposable
{
    private readonly SqliteStore _store;

    public AudioFingerprintCacheTests()
    {
        _store = new SqliteStore("Data Source=:memory:");
        _store.Initialize();
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void UpsertThenGet_RoundTripsEveryField_CaseInsensitivePath()
    {
        var t = new DateTime(2026, 10, 6, 8, 8, 16, 123, DateTimeKind.Utc);
        var saved = new CachedAudioFingerprint(1234, t, 1, 228.8, 24, 48000, 2304, new uint[] { 0, 1, uint.MaxValue, 0xDEADBEEF });

        _store.UpsertAudioFingerprint(@"C:\m\Song.wav", saved);
        var loaded = _store.GetAudioFingerprint(@"c:\M\song.WAV");

        Assert.NotNull(loaded);
        Assert.Equal(saved with { Frames = loaded!.Frames }, loaded);
        Assert.Equal(saved.Frames, loaded.Frames);
        Assert.Equal(DateTimeKind.Utc, loaded.LastWriteTimeUtc.Kind);
    }

    [Fact]
    public void Upsert_ReplacesAStaleRow()
    {
        var t = DateTime.UtcNow;
        _store.UpsertAudioFingerprint(@"C:\m\a.wav", new CachedAudioFingerprint(1, t, 1, 10, 16, 44100, 0, new uint[] { 1 }));
        _store.UpsertAudioFingerprint(@"C:\m\a.wav", new CachedAudioFingerprint(2, t, 1, 10, 16, 44100, 0, new uint[] { 2 }));

        var loaded = _store.GetAudioFingerprint(@"C:\m\a.wav")!;
        Assert.Equal(2, loaded.SizeBytes);
        Assert.Equal(new uint[] { 2 }, loaded.Frames);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownPath()
    {
        Assert.Null(_store.GetAudioFingerprint(@"C:\nope.wav"));
    }
}
