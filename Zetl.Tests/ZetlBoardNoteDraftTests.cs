using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

// The Board editor against another writer: Kastn saves on focus loss, which can
// land just after Ctrl+B opened the Board on the same slip.
public class ZetlBoardNoteDraftTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "ZetlBoardNoteDraftTests", Guid.NewGuid().ToString("N"));
    private readonly ZetlStateStore store;
    private readonly ZetlProject project;
    private readonly ZetlBucket bucket;

    public ZetlBoardNoteDraftTests()
    {
        Directory.CreateDirectory(directory);
        store = new ZetlStateStore(Path.Combine(directory, "state.json"), "board-draft");
        project = store.CreateProject("Board", ["Inbox"], "Inbox");
        bucket = project.Buckets.Single(item => item.Name == "Inbox");
    }

    [Fact]
    public void KastnSaveAfterTheBoardLoadedIsNotMistakenForBoardTyping()
    {
        var slip = store.AddSlip(bucket, "before", "kastn");
        var draft = new ZetlBoardNoteDraft();
        draft.Load(slip);
        var editorText = slip.Text;

        SaveFromKastn(slip, "typed in Kastn");

        // The store updated the very slip the Board holds.
        Assert.Equal("typed in Kastn", draft.Slip!.Text);
        Assert.False(draft.IsEdited(editorText), "An untouched editor should reload, not keep its stale text.");
        Assert.Null(draft.TextToSave(editorText));
    }

    [Fact]
    public void TypingInTheBoardIsKeptAndSaved()
    {
        var slip = store.AddSlip(bucket, "before", "manual");
        var draft = new ZetlBoardNoteDraft();
        draft.Load(slip);

        Assert.True(draft.IsEdited("before "), "A trailing space being typed is still an edit.");
        Assert.Null(draft.TextToSave("before "));
        Assert.Equal("after", draft.TextToSave("  after "));
        Assert.Null(draft.TextToSave("   "));
    }

    [Fact]
    public void APictureCaptionCanBeCleared()
    {
        var slip = store.AddSlip(bucket, "caption", "manual");
        slip.Type = ZetlSlipType.Picture;
        slip.Image = new ZetlImageAsset { RelativePath = "assets/caption.png" };
        var draft = new ZetlBoardNoteDraft();
        draft.Load(slip);

        Assert.Equal("", draft.TextToSave(""));
    }

    private void SaveFromKastn(ZetlSlip slip, string text)
    {
        var service = new ZetlProjectService(store);
        var response = service.Execute(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = text },
            project.Id,
            slip.Id,
            slip.Revision));
        Assert.Equal(ZetlResponseStatus.Success, response.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
