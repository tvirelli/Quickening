using Quickening.App.Controls;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.App.Tests;

public class FileViewerKindTests
{
    [Theory]
    [InlineData("a.js", FileViewerKind.Code)]
    [InlineData("a.CS", FileViewerKind.Code)]
    [InlineData("a.html", FileViewerKind.Code)]
    [InlineData("a.json", FileViewerKind.Code)]
    [InlineData("a.md", FileViewerKind.Markdown)]
    [InlineData("a.txt", FileViewerKind.PlainText)]
    [InlineData("a.log", FileViewerKind.PlainText)]
    [InlineData("a.pdf", FileViewerKind.Pdf)]
    [InlineData("a.psd", FileViewerKind.None)]
    [InlineData("a.ai", FileViewerKind.None)]
    public void RoutesByExtension(string name, FileViewerKind expected)
    {
        Assert.Equal(expected, FileViewerRouter.ForPath(name, MimeCategory.Other));
    }

    [Theory]
    [InlineData(MimeCategory.Image, FileViewerKind.Image)]
    [InlineData(MimeCategory.Video, FileViewerKind.Video)]
    [InlineData(MimeCategory.Audio, FileViewerKind.Audio)]
    public void MediaCategoriesWin(MimeCategory category, FileViewerKind expected)
    {
        Assert.Equal(expected, FileViewerRouter.ForPath("x.bin", category));
    }
}
