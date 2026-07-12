using Quickening.App.Controls;
using Quickening.Core.Models;
using Windows.UI;
using Xunit;

namespace Quickening.App.Tests;

public class CategoryColorsTests
{
    [Theory]
    [InlineData(MimeCategory.Image, 0xFF, 0x7A, 0xB6)]
    [InlineData(MimeCategory.Video, 0x8C, 0x6E, 0xFF)]
    [InlineData(MimeCategory.Audio, 0x5E, 0xE7, 0xB7)]
    [InlineData(MimeCategory.Document, 0xFF, 0xD6, 0x66)]
    [InlineData(MimeCategory.Archive, 0x40, 0xC4, 0xFF)]
    [InlineData(MimeCategory.Executable, 0xFF, 0x8A, 0x5C)]
    [InlineData(MimeCategory.Other, 0x8B, 0x96, 0xBC)]
    public void For_ReturnsTheDocumentedFixedColor_ForEachCategory(MimeCategory category, byte r, byte g, byte b)
    {
        // Pinned to exact ARGB values (not just "some color") because
        // CategoryColors.cs's own doc comment calls out that these hex
        // values are duplicated by hand in Styles/Theme.xaml's
        // Category*Color resources - a drift between the two would be
        // silent (XAML can't reference these C# constants) unless this
        // test pins the C# side to the values it's supposed to match.
        // Values are the "Nebula Playful" palette (design-handoff/
        // DESIGN-SPEC.md §1) - updated here since Task 2 of the Nebula
        // redesign plan changed CategoryColors.cs but missed this file.
        Assert.Equal(Color.FromArgb(0xFF, r, g, b), CategoryColors.For(category));
    }

    [Fact]
    public void For_EveryNamedCategory_MapsToADistinctColor()
    {
        var categories = new[]
        {
            MimeCategory.Image, MimeCategory.Video, MimeCategory.Audio, MimeCategory.Document,
            MimeCategory.Archive, MimeCategory.Executable, MimeCategory.Other,
        };

        var colors = categories.Select(CategoryColors.For).Distinct().ToList();

        Assert.Equal(categories.Length, colors.Count);
    }
}
