using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace ZETL;

// The held Ctrl+X / Ctrl+C note capture dialog: a project selector (with a
// "+ New project…" option), an Activate toggle, a bucket picker, and inline
// bucket creation. Internal because it consumes Zetl.Core's internal types.
internal partial class NoteCaptureWindow : ZetlPopupWindow
{
    private readonly ZetlStateStore store = null!;
    private readonly ZetlProject project = null!;
    // Whether the chosen project should be active by default (held copy) versus
    // left inactive for a throwaway jot (held cut).
    private readonly bool activateByDefault;
    // The project that was active before the dialog opened (null when none),
    // which the Activate toggle reflects.
    private readonly string? preDialogActiveProjectId;
    // The "+ New project…" sentinel at the end of the selector; choosing it
    // reveals the name box and files into a freshly created project.
    private ZetlProject? newProjectSentinel;
    private ZetlBucket? lastFullProjectBucket;
    private bool completionDecided;
    // The project the note will be filed into; follows the selector.
    private ZetlProject selectedProject = null!;
    private readonly ZetlClipboardImage? image;
    // True when the picture replaces the note editor and the text box below it
    // is a caption (pure image capture). A dual capture — clipboard text and
    // picture together — keeps the note editor as the content instead and shows
    // the picture as an attached strip.
    private readonly bool imageCaptionMode;
    private Bitmap? imagePreviewBitmap;

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
        bool noActiveProject = false,
        bool activateByDefault = false,
        ZetlClipboardImage? image = null)
    {
        this.store = store;
        this.project = project;
        this.activateByDefault = activateByDefault;
        this.image = image;
        lastFullProjectBucket = preferredBucket;
        selectedProject = project;
        preDialogActiveProjectId = noActiveProject ? null : project.Id;

        InitializeComponent();

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

        noteBox.Text = BuildInitialNoteText(text);
        imageCaptionMode = image is not null && string.IsNullOrWhiteSpace(text);
        if (imageCaptionMode)
        {
            Title = "Save Zetl Image";
            Height = 520;
            noteBox.IsVisible = false;
            imageCapturePanel.IsVisible = true;
            try
            {
                using var stream = new MemoryStream(image!.PngBytes, writable: false);
                imagePreviewBitmap = Bitmap.DecodeToWidth(stream, 900);
                captureImagePreview.Source = imagePreviewBitmap;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                captureImagePreview.Source = null;
            }
        }
        else if (image is not null)
        {
            // Dual capture: keep the note editor as the content and show the
            // clipboard picture as an attached strip below it.
            Height = 356;
            dualImagePanel.IsVisible = true;
            dualImageLabel.Text = $"Picture attached · {image.Width}×{image.Height}";
            try
            {
                using var stream = new MemoryStream(image.PngBytes, writable: false);
                imagePreviewBitmap = Bitmap.DecodeToWidth(stream, 240);
                dualImagePreview.Source = imagePreviewBitmap;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                dualImagePreview.Source = null;
            }
        }

        saveButton.Click += (_, _) => Commit(saved: true);
        cancelButton.Click += (_, _) => Commit(saved: false);
        bucketBox.SelectionChanged += (_, _) => UpdateInlineBucketLabel();
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
        Closed += (_, _) => imagePreviewBitmap?.Dispose();

        RefreshBuckets(preferredBucket);
        UpdateProjectModeControls();
    }

    // Whether the user committed (Save / Ctrl+Enter) vs cancelled (Cancel / Esc).
    public bool Saved { get; private set; }

    public ZetlBucket SelectedBucket => ((BucketDisplayItem)bucketBox.SelectedItem!).Bucket;

    public string SelectedBucketName => SelectedBucket.Name;

    public string NoteText => imageCaptionMode
        ? imageCaptionBox.Text?.Trim() ?? ""
        : noteBox.Text?.Trim() ?? "";

    // The Activate toggle decides whether the chosen project becomes the lane's
    // active project on save.
    public bool StartProject => activateProjectButton.IsChecked == true;

    public bool CreateNewProject => IsNewProjectSelected;

    public string ProjectName => IsNewProjectSelected
        ? newProjectNameBox.Text?.Trim() ?? ""
        : "";

    // The project the note will be filed into. Ignored when creating a new one.
    public ZetlProject SelectedProject => selectedProject;

    // Whether the "+ New project…" sentinel is the current selection.
    private bool IsNewProjectSelected =>
        newProjectSentinel is not null
        && ReferenceEquals(projectBox.SelectedItem, newProjectSentinel);

    // Buckets can be browsed/created only for a concrete (existing) project.
    private bool CanEditBuckets => !IsNewProjectSelected;

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
        if (imageCaptionMode)
        {
            imageCaptionBox.Focus();
            imageCaptionBox.CaretIndex = imageCaptionBox.Text?.Length ?? 0;
            return;
        }

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
    // after saving: a project being created defaults to active; an existing
    // project reflects whether it is already active, or activates by default on
    // a held copy of its own project.
    private bool ShouldActivateProject()
    {
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
        IReadOnlyList<BucketDisplayItem> items = IsNewProjectSelected
            ? GetNewProjectBucketItems()
            : store.GetBucketDisplayItems(selectedProject);
        bucketBox.ItemsSource = items;
        bucketBox.SelectedItem = items.FirstOrDefault(item => item.Bucket.Id == selectedBucket?.Id)
            ?? items.FirstOrDefault();
    }

    private void UpdateProjectModeControls()
    {
        inlineBucketPanel.IsVisible = CanEditBuckets;
        if (CanEditBuckets && bucketBox.SelectedItem is BucketDisplayItem item)
        {
            lastFullProjectBucket = item.Bucket;
        }
    }

    private IReadOnlyList<BucketDisplayItem> GetNewProjectBucketItems()
    {
        var bucketNames = store.Defaults.ResolvedProjectBuckets
            .Append(ZetlStateStore.ScratchBucketName)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return bucketNames
            .Select(name => new BucketDisplayItem(new ZetlBucket { Id = name, Name = name }, name))
            .ToList();
    }
}
