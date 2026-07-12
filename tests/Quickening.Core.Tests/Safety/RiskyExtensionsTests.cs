using Quickening.Core.Safety;
using Xunit;

namespace Quickening.Core.Tests.Safety;

public class RiskyExtensionsTests
{
    // One representative extension per category from the design doc's
    // finalized risky-extension list, plus a couple of extras where a
    // category has meaningfully distinct sub-groups (e.g. VM snapshot vs.
    // VM disk) - not the full ~40-entry list, but enough that an entire
    // category being silently dropped from the implementation would fail.
    [Theory]
    [InlineData(@"C:\VMs\guest.vdi")] // VirtualBox
    [InlineData(@"C:\VMs\guest.vmdk")] // VMware
    [InlineData(@"C:\VMs\guest.vhdx")] // Hyper-V / WSL2
    [InlineData(@"C:\VMs\snapshot.vmsn")] // VMware snapshot
    [InlineData(@"C:\Images\installer.iso")]
    [InlineData(@"C:\Images\disk.mdf")] // Alcohol/CloneCD/Nero
    [InlineData(@"C:\Data\app.sqlite")]
    [InlineData(@"C:\Data\vault.kdbx")] // KeePass
    [InlineData(@"C:\Mail\archive.pst")] // Outlook
    [InlineData(@"C:\Backups\full.bak")]
    [InlineData(@"C:\Backups\system.tib")] // Acronis
    [InlineData(@"C:\Vaults\secrets.vc")] // VeraCrypt
    [InlineData(@"C:\Vaults\timemachine.sparsebundle")]
    [InlineData(@"C:\Games\slot1.sav")]
    [InlineData(@"C:\Games\DarkSouls.sl2")] // FromSoftware
    public void IsRisky_ReturnsTrue_ForFinalizedRiskyExtensions(string path)
    {
        Assert.True(RiskyExtensions.IsRisky(path));
    }

    [Theory]
    [InlineData(@"C:\VMs\GUEST.VDI")]
    [InlineData(@"C:\Data\Vault.KDBX")]
    public void IsRisky_IsCaseInsensitive(string path)
    {
        Assert.True(RiskyExtensions.IsRisky(path));
    }

    [Theory]
    [InlineData(@"C:\Users\Tony\Downloads\notes.txt")]
    [InlineData(@"D:\Photos\vacation.jpg")]
    public void IsRisky_ReturnsFalse_ForOrdinarySafeExtensions(string path)
    {
        Assert.False(RiskyExtensions.IsRisky(path));
    }

    [Theory]
    [InlineData(@"C:\Users\Tony\noextension")]
    [InlineData(@"C:\Users\Tony\.hidden")] // dotfile: Path.GetExtension returns "" here, not ".hidden"
    public void IsRisky_ReturnsFalse_ForPathsWithNoExtension(string path)
    {
        Assert.False(RiskyExtensions.IsRisky(path));
    }
}
