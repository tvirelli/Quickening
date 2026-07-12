using Quickening.Core.Hashing;
using Xunit;

namespace Quickening.Core.Tests.Hashing;

public class HashProviderTests
{
    [Fact]
    public void ComputeFullHash_OfEmptyFile_MatchesKnownBlake3TestVector()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, Array.Empty<byte>());
            var provider = new HashProvider();

            var hash = provider.ComputeFullHash(tempFile);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();

            // Published BLAKE3 test vector for zero-length input.
            Assert.Equal(
                "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
                hex);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeFullHash_IsIdentical_ForIdenticalContent()
    {
        var fileA = System.IO.Path.GetTempFileName();
        var fileB = System.IO.Path.GetTempFileName();
        try
        {
            var content = "the quick brown fox"u8.ToArray();
            File.WriteAllBytes(fileA, content);
            File.WriteAllBytes(fileB, content);
            var provider = new HashProvider();

            Assert.Equal(provider.ComputeFullHash(fileA), provider.ComputeFullHash(fileB));
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public void ComputeFullHash_Differs_ForDifferentContent()
    {
        var fileA = System.IO.Path.GetTempFileName();
        var fileB = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(fileA, "content one"u8.ToArray());
            File.WriteAllBytes(fileB, "content two"u8.ToArray());
            var provider = new HashProvider();

            Assert.NotEqual(provider.ComputeFullHash(fileA), provider.ComputeFullHash(fileB));
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public void ComputePartialHash_IsIdentical_ForFilesUnder64KbWithSameContent()
    {
        var fileA = System.IO.Path.GetTempFileName();
        var fileB = System.IO.Path.GetTempFileName();
        try
        {
            var content = "short file content"u8.ToArray();
            File.WriteAllBytes(fileA, content);
            File.WriteAllBytes(fileB, content);
            var provider = new HashProvider();

            Assert.Equal(provider.ComputePartialHash(fileA), provider.ComputePartialHash(fileB));
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public void ComputePartialHash_Differs_ForDifferentSmallContent()
    {
        var fileA = System.IO.Path.GetTempFileName();
        var fileB = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(fileA, "content one"u8.ToArray());
            File.WriteAllBytes(fileB, "content two"u8.ToArray());
            var provider = new HashProvider();

            Assert.NotEqual(provider.ComputePartialHash(fileA), provider.ComputePartialHash(fileB));
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public void ComputePartialHash_Differs_WhenOnlyTailBytesDiffer()
    {
        var fileA = System.IO.Path.GetTempFileName();
        var fileB = System.IO.Path.GetTempFileName();
        try
        {
            // Identical first 64KB, but each file has an extra 1KB tail that differs.
            var head = new byte[64 * 1024];
            for (var i = 0; i < head.Length; i++)
            {
                head[i] = (byte)(i % 256);
            }

            var contentA = new byte[head.Length + 1024];
            head.CopyTo(contentA, 0);
            Array.Fill(contentA, (byte)0xAA, head.Length, 1024);

            var contentB = new byte[head.Length + 1024];
            head.CopyTo(contentB, 0);
            Array.Fill(contentB, (byte)0xBB, head.Length, 1024);

            File.WriteAllBytes(fileA, contentA);
            File.WriteAllBytes(fileB, contentB);
            var provider = new HashProvider();

            Assert.NotEqual(provider.ComputePartialHash(fileA), provider.ComputePartialHash(fileB));
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public void ComputePartialHash_IsIdentical_ForIdenticalFilesOver128Kb()
    {
        var fileA = System.IO.Path.GetTempFileName();
        var fileB = System.IO.Path.GetTempFileName();
        try
        {
            // Large enough that head and tail samples definitely don't overlap.
            var content = new byte[200 * 1024];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i % 256);
            }

            File.WriteAllBytes(fileA, content);
            File.WriteAllBytes(fileB, content);
            var provider = new HashProvider();

            Assert.Equal(provider.ComputePartialHash(fileA), provider.ComputePartialHash(fileB));
        }
        finally
        {
            File.Delete(fileA);
            File.Delete(fileB);
        }
    }

    [Fact]
    public void ComputeFullHash_Throws_WhenCancellationAlreadyRequested()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "some content"u8.ToArray());
            var provider = new HashProvider();
            var cancelledToken = new CancellationToken(canceled: true);

            Assert.Throws<OperationCanceledException>(
                () => provider.ComputeFullHash(tempFile, cancelledToken));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputeFullHash_Throws_WhenCancellationAlreadyRequested_ForEmptyFile()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, Array.Empty<byte>());
            var provider = new HashProvider();
            var cancelledToken = new CancellationToken(canceled: true);

            Assert.Throws<OperationCanceledException>(
                () => provider.ComputeFullHash(tempFile, cancelledToken));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ComputePartialHash_Throws_WhenCancellationAlreadyRequested()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, "some content"u8.ToArray());
            var provider = new HashProvider();
            var cancelledToken = new CancellationToken(canceled: true);

            Assert.Throws<OperationCanceledException>(
                () => provider.ComputePartialHash(tempFile, cancelledToken));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
