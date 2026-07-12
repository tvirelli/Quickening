using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Quickening.App.Controls;

/// <summary>
/// Just enough markdown for the shipped legal documents and any text preview:
/// #/##/### headings, **bold** runs, "- " bullets, "---" dividers, and
/// table/code-fence lines shown monospace. Anything fancier belongs on the
/// website, not in the app. Extracted from SettingsDialog so the compare
/// viewer's markdown tile and the About-screen document viewer share one
/// implementation.
/// </summary>
public static class MarkdownLiteRenderer
{
    public static UIElement Render(ResourceDictionary resources, string[] lines)
    {
        var stack = new StackPanel { Spacing = 8 };
        var monoFont = new FontFamily("Consolas");
        var inCodeFence = false;
        var paragraph = new List<string>();
        var tableRows = new List<string[]>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }
            var block = new TextBlock
            {
                FontFamily = (FontFamily)resources["BodyFontFamily"],
                FontSize = 13.5,
                Foreground = (Brush)resources["TextBodyBrush"],
                TextWrapping = TextWrapping.Wrap,
            };
            AppendWithBoldRuns(block, string.Join(" ", paragraph));
            stack.Children.Add(block);
            paragraph.Clear();
        }

        void FlushTable()
        {
            if (tableRows.Count == 0)
            {
                return;
            }
            stack.Children.Add(BuildTable(resources, tableRows));
            tableRows.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Replace("`", string.Empty);

            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushTable();
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                FlushParagraph();
                stack.Children.Add(new TextBlock
                {
                    Text = raw,
                    FontFamily = monoFont,
                    FontSize = 11.5,
                    Foreground = (Brush)resources["TextMutedBrush"],
                    TextWrapping = TextWrapping.NoWrap,
                });
                continue;
            }

            if (line.StartsWith("|", StringComparison.Ordinal))
            {
                FlushParagraph();
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                // Skip the |---|---| separator row.
                if (!cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':' or ' ')))
                {
                    tableRows.Add(cells);
                }
                continue;
            }
            FlushTable();

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                FlushParagraph();
                var level = line.TakeWhile(c => c == '#').Count();
                stack.Children.Add(new TextBlock
                {
                    Text = line.TrimStart('#').Trim(),
                    FontFamily = (FontFamily)resources["DisplayFontFamily"],
                    FontSize = level switch { 1 => 20, 2 => 16.5, _ => 14.5 },
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)resources["TextHeadingBrush"],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, level == 1 ? 0 : 10, 0, 0),
                });
                continue;
            }

            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                FlushParagraph();
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)resources["SurfaceCardBorderBrush"],
                    Margin = new Thickness(0, 6, 0, 6),
                });
                continue;
            }

            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var bullet = new TextBlock
                {
                    FontFamily = (FontFamily)resources["BodyFontFamily"],
                    FontSize = 13.5,
                    Foreground = (Brush)resources["TextBodyBrush"],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(14, 0, 0, 0),
                };
                AppendWithBoldRuns(bullet, "•  " + line.TrimStart().Substring(2));
                stack.Children.Add(bullet);
                continue;
            }

            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        FlushTable();

        return stack;
    }

    /// <summary>
    /// Renders a parsed markdown table as a real Grid - header row bold with a
    /// rule under it, evenly distributed wrapping columns.
    /// </summary>
    private static UIElement BuildTable(ResourceDictionary resources, List<string[]> rows)
    {
        var columnCount = rows.Max(r => r.Length);
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 7, Margin = new Thickness(0, 4, 0, 4) };
        for (var c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var gridRow = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var isHeader = r == 0;
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = new TextBlock
                {
                    FontFamily = (FontFamily)resources["BodyFontFamily"],
                    FontSize = 12,
                    FontWeight = isHeader ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = (Brush)resources[isHeader ? "TextHeadingBrush" : "TextMutedBrush"],
                    TextWrapping = TextWrapping.Wrap,
                };
                AppendWithBoldRuns(cell, rows[r][c]);
                Grid.SetRow(cell, gridRow);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            gridRow++;

            if (isHeader)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var rule = new Border
                {
                    Height = 1,
                    Background = (Brush)resources["SurfaceCardBorderBrush"],
                };
                Grid.SetRow(rule, gridRow);
                Grid.SetColumnSpan(rule, columnCount);
                grid.Children.Add(rule);
                gridRow++;
            }
        }

        return grid;
    }

    /// <summary>
    /// Splits on ** pairs and alternates regular/bold runs so emphasis in the
    /// text survives without a real markdown engine.
    /// </summary>
    private static void AppendWithBoldRuns(TextBlock block, string text)
    {
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = parts[i] };
            if (i % 2 == 1)
            {
                run.FontWeight = FontWeights.Bold;
            }
            // Non-bold runs set no weight so they inherit the TextBlock's own
            // (e.g. table header cells are bold at block level).
            block.Inlines.Add(run);
        }
    }
}
