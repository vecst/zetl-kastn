using System.Security.Cryptography;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnEditorMutationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LaterTypingSurvivesFormattingAndPictureResponses(bool picture, bool earlySnapshot)
    {
        var (editor, before) = Setup();
        editor.SetDraft("sent draft");
        object payload = picture ? new RemoveSlipPictureCommand()
            : new UpdateSlipCommand { Text = "sent draft", Bold = true };
        var mutation = Mutation(editor, payload);
        editor.ApplyTextEdit("sent draft plus more");
        var saved = before with { Revision = 4, Text = picture ? before.Text : "sent draft", Bold = !picture, Picture = null };
        if (earlySnapshot)
        {
            Assert.True(mutation.TryAcknowledgeSnapshot(saved));
            Assert.Null(editor.ConflictCurrent);
            editor.ApplyTextEdit("sent draft plus even more");
        }
        Assert.True(mutation.TryAcceptResponse(Success(mutation, saved)));

        Assert.Equal(4, editor.Revision);
        Assert.Equal(saved.Text, editor.BaselineText);
        Assert.Equal(earlySnapshot ? "sent draft plus even more" : "sent draft plus more", editor.DraftText);
        Assert.True(editor.IsDirty);
        Assert.Null(editor.ConflictCurrent);
        Assert.Equal(4, editor.ToDraftDocument("project").BaselineRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PictureResponsePreservesExistingDraftAndStyles(bool attach)
    {
        var (editor, before) = Setup();
        editor.SetDraft("draft");
        editor.SetInlineStyles([new() { Start = 0, Length = 5, Kind = ZetlInlineStyleKinds.Bold }]);
        object payload = attach ? new SetSlipPictureCommand { Bytes = [1, 2, 3], Width = 1, Height = 1 }
            : new RemoveSlipPictureCommand();
        var mutation = Mutation(editor, payload);
        var saved = before with { Revision = 4, Picture = attach ? new()
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant()
        } : null };

        Assert.True(mutation.TryAcknowledgeSnapshot(saved));
        Assert.True(mutation.TryAcceptResponse(Success(mutation, saved)));
        Assert.Equal("draft", editor.DraftText);
        Assert.Equal(ZetlInlineStyleKinds.Bold, Assert.Single(editor.DraftInlineStyles).Kind);
        Assert.Empty(editor.BaselineInlineStyles);
        Assert.Null(editor.ConflictCurrent);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void UnchangedFormattingDraftIsAcceptedButLaterStylesAndPendingIntentSurvive()
    {
        var (editor, before) = Setup();
        editor.SetDraft("sent");
        var mutation = Mutation(editor, new UpdateSlipCommand { Text = "sent", Align = "center" });
        var saved = before with { Revision = 4, Text = "sent", Align = "center" };
        Assert.True(mutation.TryAcceptResponse(Success(mutation, saved)));
        Assert.False(editor.IsDirty);

        mutation = Mutation(editor, new UpdateSlipCommand { Text = "sent", Bold = true });
        editor.SetInlineStyles([new() { Start = 0, Length = 4, Kind = ZetlInlineStyleKinds.Italic }]);
        editor.TogglePendingInlineStyle(ZetlInlineStyleKinds.Code);
        Assert.True(mutation.TryAcceptResponse(Success(mutation, saved with { Revision = 5, Bold = true })));
        Assert.Equal(ZetlInlineStyleKinds.Italic, Assert.Single(editor.DraftInlineStyles).Kind);
        Assert.True(editor.HasPendingInlineStyle(ZetlInlineStyleKinds.Code));
        Assert.True(editor.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponsesCannotChangeReselectedEditorEvenForTheSameSlip(bool sameSlip)
    {
        var (editor, before) = Setup();
        var mutation = Mutation(editor, new UpdateSlipCommand { Text = "sent", Bold = true });
        editor.Select(before with { Id = sameSlip ? before.Id : "other" });
        editor.SetDraft("new session");
        var saved = before with { Revision = 4, Text = "sent", Bold = true };

        Assert.False(mutation.IsCurrentEditor);
        Assert.False(mutation.TryAcknowledgeSnapshot(saved));
        Assert.True(mutation.TryAcceptResponse(Success(mutation, saved)));
        Assert.False(mutation.ReconcileConflict(Conflict(mutation, saved)));
        Assert.Equal("new session", editor.DraftText);
        Assert.Equal(3, editor.Revision);
        Assert.Null(editor.ConflictCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponsePreservesNewerRemoteStateOrConflict(bool dirty)
    {
        var (editor, before) = Setup();
        var mutation = Mutation(editor, new UpdateSlipCommand { Text = before.Text, Bold = true });
        var saved = before with { Revision = 4, Bold = true };
        Assert.True(mutation.TryAcknowledgeSnapshot(saved));
        if (dirty) editor.SetDraft("new typing");
        var remote = saved with { Revision = 5, Text = "remote" };
        editor.Reconcile(remote);

        Assert.True(mutation.TryAcceptResponse(Success(mutation, saved)));
        Assert.Equal(dirty ? "new typing" : "remote", editor.DraftText);
        Assert.Equal(dirty ? 4 : 5, editor.Revision);
        Assert.Equal(dirty ? remote : null, editor.ConflictCurrent);
    }

    [Fact]
    public void OlderResponseAdvancesBaselineWithoutErasingNewerConflict()
    {
        var (editor, before) = Setup();
        var mutation = Mutation(editor, new UpdateSlipCommand { Text = "sent", Bold = true });
        editor.SetDraft("new typing");
        var remote = before with { Revision = 5, Text = "remote" };
        editor.Reconcile(remote);
        Assert.True(mutation.TryAcceptResponse(Success(mutation, before with { Revision = 4, Text = "sent", Bold = true })));

        Assert.Equal(4, editor.Revision);
        Assert.Equal("sent", editor.BaselineText);
        Assert.Equal("new typing", editor.DraftText);
        Assert.Equal(remote, editor.ConflictCurrent);
    }

    [Fact]
    public void UnrelatedSnapshotsAreNotAcknowledged()
    {
        var (editor, before) = Setup();
        var mutation = Mutation(editor, new UpdateSlipCommand { Text = before.Text, Bold = true, FontSize = 18 });
        var saved = before with { Revision = 4, Bold = true, FontSize = 18 };
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { Bold = false }));
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { FontSize = 20 }));
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { Revision = 5 }));
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { Id = "other" }));
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { Text = "remote" }));
        Assert.True(mutation.TryAcknowledgeSnapshot(saved));

        mutation = Mutation(editor, new SetSlipPictureCommand { Bytes = [1], Width = 1, Height = 1 });
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { Revision = 5, Picture = new() { Sha256 = "other" } }));
        mutation = Mutation(editor, new RemoveSlipPictureCommand());
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { Revision = 5, Text = "remote", Picture = null }));
    }

    [Fact]
    public void UnconfirmedAndUnrelatedResponsesKeepTheDraft()
    {
        var (editor, before) = Setup();
        editor.SetDraft("draft");
        var mutation = Mutation(editor, new UpdateSlipCommand { Text = "draft", Bold = true });
        var response = Success(mutation, before with { Revision = 4, Text = "draft", Bold = true });
        Assert.False(mutation.TryAcceptResponse(response with { Payload = null }));
        Assert.False(mutation.TryAcceptResponse(response with { ProjectId = "other" }));
        Assert.False(mutation.TryAcceptResponse(response with { CommandId = "other" }));
        Assert.False(mutation.TryAcceptResponse(Success(mutation, before)));
        Assert.False(mutation.TryAcceptResponse(Success(mutation, before with { Id = "other", Revision = 4 })));
        Assert.True(editor.IsDirty);
        Assert.Equal(3, editor.Revision);
    }

    private static (KastnEditorState Editor, ZetlSlipSnapshot Before) Setup()
    {
        var before = new ZetlSlipSnapshot
        {
            Id = "slip", BucketId = "bucket", Revision = 3, Text = "baseline", Type = ZetlSlipType.Text,
            Source = "kastn", CapturedAtUtc = DateTimeOffset.UtcNow, Picture = new() { Sha256 = "old" }
        };
        var editor = new KastnEditorState();
        editor.Select(before);
        return (editor, before);
    }

    private static KastnEditorMutationAcceptance Mutation(KastnEditorState editor, object payload)
    {
        var kind = payload switch
        {
            UpdateSlipCommand => ZetlCommandKind.UpdateSlip,
            SetSlipPictureCommand => ZetlCommandKind.SetSlipPicture,
            _ => ZetlCommandKind.RemoveSlipPicture
        };
        return new(editor, ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), kind, payload,
            "project", editor.SlipId, editor.Revision), payload);
    }

    private static ZetlResponseEnvelope Success(KastnEditorMutationAcceptance mutation, ZetlSlipSnapshot saved) => new()
    {
        CommandId = mutation.Command.CommandId, Status = ZetlResponseStatus.Success,
        ProjectId = "project", Payload = ZetlProtocolJson.ToElement(saved)
    };

    private static ZetlResponseEnvelope Conflict(KastnEditorMutationAcceptance mutation, ZetlSlipSnapshot current) => new()
    {
        CommandId = mutation.Command.CommandId, Status = ZetlResponseStatus.Conflict, ProjectId = "project",
        Conflict = new()
        {
            TargetKind = ZetlEntityKind.Slip, TargetId = current.Id, ExpectedRevision = 3,
            ActualRevision = current.Revision, Current = ZetlProtocolJson.ToElement(current)
        }
    };
}
