namespace ZETL;

// The state store owns mutation policy. These narrow durable-I/O seams keep
// project content, workspace pointers, and destructive directory lifecycle
// independent so transaction policy can evolve without widening ordinary
// project writes.
internal interface IZetlStateLoader
{
    ZetlState Load();
}

internal interface IZetlProjectStorage
{
    void WriteProject(ZetlProject project);

    string WriteAsset(
        ZetlProject project,
        string contentHash,
        string extension,
        byte[] bytes);

    byte[]? ReadAsset(ZetlProject project, string relativePath);

    IReadOnlyList<ZetlProjectAssetFile> GetAssets(ZetlProject project);
}

internal interface IZetlWorkspaceStorage
{
    void WriteWorkspace(ZetlWorkspaceFile workspace);
}

internal interface IZetlProjectDirectoryLifecycle
{
    IZetlProjectRemoval PrepareProjectRemoval(string projectId);

    void RemoveProject(string projectId)
    {
        using var removal = PrepareProjectRemoval(projectId);
        removal.Commit();
    }
}

internal interface IZetlProjectRemoval : IDisposable
{
    void Commit();

    void RollBack();
}

// Production storage and existing fault fakes use this composite adapter.
// ZetlStateStore immediately views it through the four narrow contracts above.
internal interface IZetlStateStorage :
    IZetlStateLoader,
    IZetlProjectStorage,
    IZetlWorkspaceStorage,
    IZetlProjectDirectoryLifecycle
{
}
