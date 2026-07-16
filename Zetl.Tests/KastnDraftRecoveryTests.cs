using KASTN;
using ZETL.Contracts;

using Xunit;

namespace ZETL.Tests;

public class KastnDraftRecoveryTests
{
    [Fact]
    public void DraftStoreRoundTripsOneDraftAndClearsIt()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "kastn-draft.json");
        try
        {
            var draft = Draft();
            var store = new KastnDraftStore(path);

            Assert.True(store.Save(draft));
            var loaded = new KastnDraftStore(path).Draft;

            Assert.NotNull(loaded);
            Assert.Equal("project-1", loaded.ProjectId);
            Assert.Equal("slip-1", loaded.SlipId);
            Assert.Equal("local draft", loaded.DraftText);
            Assert.Single(loaded.DraftInlineStyles);
            Assert.Equal(ZetlInlineStyleKinds.Bold, loaded.DraftInlineStyles[0].Kind);

            Assert.True(store.Clear());
            Assert.Null(store.Draft);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecoveryRestoresDirtyDraftAgainstUnchangedBaseline()
    {
        var state = new KastnEditorState();

        Assert.True(state.RestoreDraft(Draft(), Slip(revision: 3, text: "baseline")));

        Assert.True(state.IsDirty);
        Assert.Equal("local draft", state.DraftText);
        Assert.Equal(3, state.Revision);
        Assert.Null(state.ConflictCurrent);
    }

    [Fact]
    public void RecoveryPreservesDraftAndRaisesConflictAfterRemoteChange()
    {
        var state = new KastnEditorState();
        var current = Slip(revision: 4, text: "remote edit");

        Assert.True(state.RestoreDraft(Draft(), current));

        Assert.True(state.IsDirty);
        Assert.Equal("local draft", state.DraftText);
        Assert.Same(current, state.ConflictCurrent);
        Assert.Equal("remote edit", state.ConflictCurrent?.Text);
    }

    [Fact]
    public void RecoveryDropsJournalWithNoActualLocalChange()
    {
        var state = new KastnEditorState();
        var draft = Draft();
        draft.DraftText = draft.BaselineText;
        draft.DraftInlineStyles = draft.BaselineInlineStyles.Select(style => style with { }).ToList();
        var current = Slip(revision: 3, text: "baseline");

        Assert.False(state.RestoreDraft(draft, current));

        Assert.False(state.IsDirty);
        Assert.Equal(current.Text, state.DraftText);
        Assert.Null(state.ConflictCurrent);
    }

    private static KastnDraftDocument Draft() => new()
    {
        ProjectId = "project-1",
        SlipId = "slip-1",
        BaselineRevision = 3,
        BaselineText = "baseline",
        BaselineInlineStyles = [],
        DraftText = "local draft",
        DraftInlineStyles =
        [
            new ZetlInlineStyleRange
            {
                Start = 0,
                Length = 5,
                Kind = ZetlInlineStyleKinds.Bold
            }
        ],
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static ZetlSlipSnapshot Slip(long revision, string text) => new()
    {
        Id = "slip-1",
        Revision = revision,
        Type = ZetlSlipType.Text,
        BucketId = "bucket-1",
        Text = text,
        InlineStyles = [],
        Source = "kastn",
        CapturedAtUtc = DateTimeOffset.UnixEpoch
    };

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "KastnDraftTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
