using System.Text.Json;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class KastnContractTests
{
    [Fact] public void CommandEnvelopeRoundTrips()
    {
        var command = ZetlCommandEnvelope.Create(
            commandId: "command-1",
            kind: ZetlCommandKind.UpdateSlip,
            payload: new UpdateSlipCommand { Text = "revised" },
            projectId: "project-1",
            targetId: "slip-1",
            expectedTargetRevision: 4);

        var json = JsonSerializer.Serialize(command, ZetlProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ZetlCommandEnvelope>(
            json,
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Command did not deserialize.");

        AssertTrue(json.Contains("\"kind\":\"updateSlip\"", StringComparison.Ordinal), "Command kinds should be readable strings.");
        AssertEqual("command-1", roundTripped.CommandId, "Command ID should round-trip.");
        AssertEqual(ZetlCommandKind.UpdateSlip, roundTripped.Kind, "Command kind should round-trip.");
        AssertEqual(4L, roundTripped.ExpectedTargetRevision, "Expected revision should round-trip.");
        AssertEqual(
            "revised",
            roundTripped.Payload?.Deserialize<UpdateSlipCommand>(ZetlProtocolJson.Options)?.Text,
            "Typed payload should round-trip.");
    }

    [Fact] public void ValidationEnforcesMutationScope()
    {
        var missingScope = new ZetlCommandEnvelope
        {
            CommandId = "command-2",
            Kind = ZetlCommandKind.UpdateSlip,
            Payload = ZetlProtocolJson.ToElement(new UpdateSlipCommand { Text = "change" })
        };

        var missingProject = ZetlContractRules.Validate(missingScope);
        AssertFalse(missingProject.IsValid, "A scoped mutation without a project should fail validation.");
        AssertEqual("project_id_required", missingProject.Code, "Validation should identify the missing project.");

        var valid = missingScope with
        {
            ProjectId = "project-1",
            TargetId = "slip-1",
            ExpectedTargetRevision = 2
        };
        AssertTrue(ZetlContractRules.Validate(valid).IsValid, "A complete revision-checked mutation should validate.");
    }

    [Fact] public void UnsupportedProtocolIsDistinct()
    {
        var command = new ZetlCommandEnvelope
        {
            ProtocolVersion = ZetlProtocol.CurrentVersion + 1,
            CommandId = "command-3",
            Kind = ZetlCommandKind.ListProjects
        };

        var validation = ZetlContractRules.Validate(command);
        AssertFalse(validation.IsValid, "An unknown protocol should fail validation.");
        AssertEqual(ZetlResponseStatus.UnsupportedProtocol, validation.Status, "Protocol mismatch should have a distinct status.");
        AssertEqual("unsupported_protocol", validation.Code, "Protocol mismatch should have a stable code.");
    }

    [Fact] public void SnapshotKeepsRevisionScopesSeparate()
    {
        var snapshot = new ZetlProjectSnapshot
        {
            Id = "project-1",
            Name = "Demo",
            MetadataRevision = 3,
            ChangeSequence = 42,
            ActiveBucketId = "bucket-1",
            Buckets =
            [
                new ZetlBucketSnapshot
                {
                    Id = "bucket-1",
                    Revision = 5,
                    Name = "Inbox"
                }
            ],
            Slips =
            [
                new ZetlSlipSnapshot
                {
                    Id = "slip-1",
                    Revision = 2,
                    Type = ZetlSlipType.Text,
                    BucketId = "bucket-1",
                    Text = "Captured",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.Parse("2026-06-15T12:00:00Z"),
                    DeletedFromBucketId = "bucket-old",
                    DeletedAtUtc = DateTimeOffset.Parse("2026-06-15T12:05:00Z")
                }
            ]
        };

        var json = JsonSerializer.Serialize(snapshot, ZetlProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ZetlProjectSnapshot>(
            json,
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Snapshot did not deserialize.");

        AssertEqual(3L, roundTripped.MetadataRevision, "Project metadata revision should round-trip.");
        AssertEqual(42L, roundTripped.ChangeSequence, "Project change sequence should round-trip independently.");
        AssertEqual("bucket-1", roundTripped.ActiveBucketId, "Project snapshots should identify the active bucket.");
        AssertEqual(5L, roundTripped.Buckets.Single().Revision, "Bucket revision should remain independent.");
        AssertEqual(2L, roundTripped.Slips.Single().Revision, "Slip revision should remain independent.");
        AssertEqual("bucket-old", roundTripped.Slips.Single().DeletedFromBucketId, "Slip deleted origin should round-trip.");
        AssertEqual(
            DateTimeOffset.Parse("2026-06-15T12:05:00Z"),
            roundTripped.Slips.Single().DeletedAtUtc,
            "Slip deleted timestamp should round-trip.");
    }

    [Fact] public void ConflictCarriesCurrentRecord()
    {
        var current = new ZetlSlipSnapshot
        {
            Id = "slip-1",
            Revision = 5,
            Type = ZetlSlipType.Text,
            BucketId = "bucket-1",
            Text = "newer text",
            Source = "edit",
            CapturedAtUtc = DateTimeOffset.Parse("2026-06-15T12:00:00Z")
        };
        var response = new ZetlResponseEnvelope
        {
            CommandId = "command-4",
            Status = ZetlResponseStatus.Conflict,
            ProjectId = "project-1",
            ProjectChangeSequence = 18,
            Conflict = new ZetlConflict
            {
                TargetKind = ZetlEntityKind.Slip,
                TargetId = current.Id,
                ExpectedRevision = 4,
                ActualRevision = current.Revision,
                Current = ZetlProtocolJson.ToElement(current)
            }
        };

        var json = JsonSerializer.Serialize(response, ZetlProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ZetlResponseEnvelope>(
            json,
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Response did not deserialize.");
        var conflictSlip = roundTripped.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Conflict, roundTripped.Status, "Conflict status should round-trip.");
        AssertEqual(5L, roundTripped.Conflict?.ActualRevision, "Actual revision should be returned.");
        AssertEqual("newer text", conflictSlip?.Text, "Conflict should contain the current record.");
    }

    [Fact] public void ContractsExposeNoStoragePaths()
    {
        var mutationTypes = new[]
        {
            typeof(CreateProjectCommand),
            typeof(RenameProjectCommand),
            typeof(DeleteProjectCommand),
            typeof(AddBucketCommand),
            typeof(UpdateBucketCommand),
            typeof(DeleteBucketCommand),
            typeof(AddSlipCommand),
            typeof(UpdateSlipCommand),
            typeof(MoveSlipCommand),
            typeof(ReorderSlipCommand),
            typeof(DeleteSlipCommand),
            typeof(ZetlPictureSnapshot),
            typeof(ZetlPictureContent)
        };

        var pathProperties = mutationTypes
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .Where(name => name.Contains("Path", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(pathProperties);
    }
}
