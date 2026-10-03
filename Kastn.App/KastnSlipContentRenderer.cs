using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnSlipRenderResources(IBrush? BorderBrush, IBrush? AccentBrush,
    IBrush? MutedTextBrush, FontFamily? MonoFontFamily);

// Retain only the links this content actually rendered. A target disappearing or
// reappearing invalidates its source block without rebuilding unrelated notes.
internal sealed record KastnRenderedSlipContent(StackPanel Panel, IReadOnlyDictionary<string, bool> LinkTargets)
{
    public bool MatchesLinks(KastnProjectIndex? project)
    {
        foreach (var (id, resolved) in LinkTargets)
            if (resolved != (project?.Slip(id) is not null)) return false;
        return true;
    }
}

// Shared reader/preview text and compact board presentation. Inputs belong to one
// snapshot; actions carry IDs back to the adapter. Picture loading and ownership
// remain with the presenters and KastnPictureCache.
internal sealed class KastnSlipContentRenderer(
    KastnProjectIndex? project,
    KastnSlipRenderResources resources,
    Action<string>? navigateToSlip = null,
    Func<string, Task>? toggleChecked = null)
{
    public KastnRenderedSlipContent CreateTextContent(ZetlSlipSnapshot slip, double spacing = 5)
    {
        var content = new StackPanel { Spacing = spacing };
        var links = new Dictionary<string, bool>(StringComparer.Ordinal);
        AppendSlipBlocks(content, slip, ZetlViewRenderer.TextOrTitle(slip).Trim(), links);
        return new(content, links);
    }

    public static TextBlock CreateBoardPreview(ZetlSlipSnapshot slip, string untitledTitle)
    {
        var preview = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(slip.Text) ? untitledTitle : slip.Text,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        };
        ApplyTypography(preview, slip);
        return preview;
    }

    public Control WithListMarker(StackPanel content, ZetlSlipSnapshot slip, string marker,
        bool checkable = false, bool preview = false)
    {
        if (marker.Length == 0) return content;
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = preview ? 5 : 6
        };
        var label = new TextBlock
        {
            Text = marker,
            MinWidth = preview ? 16 : 18,
            VerticalAlignment = preview ? VerticalAlignment.Stretch : VerticalAlignment.Top
        };
        ApplyTypography(label, slip);
        if (checkable) MakeCheckable(label, slip.Id);
        Grid.SetColumn(content, 1);
        row.Children.Add(label);
        row.Children.Add(content);
        return row;
    }

    public TextBlock? CreateBoardListMarker(string kind, ZetlSlipSnapshot slip)
    {
        if (kind == ZetlBlockKinds.Task)
        {
            var label = new TextBlock
            {
                Text = slip.Checked ? "☑" : "☐",
                FontWeight = FontWeight.Bold,
                Foreground = slip.Checked ? resources.AccentBrush : resources.MutedTextBrush,
                Margin = new Thickness(0, 0, 4, 0)
            };
            MakeCheckable(label, slip.Id);
            return label;
        }
        return kind switch
        {
            ZetlBlockKinds.Bullet => new TextBlock { Text = "•", Classes = { "muted" } },
            ZetlBlockKinds.Ordered => new TextBlock { Text = "1.", Classes = { "muted" } },
            _ => null
        };
    }

    private void MakeCheckable(TextBlock label, string slipId)
    {
        label.Cursor = new Cursor(StandardCursorType.Hand);
        label.PointerPressed += async (_, args) =>
        {
            args.Handled = true;
            if (toggleChecked is not null) await toggleChecked(slipId);
        };
    }

    public static string OuterListMarker(string kind, bool isChecked, ref int orderedRun)
    {
        if (kind == ZetlBlockKinds.Ordered)
        {
            return $"{++orderedRun}.";
        }

        orderedRun = 0;
        return kind switch
        {
            ZetlBlockKinds.Task => isChecked ? "☑" : "☐",
            ZetlBlockKinds.Bullet => "•",
            _ => ""
        };
    }

    public static string InnerListMarker(string kind, bool isChecked) => kind switch
    {
        ZetlBlockKinds.Task => isChecked ? " ☑" : " ☐",
        ZetlBlockKinds.Bullet => " •",
        ZetlBlockKinds.Ordered => " 1.",
        _ => ""
    };

    public static void ApplyTypography(
        TextBlock block,
        ZetlSlipSnapshot slip,
        bool applyFontFamily = true)
    {
        if (applyFontFamily && KastnSlipTypography.FamilyOf(slip) is { } fontFamily)
        {
            block.FontFamily = fontFamily;
        }

        if (KastnSlipTypography.SizeOf(slip) is { } fontSize)
        {
            block.FontSize = fontSize;
        }

        if (KastnSlipTypography.ForegroundOf(slip) is { } foreground)
        {
            block.Foreground = foreground;
        }
    }

    // The italic caption under a picture in the View and the view-editor preview.
    public static void AddPictureCaption(Panel content, ZetlSlipSnapshot slip)
    {
        var caption = ZetlViewRenderer.TextOrTitle(slip);
        if (string.IsNullOrWhiteSpace(caption))
        {
            return;
        }

        var captionBlock = new TextBlock
        {
            Text = caption.Trim(),
            Classes = { "muted" },
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap
        };
        ApplyTypography(captionBlock, slip);
        content.Children.Add(captionBlock);
    }

    private static TextAlignment SlipTextAlignment(ZetlSlipSnapshot slip) =>
        ZetlViewRenderer.SlipAlignment(slip) switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };

    // Render a slip's Markdown blocks into the slip block: paragraphs as wrapped text
    // (honoring alignment) and lists as marker + content rows with a hanging indent.
    private void AppendSlipBlocks(StackPanel content, ZetlSlipSnapshot slip, string text, Dictionary<string, bool> links)
    {
        var alignment = SlipTextAlignment(slip);
        // A whole-note kind (heading/quote/code/divider) synthesizes its one block; any
        // other kind parses the body normally.
        foreach (var block in ZetlMarkdown.BlocksForNote(
            slip.BlockKind, text, ZetlViewRenderer.EffectiveInlineStyles(slip, text)))
        {
            if (block is ZetlParagraphBlock paragraph)
            {
                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment
                };
                ApplyTypography(textBlock, slip);
                for (var line = 0; line < paragraph.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        textBlock.Inlines!.Add(new LineBreak());
                    }

                    AppendInlines(textBlock.Inlines!, paragraph.Lines[line], links);
                }

                content.Children.Add(textBlock);
            }
            else if (block is ZetlListBlock list)
            {
                var number = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Kind switch
                    {
                        "ordered" => $"{number++}.",
                        "task" => item.Checked ? "☑" : "☐",
                        _ => "•"
                    };
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Avalonia.Thickness(8, 1, 0, 1)
                    };
                    var markerBlock = new TextBlock
                    {
                        Text = marker,
                        MinWidth = 16,
                        Margin = new Avalonia.Thickness(0, 0, 6, 0)
                    };
                    ApplyTypography(markerBlock, slip);
                    Grid.SetColumn(markerBlock, 0);
                    var itemBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    ApplyTypography(itemBlock, slip);
                    AppendInlines(itemBlock.Inlines!, item.Inlines, links);
                    Grid.SetColumn(itemBlock, 1);
                    row.Children.Add(markerBlock);
                    row.Children.Add(itemBlock);
                    content.Children.Add(row);
                }
            }
            else if (block is ZetlHeadingBlock heading)
            {
                // Softened sub-heading: bold and slightly larger, never a section heading.
                var headingBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment,
                    FontWeight = FontWeight.Bold,
                    FontSize = heading.Level <= 1 ? 17 : heading.Level == 2 ? 15.5 : 14,
                    Margin = new Avalonia.Thickness(0, 6, 0, 1)
                };
                // An authored size is an explicit override; without one, retain the
                // heading kind's existing semantic size above.
                ApplyTypography(headingBlock, slip);
                AppendInlines(headingBlock.Inlines!, heading.Inlines, links);
                content.Children.Add(headingBlock);
            }
            else if (block is ZetlQuoteBlock quote)
            {
                var quoteText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = alignment,
                    FontStyle = FontStyle.Italic
                };
                ApplyTypography(quoteText, slip);
                for (var line = 0; line < quote.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        quoteText.Inlines!.Add(new LineBreak());
                    }

                    AppendInlines(quoteText.Inlines!, quote.Lines[line], links);
                }

                content.Children.Add(new Border
                {
                    BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
                    BorderBrush = resources.BorderBrush ?? Brushes.Gray,
                    Padding = new Avalonia.Thickness(8, 2, 0, 2),
                    Margin = new Avalonia.Thickness(2, 2, 0, 2),
                    Child = quoteText
                });
            }
            else if (block is ZetlCodeBlock code)
            {
                var codeText = new TextBlock
                {
                    Text = code.Text,
                    TextWrapping = TextWrapping.Wrap
                };
                ApplyTypography(codeText, slip, applyFontFamily: false);
                if (resources.MonoFontFamily is { } mono)
                {
                    codeText.FontFamily = mono;
                }

                content.Children.Add(new Border
                {
                    Classes = { "surface" },
                    Padding = new Avalonia.Thickness(8, 6),
                    Margin = new Avalonia.Thickness(0, 2, 0, 2),
                    Child = codeText
                });
            }
            else if (block is ZetlDividerBlock)
            {
                content.Children.Add(new Border
                {
                    Height = 1,
                    Background = resources.BorderBrush ?? Brushes.Gray,
                    Margin = new Avalonia.Thickness(0, 6, 0, 6)
                });
            }
        }
    }

    // Walk the Markdown inline AST into Avalonia inlines. Links render as accent
    // underlined text (visual only on-screen; exported HTML/PDF carry the href).
    private void AppendInlines(InlineCollection target, IReadOnlyList<ZetlInline> inlines, Dictionary<string, bool> links)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case ZetlTextRun run:
                    target.Add(new Run(run.Text));
                    break;
                case ZetlCodeRun code:
                    var codeRun = new Run(code.Text);
                    if (resources.MonoFontFamily is { } mono)
                    {
                        codeRun.FontFamily = mono;
                    }
                    target.Add(codeRun);
                    break;
                case ZetlEmphasis emphasis:
                    var span = new Span();
                    AppendInlines(span.Inlines, emphasis.Children, links);
                    switch (emphasis.Kind)
                    {
                        case "bold":
                            span.FontWeight = FontWeight.Bold;
                            break;
                        case "italic":
                            span.FontStyle = FontStyle.Italic;
                            break;
                        case "strike":
                            span.TextDecorations = TextDecorations.Strikethrough;
                            break;
                    }
                    target.Add(span);
                    break;
                case ZetlLink link:
                    var linkSpan = new Span();
                    if (ZetlLinkSafety.TryNormalizeTarget(link.Url, out _))
                    {
                        linkSpan.TextDecorations = TextDecorations.Underline;
                        if (resources.AccentBrush is { } accent)
                        {
                            linkSpan.Foreground = accent;
                        }
                    }
                    AppendInlines(linkSpan.Inlines, link.Children, links);
                    target.Add(linkSpan);
                    break;
                case ZetlWikiLink wiki:
                    var resolved = project?.Slip(wiki.TargetId) is not null;
                    links[wiki.TargetId] = resolved;
                    if (resolved)
                    {
                        var linkBlock = new TextBlock
                        {
                            Text = wiki.CachedTitle,
                            TextDecorations = TextDecorations.Underline,
                            Cursor = new Cursor(StandardCursorType.Hand),
                            Foreground = resources.AccentBrush ?? Brushes.Blue
                        };
                        var targetId = wiki.TargetId;
                        linkBlock.PointerPressed += (s, e) =>
                        {
                            e.Handled = true;
                            navigateToSlip?.Invoke(targetId);
                        };
                        target.Add(new InlineUIContainer(linkBlock));
                    }
                    else
                    {
                        var unresolvedSpan = new Span { Foreground = Brushes.Gray };
                        unresolvedSpan.Inlines.Add(new Run(wiki.CachedTitle));
                        target.Add(unresolvedSpan);
                    }
                    break;
            }
        }
    }

}
