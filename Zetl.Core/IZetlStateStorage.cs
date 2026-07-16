namespace ZETL;

// The state store owns mutation policy; this seam covers only durable I/O so
// tests can inject precise write failures without teaching domain code about
// files, locks, or platform-specific error conditions.
internal interface IZetlStateStorage
{
    ZetlState Load();

    void WriteProject(ZetlProject project);

    string WriteAsset(
        ZetlProject project,
        string contentHash,
        string extension,
        byte[] bytes);

    byte[]? ReadAsset(ZetlProject project, string relativePath);

    string? GetAssetPath(ZetlProject project, string relativePath);

    IReadOnlyList<ZetlProjectAssetFile> GetAssets(ZetlProject project);

    void WriteWorkspace(ZetlWorkspaceFile workspace);

    void RemoveProject(string projectId);
}
