using System.Text.Json;
using ZETL.Contracts;

namespace ZETL;

internal sealed class ZetlProjectService
{
    private const int DefaultCompletedCommandCapacity = 1024;

    private readonly ZetlStateStore store;
    private readonly int completedCommandCapacity;
    private readonly Action<string>? log;
    private readonly Dictionary<string, ZetlResponseEnvelope> completedCommands = new(StringComparer.Ordinal);
    private readonly Queue<string> completedCommandOrder = new();
    private int serviceMutationDepth;

    public ZetlProjectService(
        ZetlStateStore store,
        int completedCommandCapacity = DefaultCompletedCommandCapacity,
        Action<string>? log = null)
    {
        this.store = store;
        this.completedCommandCapacity = Math.Max(1, completedCommandCapacity);
        this.log = log;
        store.ProjectPersisted += OnProjectPersisted;
    }

    public event EventHandler<ZetlProjectChangedEvent>? ProjectChanged;

    public ZetlResponseEnvelope Execute(ZetlCommandEnvelope command)
    {
        lock (store.MutationSyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(command.CommandId)
                && completedCommands.TryGetValue(command.CommandId, out var completed))
            {
                return completed;
            }

            var validation = ZetlContractRules.Validate(command);
            if (!validation.IsValid)
            {
                var error = ErrorResponse(
                    command,
                    validation.Status,
                    validation.Code!,
                    validation.Message!);
                return command.Kind == ZetlCommandKind.GetSlipPicture
                    ? error
                    : Remember(command.CommandId, error);
            }

            ZetlResponseEnvelope response;
            try
            {
                serviceMutationDepth++;
                try
                {
                    response = ExecuteValidated(command);
                }
                finally
                {
                    serviceMutationDepth--;
                }
            }
            catch (JsonException ex)
            {
                response = ErrorResponse(
                    command,
                    ZetlResponseStatus.ValidationError,
                    "payload_invalid",
                    ex.Message);
            }
            catch (Exception ex)
            {
                response = ErrorResponse(
                    command,
                    ZetlResponseStatus.Failure,
                    "mutation_failed",
                    ex.Message);
            }

            return command.Kind == ZetlCommandKind.GetSlipPicture
                ? response
                : Remember(command.CommandId, response);
        }
    }

    private void OnProjectPersisted(
        object? sender,
        ZetlProjectPersistedEventArgs persisted)
    {
        if (serviceMutationDepth != 0)
        {
            return;
        }

        Publish(
            persisted.ProjectId,
            persisted.ChangeSequence,
            ZetlChangeKind.Updated,
            ZetlEntityKind.Project,
            persisted.ProjectId,
            entityRevision: null);
    }

    private ZetlResponseEnvelope ExecuteValidated(ZetlCommandEnvelope command)
    {
        return command.Kind switch
        {
            ZetlCommandKind.ListProjects => ListProjects(command),
            ZetlCommandKind.GetProject => GetProject(command),
            ZetlCommandKind.GetSlipPicture => GetSlipPicture(command),
            ZetlCommandKind.CreateProject => CreateProject(command),
            ZetlCommandKind.RenameProject => RenameProject(command),
            ZetlCommandKind.SetProjectStatus => SetProjectStatus(command),
            ZetlCommandKind.SetJournalMode => SetJournalMode(command),
            ZetlCommandKind.SetProjectView => SetProjectView(command),
            ZetlCommandKind.SaveProjectView => SaveProjectView(command),
            ZetlCommandKind.DeleteProjectView => DeleteProjectView(command),
            ZetlCommandKind.DeleteProject => DeleteProject(command),
            ZetlCommandKind.AddBucket => AddBucket(command),
            ZetlCommandKind.UpdateBucket => UpdateBucket(command),
            ZetlCommandKind.SetBucketHeading => SetBucketHeading(command),
            ZetlCommandKind.DeleteBucket => DeleteBucket(command),
            ZetlCommandKind.AddSlip => AddSlip(command),
            ZetlCommandKind.UpdateSlip => UpdateSlip(command),
            ZetlCommandKind.MoveSlip => MoveSlip(command),
            ZetlCommandKind.ReorderSlip => ReorderSlip(command),
            ZetlCommandKind.DeleteSlip => DeleteSlip(command),
            _ => ErrorResponse(
                command,
                ZetlResponseStatus.ValidationError,
                "command_unknown",
                $"Command kind '{command.Kind}' is not supported.")
        };
    }

    private ZetlResponseEnvelope ListProjects(ZetlCommandEnvelope command)
    {
        var summaries = store.State.Projects
            .Where(project => !string.Equals(
                project.Name,
                ZetlStateStore.LogProjectName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ZetlProjectSnapshotMapper.ToSummary)
            .ToList();
        return Success(command, payload: summaries);
    }

    private ZetlResponseEnvelope GetProject(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        return project is null
            ? NotFound(command, ZetlEntityKind.Project, command.ProjectId!)
            : Success(command, project, ZetlProjectSnapshotMapper.ToSnapshot(project));
    }

    private ZetlResponseEnvelope GetSlipPicture(ZetlCommandEnvelope command)
    {
        var found = FindNote(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Slip, command.TargetId!);
        }

        var (project, _, note) = found.Value;
        if (!note.IsImage || note.Image is null)
        {
            return ValidationError(
                command,
                "slip_not_picture",
                "The requested slip does not contain a picture.");
        }

        var bytes = store.ReadImageAsset(project, note);
        if (bytes is null)
        {
            return ErrorResponse(
                command,
                ZetlResponseStatus.Failure,
                "picture_unavailable",
                "The picture asset is unavailable.");
        }

        return Success(
            command,
            project,
            new ZetlPictureContent
            {
                SlipId = note.Id,
                Sha256 = note.Image.Sha256,
                MimeType = "image/png",
                Width = note.Image.Width,
                Height = note.Image.Height,
                Bytes = bytes
            });
    }

    private ZetlResponseEnvelope CreateProject(ZetlCommandEnvelope command)
    {
        var payload = Payload<CreateProjectCommand>(command);
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return ValidationError(command, "project_name_required", "A project name is required.");
        }

        var definitions = payload.Buckets is not { Count: > 0 }
            ? [new CreateBucketDefinition { Name = "Inbox" }]
            : payload.Buckets;
        if (definitions.Any(bucket => bucket is null || string.IsNullOrWhiteSpace(bucket.Name)))
        {
            return ValidationError(command, "bucket_name_required", "Every initial bucket requires a name.");
        }

        var project = store.CreateProject(
            payload.Name,
            definitions.Select(bucket => bucket.Name),
            definitions[0].Name);
        foreach (var definition in definitions)
        {
            var bucket = project.Buckets.FirstOrDefault(item =>
                string.Equals(item.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (bucket is null)
            {
                continue;
            }

            ApplyBucketDefinition(
                project,
                bucket,
                definition.Name,
                definition.ParentBucketId,
                definition.Settings ?? new ZetlBucketSettings());
        }

        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Created, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope RenameProject(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<RenameProjectCommand>(command);
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return ValidationError(command, "project_name_required", "A project name is required.");
        }

        store.UpdateProjectName(project, payload.Name);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope SetProjectStatus(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<SetProjectStatusCommand>(command);
        var status = ZetlStateStore.CanonicalProjectStatus(payload.Status);
        if (status is null)
        {
            return ValidationError(
                command,
                "project_status_invalid",
                "A project status must be Active, Finished, or Archived.");
        }

        store.SetProjectStatus(project, status);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope SetJournalMode(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<SetJournalModeCommand>(command);
        store.SetJournalMode(project, payload.JournalMode);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope SetProjectView(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<SetProjectViewCommand>(command);
        store.SetProjectDefaultView(project, payload.ViewId);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope SaveProjectView(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<SaveProjectViewCommand>(command);
        var view = ZetlProjectSnapshotMapper.ToDocument(payload.View);
        if (view.Sections.Count == 0)
        {
            return ValidationError(
                command,
                "project_view_sections_required",
                "Project-scoped views require at least one custom section.");
        }

        if (ZetlViewDefaults.IsBuiltIn(view.Id))
        {
            return ValidationError(
                command,
                "project_view_id_reserved",
                "Built-in view IDs cannot be used for project-scoped views.");
        }

        var errors = ZetlViewValidator.Validate(view);
        if (errors.Count > 0)
        {
            return ValidationError(command, "project_view_invalid", string.Join("; ", errors));
        }

        store.SaveProjectView(project, view);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope DeleteProjectView(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<DeleteProjectViewCommand>(command);
        if (string.IsNullOrWhiteSpace(payload.ViewId))
        {
            return ValidationError(command, "project_view_id_required", "A project view ID is required.");
        }

        if (!project.Views.Any(view => string.Equals(view.Id, payload.ViewId, StringComparison.Ordinal)))
        {
            return ValidationError(command, "project_view_not_found", "That project view no longer exists.");
        }

        store.DeleteProjectView(project, payload.ViewId);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(project);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Project, project.Id, project.MetadataRevision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope DeleteProject(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision,
            ZetlProjectSnapshotMapper.ToSnapshot(project));
        if (conflict is not null)
        {
            return conflict;
        }

        var finalSequence = project.ChangeSequence + 1;
        store.DeleteProject(project.Id);
        Publish(
            project.Id,
            finalSequence,
            ZetlChangeKind.Deleted,
            ZetlEntityKind.Project,
            project.Id,
            project.MetadataRevision);
        return new ZetlResponseEnvelope
        {
            CommandId = command.CommandId,
            Status = ZetlResponseStatus.Success,
            ProjectId = project.Id,
            ProjectChangeSequence = finalSequence
        };
    }

    private ZetlResponseEnvelope AddBucket(ZetlCommandEnvelope command)
    {
        var project = FindProject(command.ProjectId!);
        if (project is null)
        {
            return NotFound(command, ZetlEntityKind.Project, command.ProjectId!);
        }

        var payload = Payload<AddBucketCommand>(command);
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return ValidationError(command, "bucket_name_required", "A bucket name is required.");
        }

        var bucket = store.AddBucket(project, payload.Name, payload.ParentBucketId, setActive: false);
        bucket.RenderKind = ZetlViewRenderer.NormalizeBucketRenderKind(payload.RenderKind);
        ApplyBucketDefinition(
            project,
            bucket,
            payload.Name,
            payload.ParentBucketId,
            payload.Settings ?? new ZetlBucketSettings());
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket);
        Publish(project, ZetlChangeKind.Created, ZetlEntityKind.Bucket, bucket.Id, bucket.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope UpdateBucket(ZetlCommandEnvelope command)
    {
        var found = FindBucket(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Bucket, command.TargetId!);
        }

        var (project, bucket) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Bucket,
            bucket.Id,
            bucket.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<UpdateBucketCommand>(command);
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return ValidationError(command, "bucket_name_required", "A bucket name is required.");
        }

        if (CreatesParentCycle(project, bucket.Id, payload.ParentBucketId))
        {
            return ValidationError(
                command,
                "bucket_parent_cycle",
                "A bucket cannot be moved beneath one of its descendants.");
        }

        var settings = payload.Settings ?? new ZetlBucketSettings();
        store.UpdateBucket(
            project,
            bucket,
            payload.Name,
            payload.ParentBucketId,
            settings.Kind,
            settings.DefaultKind,
            settings.DefaultCompileMode,
            settings.DefaultStartingText,
            settings.DefaultTsvRowLength,
            settings.PopMode,
            settings.ReplayReviewBucketId);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Bucket, bucket.Id, bucket.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope SetBucketHeading(ZetlCommandEnvelope command)
    {
        var found = FindBucket(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Bucket, command.TargetId!);
        }

        var (project, bucket) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Bucket,
            bucket.Id,
            bucket.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket));
        if (conflict is not null)
        {
            return conflict;
        }

        if (ZetlStateStore.IsDeletedBucket(bucket))
        {
            return ValidationError(command, "deleted_bucket_protected", "The Deleted bucket has no heading.");
        }

        var payload = Payload<SetBucketHeadingCommand>(command);
        store.SetBucketHeading(bucket, payload.Align, payload.Bold, payload.Level);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Bucket, bucket.Id, bucket.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope DeleteBucket(ZetlCommandEnvelope command)
    {
        var found = FindBucket(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Bucket, command.TargetId!);
        }

        var (project, bucket) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Bucket,
            bucket.Id,
            bucket.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket));
        if (conflict is not null)
        {
            return conflict;
        }

        if (ZetlStateStore.IsScratchBucket(bucket))
        {
            return ValidationError(command, "scratch_protected", "The Scratch bucket cannot be deleted.");
        }

        if (ZetlStateStore.IsDeletedBucket(bucket))
        {
            return ValidationError(command, "deleted_bucket_protected", "The Deleted bucket cannot be deleted.");
        }

        store.DeleteBucket(project, bucket.Id);
        Publish(project, ZetlChangeKind.Deleted, ZetlEntityKind.Bucket, bucket.Id, bucket.Revision);
        return Success(command, project);
    }

    private ZetlResponseEnvelope AddSlip(ZetlCommandEnvelope command)
    {
        var payload = Payload<AddSlipCommand>(command);
        var found = FindBucket(command.ProjectId!, payload.BucketId);
        if (found is null)
        {
            return NotFound(
                command,
                ZetlEntityKind.Bucket,
                payload.BucketId);
        }

        var (project, bucket) = found.Value;
        var isDivider = string.Equals(payload.ListKind, "divider", StringComparison.OrdinalIgnoreCase);
        // A divider is a structural note with no authored content, so it is exempt from
        // the title-or-note requirement; every other kind still needs content.
        if (!isDivider
            && string.IsNullOrWhiteSpace(payload.Text)
            && string.IsNullOrWhiteSpace(payload.Title))
        {
            return ValidationError(command, "slip_content_required", "A slip title or note is required.");
        }

        if (ZetlStateStore.IsDeletedBucket(bucket))
        {
            return ValidationError(command, "deleted_bucket_protected", "Deleted is not a capture target.");
        }

        if (string.IsNullOrWhiteSpace(payload.Source))
        {
            return ValidationError(command, "slip_source_required", "A slip source is required.");
        }

        var note = store.AddNote(
            bucket,
            payload.Text,
            payload.Source,
            payload.SessionId,
            payload.CapturedAtUtc?.UtcDateTime,
            title: payload.Title,
            listKind: payload.ListKind);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket, note);
        Publish(project, ZetlChangeKind.Created, ZetlEntityKind.Slip, note.Id, note.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope UpdateSlip(ZetlCommandEnvelope command)
    {
        var found = FindNote(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Slip, command.TargetId!);
        }

        var (project, bucket, note) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Slip,
            note.Id,
            note.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket, note));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<UpdateSlipCommand>(command);
        var title = payload.Title ?? note.Title;
        // A structural note (divider, etc.) is content-less by design, so it is exempt
        // from the title-or-note rule — otherwise toggling its visibility or alignment,
        // which re-sends its empty text, would be rejected.
        var resultingKind = payload.ListKind ?? note.ListKind;
        if (!ZetlViewRenderer.IsStructuralKind(resultingKind)
            && string.IsNullOrWhiteSpace(payload.Text)
            && string.IsNullOrWhiteSpace(title)
            && !note.IsImage)
        {
            return ValidationError(command, "slip_content_required", "A slip title or note is required.");
        }

        store.UpdateNote(
            note, payload.Text, payload.Title, payload.ExcludedFromViews,
            payload.Align, payload.ListKind, payload.Checked);
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket, note);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Slip, note.Id, note.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope MoveSlip(ZetlCommandEnvelope command)
    {
        var found = FindNote(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Slip, command.TargetId!);
        }

        var (project, source, note) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Slip,
            note.Id,
            note.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(source, note));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<MoveSlipCommand>(command);
        var destination = project.Buckets.FirstOrDefault(bucket =>
            bucket.Id == payload.DestinationBucketId);
        if (destination is null)
        {
            return NotFound(command, ZetlEntityKind.Bucket, payload.DestinationBucketId);
        }

        if (!store.MoveNote(project, note, destination))
        {
            return ValidationError(
                command,
                "slip_move_invalid",
                "The slip is already in the destination bucket.");
        }

        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(destination, note);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Slip, note.Id, note.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope ReorderSlip(ZetlCommandEnvelope command)
    {
        var found = FindNote(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Slip, command.TargetId!);
        }

        var (project, bucket, note) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Slip,
            note.Id,
            note.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket, note));
        if (conflict is not null)
        {
            return conflict;
        }

        var payload = Payload<ReorderSlipCommand>(command);
        if (payload.BeforeSlipId is not null
            && bucket.Slips.All(item => item.Id != payload.BeforeSlipId))
        {
            return ValidationError(
                command,
                "slip_reorder_anchor_invalid",
                "The reorder anchor must be another slip in the same bucket.");
        }

        if (!store.ReorderNote(project, note, payload.BeforeSlipId))
        {
            return ValidationError(
                command,
                "slip_reorder_invalid",
                "The slip could not be reordered.");
        }

        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket, note);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Slip, note.Id, note.Revision);
        return Success(command, project, snapshot);
    }

    private ZetlResponseEnvelope DeleteSlip(ZetlCommandEnvelope command)
    {
        var found = FindNote(command.ProjectId!, command.TargetId!);
        if (found is null)
        {
            return NotFound(command, ZetlEntityKind.Slip, command.TargetId!);
        }

        var (project, bucket, note) = found.Value;
        var conflict = CheckRevision(
            command,
            ZetlEntityKind.Slip,
            note.Id,
            note.Revision,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket, note));
        if (conflict is not null)
        {
            return conflict;
        }

        if (ZetlStateStore.IsDeletedBucket(bucket))
        {
            return ValidationError(
                command,
                "slip_already_deleted",
                "The slip is already in Deleted.");
        }

        var deleted = store.GetDeletedBucket(project);
        if (!store.MoveNote(project, note, deleted))
        {
            return ValidationError(
                command,
                "slip_delete_invalid",
                "The slip could not be moved to Deleted.");
        }

        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(deleted, note);
        Publish(project, ZetlChangeKind.Updated, ZetlEntityKind.Slip, note.Id, note.Revision);
        return Success(command, project, snapshot);
    }

    private void ApplyBucketDefinition(
        ZetlProject project,
        ZetlBucket bucket,
        string name,
        string? parentBucketId,
        ZetlBucketSettings settings)
    {
        store.UpdateBucket(
            project,
            bucket,
            name,
            parentBucketId,
            settings.Kind,
            settings.DefaultKind,
            settings.DefaultCompileMode,
            settings.DefaultStartingText,
            settings.DefaultTsvRowLength,
            settings.PopMode,
            settings.ReplayReviewBucketId);
    }

    private ZetlResponseEnvelope? CheckRevision<T>(
        ZetlCommandEnvelope command,
        ZetlEntityKind kind,
        string targetId,
        long actualRevision,
        T current)
    {
        if (command.ExpectedTargetRevision == actualRevision)
        {
            return null;
        }

        return new ZetlResponseEnvelope
        {
            CommandId = command.CommandId,
            Status = ZetlResponseStatus.Conflict,
            ProjectId = command.ProjectId,
            ProjectChangeSequence = FindProject(command.ProjectId!)?.ChangeSequence,
            Conflict = new ZetlConflict
            {
                TargetKind = kind,
                TargetId = targetId,
                ExpectedRevision = command.ExpectedTargetRevision!.Value,
                ActualRevision = actualRevision,
                Current = ZetlProtocolJson.ToElement(current)
            }
        };
    }

    private ZetlProject? FindProject(string projectId)
    {
        return store.State.Projects.FirstOrDefault(project => project.Id == projectId);
    }

    private (ZetlProject Project, ZetlBucket Bucket)? FindBucket(
        string projectId,
        string bucketId)
    {
        var project = FindProject(projectId);
        var bucket = project?.Buckets.FirstOrDefault(item => item.Id == bucketId);
        return project is null || bucket is null ? null : (project, bucket);
    }

    private (ZetlProject Project, ZetlBucket Bucket, ZetlSlip Slip)? FindNote(
        string projectId,
        string noteId)
    {
        var project = FindProject(projectId);
        if (project is null)
        {
            return null;
        }

        foreach (var bucket in project.Buckets)
        {
            var slip = bucket.Slips.FirstOrDefault(item => item.Id == noteId);
            if (slip is not null)
            {
                return (project, bucket, slip);
            }
        }

        return null;
    }

    private static bool CreatesParentCycle(
        ZetlProject project,
        string bucketId,
        string? parentBucketId)
    {
        var currentId = parentBucketId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (currentId is not null && visited.Add(currentId))
        {
            if (currentId == bucketId)
            {
                return true;
            }

            currentId = project.Buckets
                .FirstOrDefault(bucket => bucket.Id == currentId)
                ?.ParentBucketId;
        }

        return false;
    }

    private static T Payload<T>(ZetlCommandEnvelope command)
    {
        if (command.Payload is null)
        {
            throw new JsonException($"The {command.Kind} payload is missing.");
        }

        return command.Payload.Value.Deserialize<T>(ZetlProtocolJson.Options)
            ?? throw new JsonException($"The {command.Kind} payload is missing or invalid.");
    }

    private ZetlResponseEnvelope Success(
        ZetlCommandEnvelope command,
        ZetlProject? project = null)
    {
        return new ZetlResponseEnvelope
        {
            CommandId = command.CommandId,
            Status = ZetlResponseStatus.Success,
            ProjectId = project?.Id ?? command.ProjectId,
            ProjectChangeSequence = project?.ChangeSequence
        };
    }

    private ZetlResponseEnvelope Success<T>(
        ZetlCommandEnvelope command,
        ZetlProject? project = null,
        T payload = default!)
    {
        return new ZetlResponseEnvelope
        {
            CommandId = command.CommandId,
            Status = ZetlResponseStatus.Success,
            ProjectId = project?.Id ?? command.ProjectId,
            ProjectChangeSequence = project?.ChangeSequence,
            Payload = payload is null ? null : ZetlProtocolJson.ToElement(payload)
        };
    }

    private ZetlResponseEnvelope NotFound(
        ZetlCommandEnvelope command,
        ZetlEntityKind kind,
        string id)
    {
        return ErrorResponse(
            command,
            ZetlResponseStatus.NotFound,
            $"{kind.ToString().ToLowerInvariant()}_not_found",
            $"{kind} '{id}' was not found.");
    }

    private ZetlResponseEnvelope ValidationError(
        ZetlCommandEnvelope command,
        string code,
        string message)
    {
        return ErrorResponse(command, ZetlResponseStatus.ValidationError, code, message);
    }

    private static ZetlResponseEnvelope ErrorResponse(
        ZetlCommandEnvelope command,
        ZetlResponseStatus status,
        string code,
        string message)
    {
        return new ZetlResponseEnvelope
        {
            CommandId = command.CommandId,
            Status = status,
            ProjectId = command.ProjectId,
            Error = new ZetlProtocolError
            {
                Code = code,
                Message = message
            }
        };
    }

    private ZetlResponseEnvelope Remember(
        string commandId,
        ZetlResponseEnvelope response)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return response;
        }

        completedCommands[commandId] = response;
        completedCommandOrder.Enqueue(commandId);
        while (completedCommandOrder.Count > completedCommandCapacity)
        {
            var expired = completedCommandOrder.Dequeue();
            completedCommands.Remove(expired);
        }

        return response;
    }

    private void Publish(
        ZetlProject project,
        ZetlChangeKind changeKind,
        ZetlEntityKind entityKind,
        string entityId,
        long? entityRevision)
    {
        Publish(
            project.Id,
            project.ChangeSequence,
            changeKind,
            entityKind,
            entityId,
            entityRevision);
    }

    private void Publish(
        string projectId,
        long changeSequence,
        ZetlChangeKind changeKind,
        ZetlEntityKind entityKind,
        string entityId,
        long? entityRevision)
    {
        var change = new ZetlProjectChangedEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            ProjectChangeSequence = changeSequence,
            ChangeKind = changeKind,
            EntityKind = entityKind,
            EntityId = entityId,
            EntityRevision = entityRevision
        };
        if (ProjectChanged is null)
        {
            return;
        }

        foreach (EventHandler<ZetlProjectChangedEvent> handler in ProjectChanged.GetInvocationList())
        {
            try
            {
                handler(this, change);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Project change subscriber failed: {ex.Message}");
            }
        }
    }
}
