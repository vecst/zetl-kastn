using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public class KastnBoardEditTests
{
    [Theory]
    [InlineData("command")]
    [InlineData("project")]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("bucket")]
    [InlineData("payload")]
    [InlineData("failure")]
    public void UnconfirmedMoveCannotAdvanceToContentUpdate(string mismatch)
    {
        var slip = Slip();
        var operation = new KastnBoardEditOperation("project", slip,
            new() { Save = true, Text = "  edited  ", DestinationBucketId = "next" }, null);
        var command = operation.Command;
        var saved = slip with { Revision = 4, BucketId = "next" };
        var response = Success(command, saved);
        response = mismatch switch
        {
            "command" => response with { CommandId = "other" },
            "project" => response with { ProjectId = "other" },
            "target" => response with { Payload = ZetlProtocolJson.ToElement(saved with { Id = "other" }) },
            "revision" => response with { Payload = ZetlProtocolJson.ToElement(saved with { Revision = 5 }) },
            "bucket" => response with { Payload = ZetlProtocolJson.ToElement(saved with { BucketId = "other" }) },
            "payload" => response with { Payload = null },
            _ => response with { Status = ZetlResponseStatus.Failure }
        };
        Assert.False(operation.TryAcceptResponse(response, out _));
        Assert.Same(command, operation.Command);
        Assert.False(operation.IsComplete);

        Assert.True(operation.TryAcceptResponse(Success(command, saved), out _));
        Assert.Equal(ZetlCommandKind.UpdateSlip, operation.Command.Kind);
        Assert.Equal(4, operation.Command.ExpectedTargetRevision);
        Assert.Equal("edited", Assert.IsType<UpdateSlipCommand>(operation.Payload).Text);
        Assert.False(operation.TryAcceptResponse(Success(command, saved), out _)); // Late duplicate move.
    }

    [Fact]
    public void SaveResponseMustConfirmDialogContent()
    {
        var slip = Slip();
        var operation = new KastnBoardEditOperation("project", slip,
            new() { Save = true, Text = "edited", BlockKind = ZetlBlockKinds.Quote, IgnoreBucketRenderKind = true }, null);
        var saved = slip with { Revision = 4 };
        Assert.False(operation.TryAcceptResponse(Success(operation.Command, saved), out _));
        saved = saved with { Text = "edited", BlockKind = ZetlBlockKinds.Quote, IgnoreBucketRenderKind = true };
        Assert.True(operation.TryAcceptResponse(Success(operation.Command, saved), out _));
        Assert.True(operation.IsComplete);
    }

    [Fact]
    public void DeleteAcknowledgesLazilyCreatedDeletedBucketWithoutLosingTyping()
    {
        var slip = Slip();
        var editor = new KastnEditorState();
        editor.Select(slip);
        var operation = new KastnBoardEditOperation("project", slip, new() { Delete = true }, null);
        var mutation = new KastnEditorMutationAcceptance(editor, operation.Command, operation.Payload);
        editor.ApplyTextEdit("later draft");
        var saved = slip with { Revision = 4, BucketId = "deleted", DeletedFromBucketId = "inbox", DeletedAtUtc = DateTimeOffset.UtcNow };
        Assert.False(mutation.TryAcknowledgeSnapshot(saved with { DeletedAtUtc = null }));
        Assert.True(mutation.TryAcknowledgeSnapshot(saved));
        Assert.True(operation.TryAcceptResponse(Success(operation.Command, saved), out _));
        Assert.Equal("later draft", editor.DraftText);
        Assert.Equal(4, editor.Revision);
        Assert.Null(editor.ConflictCurrent);
    }

    private static ZetlSlipSnapshot Slip() => new()
    {
        Id = "slip", BucketId = "inbox", Revision = 3, Text = "baseline", Type = ZetlSlipType.Text,
        Source = "kastn", CapturedAtUtc = DateTimeOffset.UtcNow
    };

    private static ZetlResponseEnvelope Success(ZetlCommandEnvelope command, ZetlSlipSnapshot saved) => new()
    {
        CommandId = command.CommandId, ProjectId = "project", Status = ZetlResponseStatus.Success,
        Payload = ZetlProtocolJson.ToElement(saved)
    };
}
