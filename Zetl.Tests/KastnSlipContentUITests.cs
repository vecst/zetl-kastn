using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    private static KastnSlipRenderResources ContentResources =>
        new(Brushes.Gray, Brushes.Purple, Brushes.DarkGray, new FontFamily("Consolas"));

    [AvaloniaFact]
    public void SharedContentPreservesMarkdownBlocksAndAuthoredTypography()
    {
        var project = RenderProject("content", "**bold** and *italic* and ~~strike~~\nsecond line\n\n# Heading\n\n> quoted\n\n7. first\n8. second\n\n- [x] done\n- [ ] todo\n\n---\n\n```\nliteral **code**\n```");
        var slip = project.Slips[0] with { FontFamily = "Georgia", FontSize = 19, TextColor = "#2563EB", Align = "right" };
        var renderer = new KastnSlipContentRenderer(new(project), ContentResources);
        var content = renderer.CreateTextContent(slip);
        var text = ContentControls(content.Panel).OfType<TextBlock>()
            .Where(block => !string.IsNullOrWhiteSpace(ContentText(block))).ToArray();
        Assert.Equal(new[] { "bold and italic and strike\nsecond line", "Heading", "quoted", "7.", "first", "8.", "second", "☑", "done", "☐", "todo", "literal **code**" }, text.Select(block => ContentText(block).TrimEnd('\n')));
        Assert.All(text, block =>
        {
            Assert.Equal(19, block.FontSize);
            Assert.Equal(Color.Parse("#2563EB"), Assert.IsAssignableFrom<ISolidColorBrush>(block.Foreground).Color);
        });
        // The authored family renders as the platform resolves it (a serif where Georgia is missing).
        Assert.All(text[..^1], block => Assert.Equal(ZetlFontFamilies.ResolveName("Georgia"), block.FontFamily.Name));
        Assert.Equal("Consolas", text[^1].FontFamily.Name);
        Assert.Equal(TextAlignment.Right, text[0].TextAlignment);
        Assert.Contains(ContentInlines(text[0]), inline => inline is Span span && span.FontWeight == FontWeight.Bold);
        Assert.Contains(ContentInlines(text[0]), inline => inline is Span span && span.FontStyle == FontStyle.Italic);
        Assert.Contains(ContentInlines(text[0]), inline => inline is Span span && span.TextDecorations == TextDecorations.Strikethrough);
        Assert.Contains(content.Panel.Children, child => child is Border { Height: 1 });
        Assert.Contains(content.Panel.Children, child => child is Border { Child: TextBlock { FontStyle: var style } } && style == FontStyle.Italic);
        Assert.Empty(content.LinkTargets);
    }

    [AvaloniaTheory]
    [InlineData(ZetlBlockKinds.Heading, 15.5)]
    [InlineData(ZetlBlockKinds.Quote, 12)]
    [InlineData(ZetlBlockKinds.Code, 12)]
    public void SharedContentKeepsWholeNoteKindsAndSemanticHeadingSize(string kind, double size)
    {
        var project = RenderProject("content", "authored content");
        var renderer = new KastnSlipContentRenderer(new(project), ContentResources);
        var slip = project.Slips[0] with { BlockKind = kind };
        var block = Assert.Single(ContentControls(renderer.CreateTextContent(slip).Panel).OfType<TextBlock>());
        Assert.Equal("authored content", ContentText(block));
        Assert.Equal(size, block.FontSize);
        Assert.Equal(23, Assert.Single(ContentControls(renderer.CreateTextContent(slip with { FontSize = 23 }).Panel).OfType<TextBlock>()).FontSize);
        if (kind == ZetlBlockKinds.Code) Assert.Equal("Consolas", block.FontFamily.Name);
    }

    [AvaloniaFact]
    public void SharedContentAppliesInlineRangesAndKeepsUnsafeLinksUnstyled()
    {
        var project = RenderProject("content", "range [safe](https://example.com) [unsafe](javascript:alert) `code`");
        var slip = project.Slips[0] with { InlineStyles = [new() { Start = 0, Length = 5, Kind = ZetlInlineStyleKinds.Bold }] };
        var renderer = new KastnSlipContentRenderer(new(project), ContentResources);
        var block = Assert.Single(renderer.CreateTextContent(slip).Panel.Children.OfType<TextBlock>());
        var spans = ContentInlines(block).OfType<Span>().ToArray();
        Assert.Contains(spans, span => span.FontWeight == FontWeight.Bold && InlineText(span) == "range");
        var safe = Assert.Single(spans.Where(span => InlineText(span) == "safe"));
        Assert.Equal(TextDecorations.Underline, safe.TextDecorations);
        Assert.Same(ContentResources.AccentBrush, safe.Foreground);
        var unsafeLink = Assert.Single(spans.Where(span => InlineText(span) == "unsafe"));
        Assert.NotEqual(TextDecorations.Underline, unsafeLink.TextDecorations);
        Assert.Contains(ContentInlines(block), inline => inline is Run { Text: "code" } run && run.FontFamily.Name == "Consolas");
    }

    [AvaloniaFact]
    public void SharedContentCapturesWikiResolutionAndRoutesOnlyTheTargetId()
    {
        var project = RenderProject("content", "[[render-two|Target]] [[missing|Missing]]");
        string? navigated = null;
        var renderer = new KastnSlipContentRenderer(new(project), ContentResources, id => navigated = id);
        var content = renderer.CreateTextContent(project.Slips[0]);
        Assert.True(content.LinkTargets["render-two"]);
        Assert.False(content.LinkTargets["missing"]);
        var link = Assert.Single(ContentControls(content.Panel).OfType<TextBlock>().Where(block => block.Text == "Target"));
        var args = PressContent(link);
        Assert.True(args.Handled);
        Assert.Equal("render-two", navigated);
        var removed = new KastnProjectIndex(project with { Slips = [project.Slips[0]] });
        Assert.False(content.MatchesLinks(removed));
        Assert.True(content.MatchesLinks(new(project)));
        Assert.False(content.MatchesLinks(new(project with { Slips = [.. project.Slips, project.Slips[1] with { Id = "missing" }] })));
        // An already-created renderer belongs to its original indexed snapshot.
        Assert.True(renderer.CreateTextContent(project.Slips[0]).LinkTargets["render-two"]);
        Assert.Empty(renderer.CreateTextContent(project.Slips[0] with { BlockKind = ZetlBlockKinds.Code }).LinkTargets);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedListMarkersHandleTaskClicksWithoutSelectingTheContent(bool board)
    {
        var project = RenderProject("content", "task");
        var slip = project.Slips[0] with { BlockKind = ZetlBlockKinds.Task, FontSize = 18 };
        string? toggled = null;
        var renderer = new KastnSlipContentRenderer(new(project), ContentResources, toggleChecked: id =>
        {
            toggled = id;
            return Task.CompletedTask;
        });
        var marker = board ? renderer.CreateBoardListMarker(ZetlBlockKinds.Task, slip)!
            : Assert.IsType<TextBlock>(Assert.IsType<Grid>(renderer.WithListMarker(new(), slip, "☐", checkable: true)).Children[0]);
        Assert.True(PressContent(marker).Handled);
        Assert.Equal(slip.Id, toggled);
        toggled = null;
        var preview = Assert.IsType<Grid>(renderer.WithListMarker(new(), slip, "☐", preview: true));
        Assert.False(PressContent(Assert.IsType<TextBlock>(preview.Children[0])).Handled);
        Assert.Null(toggled);
        var ordered = 0;
        Assert.Equal("1.", KastnSlipContentRenderer.OuterListMarker(ZetlBlockKinds.Ordered, false, ref ordered));
        Assert.Equal("2.", KastnSlipContentRenderer.OuterListMarker(ZetlBlockKinds.Ordered, false, ref ordered));
        Assert.Equal("☑", KastnSlipContentRenderer.OuterListMarker(ZetlBlockKinds.Task, true, ref ordered));
        Assert.Equal("1.", KastnSlipContentRenderer.OuterListMarker(ZetlBlockKinds.Ordered, false, ref ordered));
    }

    [AvaloniaFact]
    public void SharedBoardPreviewAndPictureCaptionKeepTheirDistinctPresentation()
    {
        var slip = RenderProject("content", "**raw board text**").Slips[0] with { FontFamily = "Georgia", FontSize = 17 };
        var preview = KastnSlipContentRenderer.CreateBoardPreview(slip, "Untitled");
        Assert.Equal("**raw board text**", preview.Text);
        Assert.Equal(3, preview.MaxLines);
        Assert.Equal(TextTrimming.CharacterEllipsis, preview.TextTrimming);
        Assert.Equal(17, preview.FontSize);
        Assert.Equal(ZetlFontFamilies.ResolveName("Georgia"), preview.FontFamily.Name);
        Assert.Equal("Untitled", KastnSlipContentRenderer.CreateBoardPreview(slip with { Text = " " }, "Untitled").Text);
        var content = new StackPanel();
        KastnSlipContentRenderer.AddPictureCaption(content, slip with { Text = " caption " });
        var caption = Assert.IsType<TextBlock>(Assert.Single(content.Children));
        Assert.Equal("caption", caption.Text);
        Assert.Equal(FontStyle.Italic, caption.FontStyle);
        Assert.Equal(17, caption.FontSize);
        KastnSlipContentRenderer.AddPictureCaption(content, slip with { Text = "", Title = "" });
        Assert.Single(content.Children);
    }

    [AvaloniaFact]
    public void ReaderAndLiveViewPreviewRenderTheSameSlipContent()
    {
        var (window, controller, original) = WorkflowWindow();
        try
        {
            var source = original.Slips[0] with
            {
                Revision = 2, Text = "# **Heading**\n\n- item\n\n> quote\n\n`code` [[render-two|Target]]",
                FontFamily = "Georgia", FontSize = 18, Align = "center"
            };
            PublishRenderSnapshot(controller, original with { ChangeSequence = 2, Slips = [source, original.Slips[1]] });
            var reader = WindowField<KastnReaderPresenter>(window, "readerPresenter").Blocks[source.Id];
            typeof(MainWindow).GetMethod("OpenViewEditor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [ZetlViewDefaults.Clone(ZetlViewDefaults.CreateAll()[0]), false]);
            Dispatcher.UIThread.RunJobs();
            var preview = window.viewLivePreviewPanel.Children.OfType<Border>().First();
            Assert.Equal(ContentControls(reader).OfType<TextBlock>().Select(ContentDescription),
                ContentControls(preview).OfType<TextBlock>().Select(ContentDescription));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void ReaderInvalidatesOnlyWikiSourcesWhenTargetsDisappearOrReappear()
    {
        var (window, controller, original) = WorkflowWindow();
        try
        {
            var source = original.Slips[0] with { Revision = 2, Text = "See [[render-two|Target]]" };
            var unrelated = source with { Id = "unrelated", Text = "unrelated" };
            var project = original with { ChangeSequence = 2, Slips = [source, original.Slips[1], unrelated] };
            PublishRenderSnapshot(controller, project);
            var blocks = WindowField<KastnReaderPresenter>(window, "readerPresenter").Blocks;
            var firstSource = blocks[source.Id];
            var stable = blocks[unrelated.Id];
            Assert.Contains(ContentControls(firstSource).OfType<TextBlock>(), block => block.Text == "Target");
            PublishRenderSnapshot(controller, project with { ChangeSequence = 3, Slips = [source, unrelated] });
            var missingSource = blocks[source.Id];
            Assert.NotSame(firstSource, missingSource);
            Assert.Same(stable, blocks[unrelated.Id]);
            Assert.DoesNotContain(ContentControls(missingSource).OfType<TextBlock>(), block => block.Text == "Target");
            PublishRenderSnapshot(controller, project with { ChangeSequence = 4 });
            Assert.NotSame(missingSource, blocks[source.Id]);
            Assert.Same(stable, blocks[unrelated.Id]);
            var restored = blocks[source.Id];
            PublishRenderSnapshot(controller, project with { ChangeSequence = 5, Slips = [source, original.Slips[1] with { Revision = 2, Text = "changed target" }, unrelated] });
            Assert.Same(restored, blocks[source.Id]); // Cached link labels keep their authored title.
            Assert.Same(stable, blocks[unrelated.Id]);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RetiredRenderedActionsCannotTargetReusedIdsInAnotherProject(bool task, bool returnToOriginal)
    {
        var (window, controller, original) = WorkflowWindow();
        try
        {
            var source = original.Slips[0] with { Revision = 2, Text = "[[render-two|Target]]", BlockKind = task ? ZetlBlockKinds.Task : "" };
            PublishRenderSnapshot(controller, original with { ChangeSequence = 2, Slips = [source, original.Slips[1]] });
            var block = WindowField<KastnReaderPresenter>(window, "readerPresenter").Blocks[source.Id];
            var action = ContentControls(block).OfType<TextBlock>().Single(text => text.Text == (task ? "☐" : "Target"));
            PublishRenderSnapshot(controller, original with { Id = "replacement" });
            if (returnToOriginal) PublishRenderSnapshot(controller, original);
            window.projectTree.SelectedItem = window.treeProjection.Find(original.Slips[0].Id);
            Dispatcher.UIThread.RunJobs();
            var version = window.editorState.SelectionVersion;
            var status = window.statusText.Text;
            Assert.True(PressContent(action).Handled);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Equal(original.Slips[0].Id, window.editorState.SlipId);
            Assert.Equal(status, window.statusText.Text); // An unexpected IPC attempt reports offline.
        }
        finally { CloseWindow(window); }
    }

    private static string ContentDescription(TextBlock text) =>
        $"{ContentText(text)}|{text.FontFamily.Name}|{text.FontSize}|{text.FontWeight}|{text.FontStyle}|{text.TextAlignment}|{text.TextDecorations}";
    private static string ContentText(TextBlock text) => text.Inlines is { Count: > 0 } inlines
        ? string.Concat(inlines.Select(InlineText)) : text.Text ?? "";
    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text ?? "", Span span => string.Concat(span.Inlines.Select(InlineText)),
        LineBreak => "\n", InlineUIContainer { Child: TextBlock text } => text.Text ?? "", _ => ""
    };
    private static IEnumerable<Inline> ContentInlines(TextBlock block)
    {
        foreach (var inline in block.Inlines ?? [])
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in span.Inlines.SelectMany(NestedInlines)) yield return child;
        }
        static IEnumerable<Inline> NestedInlines(Inline inline)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in span.Inlines.SelectMany(NestedInlines)) yield return child;
        }
    }
    private static IEnumerable<Control> ContentControls(Control root)
    {
        yield return root;
        IEnumerable<Control> children = root switch
        {
            Panel panel => panel.Children,
            Decorator { Child: { } child } => [child],
            TextBlock text => ContentInlines(text).OfType<InlineUIContainer>().Select(inline => inline.Child).OfType<Control>(),
            _ => []
        };
        foreach (var child in children.SelectMany(ContentControls)) yield return child;
    }
    private static PointerPressedEventArgs PressContent(Control control)
    {
        var args = new PointerPressedEventArgs(control, new Avalonia.Input.Pointer(0, PointerType.Mouse, true), control,
            new Point(), 0, new PointerPointProperties(), KeyModifiers.None);
        control.RaiseEvent(args);
        return args;
    }
}
