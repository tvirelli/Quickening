namespace Quickening.Core.Hashing;

public interface IHashProvider
{
    byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default);
    byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default);
}
