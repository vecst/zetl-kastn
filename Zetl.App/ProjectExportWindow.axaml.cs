using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ZETL;

internal partial class ProjectExportWindow : Window
{
    private readonly ZetlProject project = null!;
    private readonly IReadOnlyList<ZetlProjectAssetFile> assets = [];

    public ProjectExportWindow()
    {
        InitializeComponent();
    }

    internal ProjectExportWindow(
        ZetlProject project,
        IReadOnlyList<ZetlProjectAssetFile>? assets = null)
    {
        this.project = project;
        this.assets = assets ?? [];
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        projectNameText.Text = project.Name;
        exportButton.Click += async (_, _) => await ExportAsync();
        cancelButton.Click += (_, _) => Close();
        ZetlWindowShortcuts.Enable(this, () => _ = ExportAsync(), Close);
    }

    public bool Exported { get; private set; }

    public bool IncludeCaptureOrigins => archiveExportButton.IsChecked == true;

    private async Task ExportAsync()
    {
        validationText.IsVisible = false;
        var suffix = IncludeCaptureOrigins ? "-archive" : "-clean";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Zetl Project",
            SuggestedFileName = $"{FileName(project.Name)}{suffix}.zetl.zip",
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("Zetl project package")
                {
                    Patterns = ["*.zetl.zip", "*.zip"]
                }
            ]
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            ZetlProjectExportPackage.Write(
                path,
                project,
                IncludeCaptureOrigins,
                assets);
            Exported = true;
            Close();
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException)
        {
            validationText.Text = ex.Message;
            validationText.IsVisible = true;
        }
    }

    private static string FileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "zetl-project" : cleaned;
    }
}
