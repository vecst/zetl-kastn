using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    private static ZetlProjectSnapshot ViewportProject(int count = 1000)
    {
        var project = RenderProject("viewport", "one");
        return project with
        {
            Slips = Enumerable.Range(0, count).Select(i => project.Slips[0] with
            {
                Id = $"note-{i}", Text = $"Note {i}\n**Emphasis** and [[note-0|First]]\nAnother line."
            }).ToArray()
        };
    }

    private static void SettleReader(ReaderHarness h)
    {
        h.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        h.Window.UpdateLayout();
    }

    [AvaloniaFact]
    public void ReaderViewportBoundsControlsAndCanSelectTheLastNoteAndReturn()
    {
        using var h = new ReaderHarness();
        h.Render(ViewportProject());
        SettleReader(h);
        Assert.InRange(h.Reader.Blocks.Count, 1, 50);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        Assert.DoesNotContain("note-999", h.Reader.Blocks.Keys);
        var retired = h.Reader.Blocks["note-0"];
        h.Reader.UpdateSelection("note-999");
        SettleReader(h);
        Assert.Contains("note-999", h.Reader.Blocks.Keys);
        Assert.Same(Brushes.Purple, h.Reader.Blocks["note-999"].BorderBrush);
        Assert.True(h.Scroll.Offset.Y > h.Scroll.Viewport.Height);
        Assert.InRange(h.Reader.Blocks.Count, 1, 50);
        Assert.DoesNotContain("note-0", h.Reader.Blocks.Keys);
        PressContent(retired);
        Assert.Empty(h.Selected);
        h.Reader.UpdateSelection("note-0");
        SettleReader(h);
        Assert.InRange(h.Reader.Blocks.Count, 1, 50);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        Assert.NotSame(retired, h.Reader.Blocks["note-0"]);
        Assert.True(h.Scroll.Offset.Y < h.Scroll.Viewport.Height);
    }

    [AvaloniaFact]
    public void ReaderViewportReusesVisibleBlocksAndRefreshesEditedAndNewlyVisibleContent()
    {
        using var h = new ReaderHarness();
        var project = ViewportProject();
        h.Render(project);
        SettleReader(h);
        var first = h.Reader.Blocks["note-0"];
        project = project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-999"
            ? s with { Revision = 2, Text = "Edited while offscreen" } : s).ToArray() };
        h.Render(project);
        SettleReader(h);
        Assert.Same(first, h.Reader.Blocks["note-0"]);
        Assert.DoesNotContain("note-999", h.Reader.Blocks.Keys);
        project = project with { ChangeSequence = 3, Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { Revision = 2, Text = "New visible text" } : s).ToArray() };
        h.Render(project);
        SettleReader(h);
        Assert.NotSame(first, h.Reader.Blocks["note-0"]);
        PressContent(first);
        Assert.Empty(h.Selected);
        h.Reader.UpdateSelection("note-999");
        SettleReader(h);
        Assert.Contains(ContentControls(h.Reader.Blocks["note-999"]).OfType<TextBlock>(), t => t.Inlines?.Text == "Edited while offscreen");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderViewportHandlesGroupsVariableHeightsResizeFilteringAndProjectChanges(bool grouped)
    {
        using var h = new ReaderHarness();
        var project = ViewportProject(300);
        project = project with
        {
            Buckets = [project.Buckets[0] with { RenderKind = grouped ? "group" : "plain" }],
            Slips = project.Slips.Select((s, i) => s with { Text = i % 4 == 0
                ? string.Join("\n", Enumerable.Repeat($"Long note {i} with many words to wrap at different widths.", 15)) : s.Text }).ToArray()
        };
        h.Render(project);
        SettleReader(h);
        h.Reader.UpdateSelection("note-150");
        SettleReader(h);
        Assert.Contains("note-150", h.Reader.Blocks.Keys);
        var offset = h.Scroll.Offset.Y;
        h.Render(project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-0"
            ? s with { Revision = 2, Text = "Shortened offscreen" } : s).ToArray() }, selected: "note-150");
        SettleReader(h);
        Assert.Contains("note-150", h.Reader.Blocks.Keys);
        Assert.InRange(h.Scroll.Offset.Y, offset - 1, offset + 1);
        h.Window.Width = 350;
        SettleReader(h);
        Assert.InRange(h.Reader.Blocks.Count, 1, 50);
        h.Reader.UpdateSelection("note-299");
        SettleReader(h);
        Assert.Contains("note-299", h.Reader.Blocks.Keys);
        h.Render(project with { ChangeSequence = 3 }, visible: project.Slips.Take(2).ToArray());
        SettleReader(h);
        Assert.Equal(Math.Max(0, h.Scroll.Extent.Height - h.Scroll.Viewport.Height), h.Scroll.Offset.Y);
        Assert.Equal(2, h.Reader.Blocks.Count);
        h.Render(project with { Id = "another" });
        SettleReader(h);
        Assert.Equal(0, h.Scroll.Offset.Y);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        h.Reader.Clear();
        SettleReader(h);
        Assert.Empty(h.Reader.Blocks);
        Assert.Empty(h.Panel.Children);
    }

    [AvaloniaFact]
    public void ReaderViewportScrollingKeepsContentInViewAndRetiresLinkAndCheckboxActions()
    {
        using var h = new ReaderHarness();
        var project = ViewportProject();
        project = project with { Slips = project.Slips.Select(s => s with { BlockKind = ZetlBlockKinds.Task }).ToArray() };
        h.Render(project);
        SettleReader(h);
        var old = h.Reader.Blocks["note-0"];
        var oldLink = ContentControls(old).OfType<TextBlock>().Single(t => t.Text == "First");
        var oldCheck = ContentControls(old).OfType<TextBlock>().Single(t => t.Text == "☐");
        foreach (var offset in new[] { 1200, 8000, 16000, 3000, 0 })
        {
            h.Scroll.Offset = new Vector(0, offset);
            SettleReader(h);
            Assert.InRange(h.Reader.Blocks.Count, 1, 50);
            Assert.Contains(h.Reader.Blocks.Values, block => block.TranslatePoint(default, h.Scroll) is { } point
                && point.Y < h.Scroll.Viewport.Height && point.Y + block.Bounds.Height > 0);
        }
        PressContent(oldLink);
        PressContent(oldCheck);
        Assert.Empty(h.Selected);
        Assert.Empty(h.Checked);
        var current = h.Reader.Blocks["note-0"];
        PressContent(ContentControls(current).OfType<TextBlock>().Single(t => t.Text == "First"));
        PressContent(ContentControls(current).OfType<TextBlock>().Single(t => t.Text == "☐"));
        Assert.Equal("note-0", Assert.Single(h.Selected));
        Assert.Equal("note-0", Assert.Single(h.Checked));
    }

    [AvaloniaFact]
    public void ReaderViewportKeepsOrderedNumbersAcrossOffscreenRunsAndCanNavigateBetweenBuckets()
    {
        using var h = new ReaderHarness();
        var project = ViewportProject();
        project = project with
        {
            Buckets = [project.Buckets[0], project.Buckets[0] with { Id = "second-bucket", Name = "Second", RenderKind = "group" }],
            Slips = project.Slips.Select((s, i) => s with { BlockKind = ZetlBlockKinds.Ordered,
                BucketId = i < 500 ? project.Buckets[0].Id : "second-bucket" }).ToArray()
        };
        h.Render(project);
        SettleReader(h);
        h.Reader.UpdateSelection("note-900");
        SettleReader(h);
        Assert.Contains(ContentControls(h.Reader.Blocks["note-900"]).OfType<TextBlock>(), t => t.Text == "401.");
        h.Render(project with { ChangeSequence = 2, Slips = project.Slips.Select(s => s.Id == "note-500"
            ? s with { Revision = 2, BlockKind = ZetlBlockKinds.None } : s).ToArray() });
        SettleReader(h);
        Assert.Contains(ContentControls(h.Reader.Blocks["note-900"]).OfType<TextBlock>(), t => t.Text == "400.");
        h.Reader.UpdateSelection("note-0");
        SettleReader(h);
        Assert.Contains("note-0", h.Reader.Blocks.Keys);
        Assert.InRange(h.Reader.Blocks.Count, 1, 60);
    }

    [AvaloniaFact]
    public void ReaderViewportKeepsVisibleAnchorThroughReorderAndHonorsOnlyLatestSelection()
    {
        using var h = new ReaderHarness();
        var project = ViewportProject();
        h.Render(project);
        SettleReader(h);
        h.Reader.UpdateSelection("note-500");
        SettleReader(h);
        var anchor = h.Reader.Blocks.Select(pair => (pair.Key, Y: pair.Value.TranslatePoint(default, h.Scroll)!.Value.Y,
            Height: pair.Value.Bounds.Height)).Where(item => item.Y + item.Height > 0 && item.Y < h.Scroll.Viewport.Height)
            .OrderBy(item => item.Y).First();
        h.Render(project with { ChangeSequence = 2, Slips = project.Slips.Reverse().ToArray() });
        SettleReader(h);
        Assert.Contains(anchor.Key, h.Reader.Blocks.Keys);
        var y = h.Reader.Blocks[anchor.Key].TranslatePoint(default, h.Scroll)!.Value.Y;
        Assert.InRange(y, anchor.Y - 1, anchor.Y + 1);
        h.Reader.UpdateSelection("note-0");
        h.Reader.UpdateSelection("note-999");
        SettleReader(h);
        Assert.Contains("note-999", h.Reader.Blocks.Keys);
        Assert.Same(Brushes.Purple, h.Reader.Blocks["note-999"].BorderBrush);
        Assert.DoesNotContain("note-0", h.Reader.Blocks.Keys);
        h.Reader.UpdateSelection("note-500");
        h.Reader.Suspend();
        SettleReader(h);
        var offset = h.Scroll.Offset;
        h.Reader.UpdateSelection("note-0");
        SettleReader(h);
        Assert.Equal(offset, h.Scroll.Offset);
    }

    [AvaloniaFact]
    public async Task ReaderViewportDefersOffscreenPicturesAndRetiresPendingLoadsOnEviction()
    {
        var replies = new Dictionary<string, TaskCompletionSource<ZetlPictureContent?>>();
        using var h = new ReaderHarness((_, slip) =>
        {
            replies.Add(slip.Id, new());
            return replies[slip.Id].Task;
        });
        var project = ViewportProject(300);
        project = project with { Slips = project.Slips.Select(s => s.Id is "note-0" or "note-299"
            ? s with { Type = ZetlSlipType.Picture, Picture = new() { Sha256 = s.Id, Width = 1, Height = 1 } } : s).ToArray() };
        h.Render(project);
        SettleReader(h);
        Assert.Single(replies);
        var first = h.Reader.Blocks["note-0"];
        var image = Assert.Single(ContentControls(first).OfType<Image>());
        var load = ReaderPictureLoad(h.Reader, "note-0");
        h.Reader.UpdateSelection("note-299");
        SettleReader(h);
        Assert.Equal(2, replies.Count);
        replies["note-0"].SetResult(ReaderPictureContent() with { SlipId = "note-0", Sha256 = "note-0" });
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(image.Source);
        h.Reader.Clear();
        replies["note-299"].SetResult(null);
        SettleReader(h);
        Assert.Empty(h.Reader.Blocks);
    }
}
