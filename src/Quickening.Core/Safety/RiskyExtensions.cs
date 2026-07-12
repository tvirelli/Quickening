namespace Quickening.Core.Safety;

/// <summary>
/// Soft warning, distinct from <see cref="HardBlockRules"/>: files matching
/// this list are still shown in results and can still be deleted, but the
/// design doc ("Safety and delete flow" / "Risky-extension list (finalized)")
/// requires flagging them with a caution indicator and one extra explicit
/// confirmation before they're included in a delete batch, since they're
/// disproportionately likely to be a live VM disk, database, mail store,
/// backup, encrypted container, or game save rather than harmless junk.
/// </summary>
public static class RiskyExtensions
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Virtual machine disks/snapshots
        ".vdi", ".vmdk",
        ".vhd", ".vhdx",
        ".avhd", ".avhdx",
        ".vmsn", ".vmem", ".vmss",
        ".qcow2",

        // Disk/optical images
        ".iso",
        ".img", ".bin", ".cue", ".nrg", ".mds", ".mdf",
        ".wim",
        ".dmg",

        // Local databases & mail stores
        ".sqlite", ".sqlite3", ".db",
        ".mdb", ".accdb",
        ".ldf", // .mdf already listed above under disk/optical images
        ".sdf",
        ".pst", ".ost",
        ".mbox",
        ".kdbx",

        // Backup software formats
        ".bkf",
        ".tib", ".mrimg", ".vbk", ".vib", ".vrb",
        ".bak",

        // Encrypted containers
        ".vc", ".hc", ".tc",
        ".sparsebundle", ".sparseimage",

        // Game saves & profiles
        ".sav", ".save", ".ess", ".fos", ".sl2",
    };

    public static bool IsRisky(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && Extensions.Contains(extension);
    }
}
