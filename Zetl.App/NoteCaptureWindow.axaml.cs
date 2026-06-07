using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ZETL;

// Avalonia port of NoteCaptureForm: the held Ctrl+C / Ctrl+X note dialog. The
// store-driven behavior (bucket list, inline bucket creation, project-mode
// toggle) mirrors the WinForms version one-to-one. Internal because it consumes
// Zetl.Core's internal types.
internal partial class NoteCaptureWindow : Window
{
    private readonly ZetlStateStore store = null!;
    private readonly ZetlProject project = null!;
    private readonly bool showStartProjectToggle;
    private readonly bool scratchOnlyUntilProjectStarted;
    private readonly bool createNewProjectMode;
    private ZetlBucket? lastFullProjectBucket;
    private bool completionDecided;
    private bool deactivateCommitArmed;
    private readonly DispatcherTimer deactivateArmTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };

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
        string? projectNameDefault = null)
    {
        this.store = store;
        this.project = project;
        this.showStartProjectToggle = showStartProjectToggle;
        this.scratchOnlyUntilProjectStarted = scratchOnlyUntilProjectStarted;
        this.createNewProjectMode = createNewProjectMode;
        lastFullProjectBucket = preferredBucket;

        InitializeComponent();

        projectModePanel.IsVisible = showStartProjectToggle;
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
                : startProjectBox.IsChecked == true ? lastFullProjectBucket : store.GetScratchBucket(project);
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
        deactivateArmTimer.Tick += (_, _) =>
        {
            deactivateArmTimer.Stop();
            if (CommitOnDeactivate && IsActive && !completionDecided)
            {
                deactivateCommitArmed = true;
            }
        };
        Activated += (_, _) =>
        {
            if (!CommitOnDeactivate || completionDecided)
            {
                return;
            }

            deactivateCommitArmed = false;
            deactivateArmTimer.Stop();
            deactivateArmTimer.Start();
        };
        Deactivated += (_, _) =>
        {
            if (CommitOnDeactivate
                && deactivateCommitArmed
                && !completionDecided)
            {
                ClosedByDeactivate = true;
                Commit(saved: true);
            }
        };
        Closing += (_, _) =>
        {
            deactivateArmTimer.Stop();
            completionDecided = true;
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

    public bool CommitOnDeactivate { get; set; }

    public bool ClosedByDeactivate { get; private set; }

    public ZetlBucket SelectedBucket => ((BucketDisplayItem)bucketBox.SelectedItem!).Bucket;

    public string SelectedBucketName => SelectedBucket.Name;

    public string NoteText => noteBox.Text?.Trim() ?? "";

    public bool StartProject => !showStartProjectToggle || startProjectBox.IsChecked == true;

    public bool CreateNewProject => showStartProjectToggle && createNewProjectMode && startProjectBox.IsChecked == true;

    public string ProjectName => projectNameBox.Text?.Trim() ?? "";

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
        if (!StartProject || CreateNewProject)
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
            project,
            inlineBucketNameBox.Text,
            inlineInsideCurrentBox.IsChecked == true ? selectedBucket.Id : null);
        lastFullProjectBucket = bucket;
        RefreshBuckets(bucket);
        inlineBucketNameBox.Text = "";
        FocusNoteBox();
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
        IReadOnlyList<BucketDisplayItem> items = CreateNewProject
            ? GetNewProjectBucketItems()
            : StartProject || !scratchOnlyUntilProjectStarted
                ? store.GetBucketDisplayItems(project)
                : store.GetBucketDisplayItems(project)
                    .Where(item => string.Equals(item.Bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase))
                    .ToList();
        bucketBox.ItemsSource = items;
        bucketBox.SelectedItem = items.FirstOrDefault(item => item.Bucket.Id == selectedBucket?.Id)
            ?? items.FirstOrDefault();
    }

    private void UpdateProjectModeControls()
    {
        projectNameBox.IsEnabled = StartProject;
        inlineBucketPanel.IsVisible = StartProject && !CreateNewProject;
        if (StartProject && !CreateNewProject && bucketBox.SelectedItem is BucketDisplayItem item)
        {
            lastFullProjectBucket = item.Bucket;
        }
    }

    private IReadOnlyList<BucketDisplayItem> GetNewProjectBucketItems()
    {
        var bucketNames = store.Defaults.ProjectBuckets
            .Append("Scratch")
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (bucketNames.Count == 0)
        {
            bucketNames.Add("Inbox");
            bucketNames.Add("Scratch");
        }

        return bucketNames
            .Select(name => new BucketDisplayItem(new ZetlBucket { Id = name, Name = name }, name))
            .ToList();
    }
}
