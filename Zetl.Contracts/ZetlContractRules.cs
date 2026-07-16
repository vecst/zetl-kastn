namespace ZETL.Contracts;

public sealed record ZetlCommandValidation
{
    public required bool IsValid { get; init; }
    public required ZetlResponseStatus Status { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }

    public static ZetlCommandValidation Valid { get; } = new()
    {
        IsValid = true,
        Status = ZetlResponseStatus.Success
    };
}

public static class ZetlContractRules
{
    public static ZetlCommandValidation Validate(ZetlCommandEnvelope command)
    {
        if (command.ProtocolVersion != ZetlProtocol.CurrentVersion)
        {
            return Invalid(
                ZetlResponseStatus.UnsupportedProtocol,
                "unsupported_protocol",
                $"Protocol {command.ProtocolVersion} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            return Invalid(
                ZetlResponseStatus.ValidationError,
                "command_id_required",
                "A command ID is required.");
        }

        if (RequiresProject(command.Kind) && string.IsNullOrWhiteSpace(command.ProjectId))
        {
            return Invalid(
                ZetlResponseStatus.ValidationError,
                "project_id_required",
                $"{command.Kind} requires a project ID.");
        }

        if (RequiresTarget(command.Kind) && string.IsNullOrWhiteSpace(command.TargetId))
        {
            return Invalid(
                ZetlResponseStatus.ValidationError,
                "target_id_required",
                $"{command.Kind} requires a target ID.");
        }

        if (RequiresExpectedRevision(command.Kind)
            && command.ExpectedTargetRevision is null)
        {
            return Invalid(
                ZetlResponseStatus.ValidationError,
                "expected_revision_required",
                $"{command.Kind} requires the target record's expected revision.");
        }

        if (command.ExpectedTargetRevision is <= 0)
        {
            return Invalid(
                ZetlResponseStatus.ValidationError,
                "expected_revision_invalid",
                "Expected revisions must be positive.");
        }

        if (RequiresPayload(command.Kind) && command.Payload is null)
        {
            return Invalid(
                ZetlResponseStatus.ValidationError,
                "payload_required",
                $"{command.Kind} requires a payload.");
        }

        return ZetlCommandValidation.Valid;
    }

    public static bool RequiresProject(ZetlCommandKind kind)
    {
        return kind is not ZetlCommandKind.ListProjects
            and not ZetlCommandKind.CreateProject;
    }

    public static bool IsReadOnly(ZetlCommandKind kind) =>
        kind is ZetlCommandKind.ListProjects
            or ZetlCommandKind.GetProject
            or ZetlCommandKind.GetSlipPicture;

    public static bool RequiresTarget(ZetlCommandKind kind)
    {
        return kind is ZetlCommandKind.UpdateBucket
            or ZetlCommandKind.SetBucketHeading
            or ZetlCommandKind.DeleteBucket
            or ZetlCommandKind.ReorderBucket
            or ZetlCommandKind.GetSlipPicture
            or ZetlCommandKind.UpdateSlip
            or ZetlCommandKind.MoveSlip
            or ZetlCommandKind.ReorderSlip
            or ZetlCommandKind.DeleteSlip
            or ZetlCommandKind.SetSlipPicture
            or ZetlCommandKind.RemoveSlipPicture;
    }

    public static bool RequiresExpectedRevision(ZetlCommandKind kind)
    {
        return kind is ZetlCommandKind.RenameProject
            or ZetlCommandKind.SetProjectStatus
            or ZetlCommandKind.SetJournalMode
            or ZetlCommandKind.SetProjectView
            or ZetlCommandKind.SaveProjectView
            or ZetlCommandKind.DeleteProjectView
            or ZetlCommandKind.DeleteProject
            or ZetlCommandKind.UpdateBucket
            or ZetlCommandKind.SetBucketHeading
            or ZetlCommandKind.DeleteBucket
            or ZetlCommandKind.ReorderBucket
            or ZetlCommandKind.UpdateSlip
            or ZetlCommandKind.MoveSlip
            or ZetlCommandKind.ReorderSlip
            or ZetlCommandKind.DeleteSlip
            or ZetlCommandKind.SetSlipPicture
            or ZetlCommandKind.RemoveSlipPicture;
    }

    public static bool RequiresPayload(ZetlCommandKind kind)
    {
        return kind is ZetlCommandKind.CreateProject
            or ZetlCommandKind.CreateTemporaryProjectFromReplay
            or ZetlCommandKind.RenameProject
            or ZetlCommandKind.SetActiveProject
            or ZetlCommandKind.SetProjectStatus
            or ZetlCommandKind.SetJournalMode
            or ZetlCommandKind.SetProjectView
            or ZetlCommandKind.SaveProjectView
            or ZetlCommandKind.DeleteProjectView
            or ZetlCommandKind.AddBucket
            or ZetlCommandKind.UpdateBucket
            or ZetlCommandKind.SetBucketHeading
            or ZetlCommandKind.ReorderBucket
            or ZetlCommandKind.AddSlip
            or ZetlCommandKind.UpdateSlip
            or ZetlCommandKind.MoveSlip
            or ZetlCommandKind.ReorderSlip
            or ZetlCommandKind.SetSlipPicture;
    }

    private static ZetlCommandValidation Invalid(
        ZetlResponseStatus status,
        string code,
        string message)
    {
        return new ZetlCommandValidation
        {
            IsValid = false,
            Status = status,
            Code = code,
            Message = message
        };
    }
}
