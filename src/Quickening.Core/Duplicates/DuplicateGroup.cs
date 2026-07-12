using Quickening.Core.Models;

namespace Quickening.Core.Duplicates;

public sealed class DuplicateGroup
{
    public required byte[] FullHash { get; init; }
    public required List<FileRecord> Files { get; init; }
}
