using Avalonia.Controls;
using Avalonia.Input;

namespace ZETL;

// Avalonia port of NoteCaptureForm: the held Ctrl+C / Ctrl+X note dialog. The
// store-driven behavior (bucket list, inline bucket creation, project-mode
// toggle) mirrors the WinForms version one-to-one. Internal because it consumes
// Zetl.Core's internal types.
internal partial class NoteCaptureWindow : ZetlPopupWindow
{
    private readonly ZetlStateStore store = null!;
    private readonly ZetlProject project = null!;
    private readonly bool showStartProjectToggle;
    private readonly bool scratchOnlyUntilProjectStarted;
    private readonly bool createNewProjectMode;
    private readonly bool quickNote;
    // Whether the chosen project should be active by default (held copy) versus
    // left inactive for a throwaway jot (held cut).
    private readonly bool activateByDefault;
    private readonly string? preDialogActiveProjectId;
    // The "+ New project…" sentinel shown at the end of the selector. Choosing
    // it reveals the name box and files into a freshly created project.
    private ZetlProject? newProjectSentinel;
    private ZetlBucket? lastFullProjectBucket;
    private bool completionDecided;
    // The project the note will be filed into. Starts as the request's project
    // and follows the project selector on the quick-note path.
    private ZetlProject selectedProject = null!;

    // Parameterless ctor for the Avalonia previewer / XAML tooling.
    public NoteCaptureWindow()
    {
        InitializeComponent();
    }

    internal NoteCaptureWindow(
        ZetlStateStore store,
        ZetlProject project,
        ZetlBucket? preferredBucket,
        string text,
        bool showStartProjectToggle = false,
        bool startProjectDefault = true,
        bool scratchOnlyUntilProjectStarted = false,
        bool createNewProjectMode = false,
        string? projectToggleText = null,
        string? projectNameDefault = null,
        bool quickNote = true)
    {
        this.store = store;
        this.project = project;
        this.showStartProjectToggle = showStartProjectToggle;
        this.scratchOnlyUntilProjectStarted = scratchOnlyUntilProjectStarted;
        this.createNewProjectMode = createNewProjectMode;
        this.quickNote = quickNote;
        activateByDefault = startProjectDefault;
        lastFullProjectBucket = preferredBucket;
        selectedProject = project;
        // The project that was active before the dialog opened (null when none).
        // Drives the Activate toggle's reflected state on the quick-note path.
        preDialogActiveProjectId = showStartProjectToggle ? null : project.Id;

        InitializeComponent();

        // Held cut and held copy share this dialog: an always-visible project
        // selector plus Activate toggle with full bucket access throughout.
        projectSelectorPanel.IsVisible = quickNote;
        projectModePanel.IsVisible = !quickNote && showStartProjectToggle;
        if (quickNote)
        {
            var projects = store.State.Projects
                .Where(item => !string.Equals(item.Name, ZetlStateStore.LogProjectName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            newProjectSentinel = new ZetlProject { Id = "", Name = "+ New project…" };
            projectBox.ItemsSource = new List<ZetlProject>(projects) { newProjectSentinel };
            projectBox.SelectedItem = projects.FirstOrDefault(item => item.Id == project.Id)
                ?? projects.FirstOrDefault()
                ?? newProjectSentinel;
            activateProjectButton.IsCheckedChanged += (_, _) => UpdateActivateButtonText();
            activateProjectButton.IsChecked = ShouldActivateProject();
            UpdateActivateButtonText();
            projectBox.SelectionChanged += (_, _) => OnSelectedProjectChanged();
        }

        startProjectBox.Content = projectToggleText ?? "Start project";
        projectNameBox.Text = projectNameDefault ?? project.Name;
        startProjectBox.IsChecked = !showStartProjectToggle || startProjectDefault;
        noteBox.Text = BuildInitialNoteText(text);

        saveButton.Click += (_, _) => Commit(saved: true);
        cancelButton.Click += (_, _) => Commit(saved: false);
        bucketBox.SelectionChanged += (_, _) => UpdateInlineBucketLabel();
        startProjectBox.IsCheckedChanged += (_, _) =>
        {
            var selectedBucket = createNewProjectMode
                ? startProjectBox.IsChecked == true ? null : lastFullProjectBucket
                : startProjectBox.IsChecked == true ? lastFullProjectBucket : store.GetScratchBucket(selectedProject);
            RefreshBuckets(selectedBucket);
            UpdateProjectModeControls();
        };
        inlineCreateButton.Click += (_, _) => CreateInlineBucket();
        inlineBucketNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CreateInlineBucket();
            }
        };
        Opened += (_, _) =>
        {
            UpdateInlineBucketLabel();
            FocusNoteBox();
        };
        ZetlWindowShortcuts.Enable(
            this,
            () => Commit(saved: true),
            () => Commit(saved: false),
            HandleAdditionalShortcut);

        RefreshBuckets(preferredBucket);
        UpdateProjectModeControls();
    }

    // Whether the user committed (Save / Ctrl+Enter) vs cancelled (Cancel / Esc).
    public bool Saved { get; private set; }

    public ZetlBucket SelectedBucket => ((BucketDisplayItem)bucketBox.SelectedItem!).Bucket;

    public string SelectedBucketName => SelectedBucket.Name;

    public string NoteText => noteBox.Text?.Trim() ?? "";

    // On the quick-note path the Activate toggle decides whether the selected
    // project becomes the lane's active project; otherwise the copy
    // Start/New-project checkbox drives it.
    public bool StartProject => quickNote
        ? activateProjectButton.IsChecked == true
        : !showStartProjectToggle || startProjectBox.IsChecked == true;

