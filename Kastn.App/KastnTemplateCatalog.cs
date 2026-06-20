using ZETL;

namespace KASTN;

/// <summary>
/// Kastn's view onto the shared, versioned template catalog. The template format,
/// built-in presets, validation, and the user-template store all live in Zetl.Core
/// (<see cref="ZetlTemplateDefaults"/>, <see cref="ZetlTemplateValidator"/>,
/// <see cref="ZetlTemplateStore"/>) so Zetl can reuse the exact same catalog for
/// its own New Project flow. Kastn only presents these documents and asks Zetl to
/// create projects from them.
/// </summary>
internal sealed class KastnTemplateCatalog
{
    private readonly ZetlTemplateStore store;

    public KastnTemplateCatalog(Action<string>? log = null)
        : this(new ZetlTemplateStore(log: log))
    {
    }

    public KastnTemplateCatalog(ZetlTemplateStore store)
    {
        this.store = store;
    }

    public string TemplateDirectory => store.TemplateDirectory;

    // The backing store, for authoring actions (save, duplicate, delete).
    public ZetlTemplateStore Store => store;

    /// <summary>
    /// Built-ins plus valid user templates, re-read from disk on each call so a
    /// user adding or removing a template file is reflected on the next refresh.
    /// </summary>
    public IReadOnlyList<ZetlTemplateDocument> LoadAll() => store.LoadAll();

    /// <summary>
    /// The protected built-ins, always available even with no user templates.
    /// </summary>
    public static IReadOnlyList<ZetlTemplateDocument> BuiltIns => ZetlTemplateDefaults.CreateAll();
}
