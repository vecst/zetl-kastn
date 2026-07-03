using System.Text.RegularExpressions;

namespace ZETL;

/// <summary>
/// Shared implementation behind the template, view, creation-type, and theme
/// catalogs: protected built-ins plus valid user JSON documents in one
/// directory (default <c>%AppData%\Zetl\&lt;name&gt;</c>). A bad file is
/// quarantined or skipped with a diagnostic, never blocking the load, and a
/// built-in id can never be overwritten or deleted.
/// </summary>
internal sealed class ZetlDocumentStore<T> where T : class
{
    private readonly string label;
    private readonly Func<IReadOnlyList<T>> createBuiltIns;
    private readonly Func<T?, IReadOnlyList<string>> validate;
    private readonly Func<string?, bool> isBuiltIn;
    private readonly Func<T, string> idOf;
    private readonly Action<string>? log;

    public ZetlDocumentStore(
        string? directory,
        string defaultDirectoryName,
        string label,
        Func<IReadOnlyList<T>> createBuiltIns,
        Func<T?, IReadOnlyList<string>> validate,
        Func<string?, bool> isBuiltIn,
        Func<T, string> idOf,
        Action<string>? log = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            defaultDirectoryName);
        this.label = label;
        this.createBuiltIns = createBuiltIns;
        this.validate = validate;
        this.isBuiltIn = isBuiltIn;
        this.idOf = idOf;
        this.log = log;
    }

    public string Directory { get; }

    public IReadOnlyList<T> LoadAll()
    {
        var documents = createBuiltIns().ToList();
        if (!System.IO.Directory.Exists(Directory))
        {
            return documents;
        }

        string[] paths;
        try
        {
            paths = System.IO.Directory.GetFiles(Directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not list {label} directory '{Directory}': {ex.Message}");
            return documents;
        }

        var seenIds = new HashSet<string>(documents.Select(idOf), StringComparer.Ordinal);
        foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            // ReadOrQuarantine moves a corrupt file aside (a visible .corrupt-*
            // copy) and returns null, so a damaged user file degrades to "skipped"
            // rather than aborting the load.
            var document = JsonFile.ReadOrQuarantine<T>(path, log);
            if (document is null)
            {
                continue;
            }

            var errors = validate(document);
            if (errors.Count > 0)
            {
                log?.Invoke($"Ignoring invalid {label} '{name}': {string.Join("; ", errors)}");
                continue;
            }

            var id = idOf(document);
            if (isBuiltIn(id))
            {
                log?.Invoke($"Ignoring user {label} '{name}': id '{id}' is reserved by a built-in.");
                continue;
            }

            if (!seenIds.Add(id))
            {
                log?.Invoke($"Ignoring user {label} '{name}': duplicate id '{id}'.");
                continue;
            }

            documents.Add(document);
        }

        return documents;
    }

    /// <summary>
    /// Writes a user document as JSON. The document must be valid and must not use
    /// a built-in id, so authoring can never overwrite a protected preset. The file
    /// is named from the (stable) id, so renaming a document never moves its file.
    /// </summary>
    public void Save(T document)
    {
        var errors = validate(document);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        if (isBuiltIn(idOf(document)))
        {
            throw new InvalidOperationException($"Built-in {label}s cannot be overwritten.");
        }

        JsonFile.WriteAtomic(PathFor(idOf(document)), document);
    }

    /// <summary>
    /// Removes a user document file. Built-ins are never deletable. A missing file
    /// is a no-op so a double-delete is harmless.
    /// </summary>
    public void Delete(string id)
    {
        if (isBuiltIn(id))
        {
            throw new InvalidOperationException($"Built-in {label}s cannot be deleted.");
        }

        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string PathFor(string id)
    {
        var safeId = string.Concat(id.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        return Path.Combine(Directory, $"{safeId}.json");
    }
}

/// <summary>
/// Stable, filesystem-safe ids for user-authored catalog documents: a slug from
/// the display name plus a short uniqueness suffix, shared by every document
/// kind so user files never collide.
/// </summary>
internal static partial class ZetlDocumentId
{
    public static string Create(string name, string fallback)
    {
        var slug = NonSlugCharacters().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = fallback;
        }

        slug = slug[..Math.Min(slug.Length, 30)];
        return $"{slug}-{Guid.NewGuid():N}"[..(slug.Length + 9)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