    public bool CreateNewProject => IsNewProjectSelected;

    public string ProjectName => IsNewProjectSelected
        ? newProjectNameBox.Text?.Trim() ?? ""
        : projectNameBox.Text?.Trim() ?? "";

    // The project the note will be filed into (the request's project, or the
    // one chosen in the project selector). Ignored when creating a new project.
    public ZetlProject SelectedProject => selectedProject;

    // Whether the "+ New project…" sentinel is the current selection.
    private bool IsNewProjectSelected =>
        newProjectSentinel is not null
        && ReferenceEquals(projectBox.SelectedItem, newProjectSentinel);

    // Buckets can be browsed/created for a concrete project: never while
    // creating a new one, and on the copy flow only once it is started.
    private bool CanEditBuckets => !IsNewProjectSelected && (quickNote || (StartProject && !CreateNewProject));

    protected override bool IsDismissSuppressed => completionDecided;

    protected override void OnClickAwayDismiss() => Commit(saved: true);

    protected override void OnPopupClosing() => completionDecided = true;

    private void Commit(bool saved)
    {
        completionDecided = true;
        Saved = saved;
        Close();
    }

    private void HandleAdditionalShortcut(KeyEventArgs e)
    {
        if (e.Key == Key.B
            && e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && inlineBucketPanel.IsVisible)
        {
            e.Handled = true;
            FocusInlineBucketName();
        }
    }

    private static string BuildInitialNoteText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? "" : $"{trimmed} ";
    }

    private void FocusNoteBox()
    {
        noteBox.Focus();
        noteBox.CaretIndex = noteBox.Text?.Length ?? 0;
    }

    private void FocusInlineBucketName()
    {
        UpdateInlineBucketLabel();
        inlineBucketNameBox.Focus();
        inlineBucketNameBox.SelectAll();
    }

    private void CreateInlineBucket()
    {
        if (!CanEditBuckets)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(inlineBucketNameBox.Text))
        {
            inlineBucketNameBox.Focus();
            return;
        }

        var selectedBucket = SelectedBucket;
        var bucket = store.AddBucket(
            selectedProject,
            inlineBucketNameBox.Text,
            inlineInsideCurrentBox.IsChecked == true ? selectedBucket.Id : null);
        lastFullProjectBucket = bucket;
        RefreshBuckets(bucket);
        inlineBucketNameBox.Text = "";
        FocusNoteBox();
    }

    // The Activate toggle reflects whether the chosen project should be active
    // after saving: true when it is already the active project, or when this
    // capture activates by default (held copy) and the chosen project is the
    // request's own project.
    private bool ShouldActivateProject()
    {
        // A project you are explicitly creating defaults to active.
        return IsNewProjectSelected
            || selectedProject.Id == preDialogActiveProjectId
            || (activateByDefault && selectedProject.Id == project.Id);
    }

    private void UpdateActivateButtonText()
    {
        activateProjectButton.Content = activateProjectButton.IsChecked == true
            ? "Deactivate project"
            : "Activate project";
    }

    private void OnSelectedProjectChanged()
    {
        if (IsNewProjectSelected)
        {
            newProjectNameBox.IsVisible = true;
            activateProjectButton.IsChecked = ShouldActivateProject();
            RefreshBuckets(null);
            UpdateProjectModeControls();
            UpdateInlineBucketLabel();
            newProjectNameBox.Focus();
            return;
        }

        newProjectNameBox.IsVisible = false;
        selectedProject = projectBox.SelectedItem as ZetlProject ?? project;
        activateProjectButton.IsChecked = ShouldActivateProject();
        lastFullProjectBucket = selectedProject.Buckets.FirstOrDefault(
            bucket => bucket.Id == selectedProject.ActiveBucketId);
        RefreshBuckets(lastFullProjectBucket);
        UpdateProjectModeControls();
        UpdateInlineBucketLabel();
    }

    private void UpdateInlineBucketLabel()
    {
        if (bucketBox.SelectedItem is BucketDisplayItem item)
        {
            inlineInsideCurrentBox.Content = $"Inside {item.Bucket.Name}";
        }
    }

    private void RefreshBuckets(ZetlBucket? selectedBucket)
    {
        var restrictToScratch = !quickNote
            && !CreateNewProject
            && !StartProject
            && scratchOnlyUntilProjectStarted;
        IReadOnlyList<BucketDisplayItem> items = CreateNewProject
            ? GetNewProjectBucketItems()
            : restrictToScratch
                ? store.GetBucketDisplayItems(selectedProject)
                    .Where(item => string.Equals(item.Bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : store.GetBucketDisplayItems(selectedProject);
        bucketBox.ItemsSource = items;
        bucketBox.SelectedItem = items.FirstOrDefault(item => item.Bucket.Id == selectedBucket?.Id)
            ?? items.FirstOrDefault();
    }

    private void UpdateProjectModeControls()
    {
        projectNameBox.IsEnabled = startProjectBox.IsChecked == true;
        inlineBucketPanel.IsVisible = CanEditBuckets;
        if (CanEditBuckets && bucketBox.SelectedItem is BucketDisplayItem item)
        {
            lastFullProjectBucket = item.Bucket;
        }
    }

    private IReadOnlyList<BucketDisplayItem> GetNewProjectBucketItems()
    {
        var bucketNames = store.Defaults.ResolvedProjectBuckets
            .Append("Scratch")
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return bucketNames
            .Select(name => new BucketDisplayItem(new ZetlBucket { Id = name, Name = name }, name))
            .ToList();
    }
}
