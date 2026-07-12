using System.Buffers;
using System.IO.Hashing;
using Blake3;

namespace Quickening.Core.Hashing;

public sealed class HashProvider : IHashProvider
{
    private const int SampleSize = 64 * 1024; // 64KB
    private const int StreamBufferSize = 81920;

    public byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = File.OpenRead(path);
        var length = stream.Length;

        var headBuffer = new byte[Math.Min(SampleSize, length)];
        stream.ReadExactly(headBuffer);

        byte[] tailBuffer = Array.Empty<byte>();
        if (length > SampleSize)
        {
            // Clamp so the tail read never re-reads bytes already captured by the head.
            var tailSize = (int)Math.Min(SampleSize, length - SampleSize);
            if (tailSize > 0)
            {
                tailBuffer = new byte[tailSize];
                stream.Seek(-tailSize, SeekOrigin.End);
                stream.ReadExactly(tailBuffer);
            }
        }

        var combined = new byte[headBuffer.Length + tailBuffer.Length + sizeof(long)];
        headBuffer.CopyTo(combined, 0);
        tailBuffer.CopyTo(combined, headBuffer.Length);
        BitConverter.GetBytes(length).CopyTo(combined, headBuffer.Length + tailBuffer.Length);

        return XxHash128.Hash(combined);
    }

    public byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = File.OpenRead(path);
        using var hasher = Hasher.New();

        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.Update(buffer.AsSpan(0, bytesRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var hash = hasher.Finalize();
        return hash.AsSpan().ToArray();
    }
}
