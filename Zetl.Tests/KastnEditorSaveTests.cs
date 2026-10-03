using System.Text.Json;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnEditorSaveTests
{
    [Fact]
    public async Task SaveCapturesRevisionAndTrimsTextAndStyles()
    {
        var (editor, project) = Setup("  edited  ");
        editor.SetInlineStyles([new() { Start = 2, Length = 6, Kind = ZetlInlineStyleKinds.Bold }]);
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var payload = operation.Command.Payload!.Value.Deserialize<UpdateSlipCommand>(ZetlProtocolJson.Options)!;

        Assert.Equal(3, operation.Command.ExpectedTargetRevision);
        Assert.Equal("edited", payload.Text);
        Assert.Equal(0, Assert.Single(payload.InlineStyles!).Start);
        Assert.Equal(6, payload.InlineStyles![0].Length);
        var result = await operation.ExecuteAsync(command => Task.FromResult(Success(command,
            project.Slips[0] with { Text = "edited", Revision = 4, InlineStyles = payload.InlineStyles! })));

        Assert.True(result.CanLeaveEditor);
        Assert.False(editor.IsDirty);
        Assert.Equal("edited", editor.DraftText);
    }

    [Fact]
    public void RefreshingLinkTitlesKeepsFormattingOnFollowingText()
    {
        var text = "See [[target|Old]] tail";
        var (editor, project) = Setup(text);
        project = project with { Slips = [project.Slips[0], project.Slips[0] with { Id = "target", Title = "Renamed target" }] };
        editor.SetInlineStyles([new() { Start = text.IndexOf("tail", StringComparison.Ordinal), Length = 4, Kind = ZetlInlineStyleKinds.Bold }]);
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var payload = operation.Command.Payload!.Value.Deserialize<UpdateSlipCommand>(ZetlProtocolJson.Options)!;

        Assert.Equal("See [[target|Renamed target]] tail", payload.Text);
        Assert.Equal(payload.Text.IndexOf("tail", StringComparison.Ordinal), Assert.Single(payload.InlineStyles!).Start);
        Assert.Equal(text, editor.DraftText);
    }

    [Fact]
    public void SnapshotAtLaterRevisionIsNotAcknowledgedAsThisSave()
    {
        var (editor, project) = Setup("draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var remote = project.Slips[0] with { Text = "draft", Revision = 5 };

        Assert.False(operation.TryAcknowledgeSnapshot(remote));
        editor.Reconcile(remote);
        Assert.Equal(remote, editor.ConflictCurrent);
        Assert.Equal(3, editor.Revision);
    }

    [Fact]
    public void PlainTextSaveLeavesUnchangedStylePayloadAbsent()
    {
        var (editor, project) = Setup("edited");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var payload = operation.Command.Payload!.Value.Deserialize<UpdateSlipCommand>(ZetlProtocolJson.Options)!;
        Assert.Null(payload.InlineStyles);
    }

    [Fact]
    public void BlankBodyRequiresTitleOrPicture()
    {
        var (editor, project) = Setup("   ");
        Assert.Null(KastnEditorSaveOperation.Create(editor, project));
        Assert.NotNull(KastnEditorSaveOperation.Create(editor,
            project with { Slips = [project.Slips[0] with { Title = "Titled" }] }));
        Assert.NotNull(KastnEditorSaveOperation.Create(editor,
            project with { Slips = [project.Slips[0] with { Type = ZetlSlipType.Picture }] }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypingDuringSaveSurvivesResponseAndEarlySnapshot(bool acknowledgeEarly)
    {
        var (editor, project) = Setup("sent draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var response = new TaskCompletionSource<ZetlResponseEnvelope>();
        var save = operation.ExecuteAsync(_ => response.Task);
        editor.ApplyTextEdit("sent draft plus more");
        var saved = project.Slips[0] with { Revision = 4, Text = "sent draft" };
        if (acknowledgeEarly)
        {
            Assert.True(operation.TryAcknowledgeSnapshot(saved));
            Assert.True(editor.IsDirty);
            Assert.Null(editor.ConflictCurrent);
            editor.ApplyTextEdit("sent draft plus even more");
        }

        response.SetResult(Success(operation.Command, saved));
        var result = await save;

        Assert.True(result.Saved);
        Assert.False(result.CanLeaveEditor);
        Assert.Equal(4, editor.Revision);
        Assert.Equal("sent draft", editor.BaselineText);
        Assert.Equal(acknowledgeEarly ? "sent draft plus even more" : "sent draft plus more", editor.DraftText);
        Assert.Null(editor.ConflictCurrent);
        Assert.True(editor.IsDirty);
        Assert.Equal(4, editor.ToDraftDocument(project.Id).BaselineRevision);
    }

    [Fact]
    public async Task FormattingDuringSaveSurvives()
    {
        var (editor, project) = Setup("draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var response = new TaskCompletionSource<ZetlResponseEnvelope>();
        var save = operation.ExecuteAsync(_ => response.Task);
        editor.SetInlineStyles([new() { Start = 0, Length = 5, Kind = ZetlInlineStyleKinds.Bold }]);
        response.SetResult(Success(operation.Command, project.Slips[0] with { Text = "draft", Revision = 4 }));

        Assert.False((await save).CanLeaveEditor);
        Assert.Equal(ZetlInlineStyleKinds.Bold, Assert.Single(editor.DraftInlineStyles).Kind);
        Assert.Empty(editor.BaselineInlineStyles);
    }

    [Fact]
    public async Task SaveCannotReplaceNewEditorSelectionEvenForSameSlip()
    {
        var (editor, project) = Setup("draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var response = new TaskCompletionSource<ZetlResponseEnvelope>();
        var save = operation.ExecuteAsync(_ => response.Task);
        editor.Select(project.Slips[0]);
        editor.SetDraft("new session");
        var saved = project.Slips[0] with { Text = "draft", Revision = 4 };
        Assert.False(operation.TryAcknowledgeSnapshot(saved));
        response.SetResult(Success(operation.Command, saved));

        Assert.False((await save).CanLeaveEditor);
        Assert.Equal("new session", editor.DraftText);
        Assert.Equal(3, editor.Revision);
    }

    [Fact]
    public async Task OlderResponseKeepsNewerRemoteConflictAndLocalTyping()
    {
        var (editor, project) = Setup("sent draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var response = new TaskCompletionSource<ZetlResponseEnvelope>();
        var save = operation.ExecuteAsync(_ => response.Task);
        editor.SetDraft("more typing");
        var remote = project.Slips[0] with { Text = "remote version", Revision = 5 };
        editor.Reconcile(remote);
        response.SetResult(Success(operation.Command, project.Slips[0] with { Text = "sent draft", Revision = 4 }));

        Assert.False((await save).CanLeaveEditor);
        Assert.Equal("more typing", editor.DraftText);
        Assert.Equal(4, editor.Revision);
        Assert.Equal(remote, editor.ConflictCurrent);
        editor.Reconcile(remote with { Revision = 4, Text = "stale remote" });
        Assert.Equal(remote, editor.ConflictCurrent);
    }

    [Fact]
    public async Task OlderResponseCannotRollBackAlreadyAdoptedRemoteRevision()
    {
        var (editor, project) = Setup("sent draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var response = new TaskCompletionSource<ZetlResponseEnvelope>();
        var save = operation.ExecuteAsync(_ => response.Task);
        var saved = project.Slips[0] with { Text = "sent draft", Revision = 4 };
        Assert.True(operation.TryAcknowledgeSnapshot(saved));
        editor.Reconcile(saved with { Text = "remote version", Revision = 5 });
        response.SetResult(Success(operation.Command, saved));
        await save;

        Assert.Equal(5, editor.Revision);
        Assert.Equal("remote version", editor.DraftText);
        editor.Reconcile(saved);
        Assert.Equal(5, editor.Revision);
    }

    [Fact]
    public async Task ConflictRetainsDraftAndExposesCurrentVersion()
    {
        var (editor, project) = Setup("local");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var current = project.Slips[0] with { Text = "remote", Revision = 4 };
        var result = await operation.ExecuteAsync(command => Task.FromResult(new ZetlResponseEnvelope
        {
            CommandId = command.CommandId,
            Status = ZetlResponseStatus.Conflict,
            Conflict = new()
            {
                TargetKind = ZetlEntityKind.Slip, TargetId = current.Id,
                ExpectedRevision = 3, ActualRevision = 4, Current = ZetlProtocolJson.ToElement(current)
            }
        }));

        Assert.False(result.CanLeaveEditor);
        Assert.Equal("local", editor.DraftText);
        Assert.Equal(current.Text, editor.ConflictCurrent?.Text);
        Assert.Equal(current.Revision, editor.ConflictCurrent?.Revision);
        editor.PrepareOverwrite();
        Assert.Equal(4, KastnEditorSaveOperation.Create(editor, project)!.Command.ExpectedTargetRevision);
    }

    [Fact]
    public async Task UnconfirmedSuccessKeepsDraftAndPreventsNavigation()
    {
        var (editor, project) = Setup("draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var result = await operation.ExecuteAsync(command => Task.FromResult(new ZetlResponseEnvelope
        {
            CommandId = command.CommandId, Status = ZetlResponseStatus.Success
        }));
        Assert.False(result.CanLeaveEditor);
        Assert.False(result.Saved);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task TransportFailureKeepsDraftForRecovery()
    {
        var (editor, project) = Setup("draft");
        var operation = KastnEditorSaveOperation.Create(editor, project)!;
        var result = await operation.ExecuteAsync(_ => throw new IOException("Disconnected"));
        Assert.False(result.CanLeaveEditor);
        Assert.Contains("Disconnected", result.Message);
        Assert.Equal("draft", editor.DraftText);
        Assert.Equal(3, editor.Revision);
    }

    private static (KastnEditorState Editor, ZetlProjectSnapshot Project) Setup(string draft)
    {
        var slip = new ZetlSlipSnapshot
        {
            Id = "slip", BucketId = "bucket", Text = "baseline", Revision = 3,
            Type = ZetlSlipType.Text, Source = "kastn", CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var project = new ZetlProjectSnapshot
        {
            Id = "project", Name = "Project", MetadataRevision = 1, ChangeSequence = 3, Slips = [slip]
        };
        var editor = new KastnEditorState();
        editor.Select(slip);
        editor.SetDraft(draft);
        return (editor, project);
    }

    private static ZetlResponseEnvelope Success(ZetlCommandEnvelope command, ZetlSlipSnapshot saved) => new()
    {
        CommandId = command.CommandId, Status = ZetlResponseStatus.Success,
        Payload = ZetlProtocolJson.ToElement(saved)
    };
}
