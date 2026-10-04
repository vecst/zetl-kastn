using KASTN;
using Xunit;

namespace ZETL.Tests;

public class KastnCatalogEditorSessionTests
{
    private static KastnCatalogEditorSession<ZetlCreationTypeDocument> Session(ZetlCreationTypeDocument? document = null) => new(
        document ?? new() { Name = "Draft", TemplateId = "blank" }, doc => doc.Id, (doc, id) => doc.Id = id,
        doc => ZetlCreationTypeDefaults.CreateId(doc.Name), ZetlCreationTypeValidator.Validate);

    [Fact]
    public void SessionAndSaveCapturesAreIndependentCopies()
    {
        var original = new ZetlCreationTypeDocument { Id = "mine", Name = "Original", TemplateId = "blank", ViewIds = ["markdown"] };
        var session = Session(original);
        var baseline = JsonFile.Clone(session.Document);
        session.SetBaseline(baseline);
        session.Document.ViewIds.Add("html");
        var captured = JsonFile.Clone(session.Document);
        var saved = session.Save(captured, doc => doc.Name = "Persisted").Saved!;
        Assert.Equal("Original", original.Name);
        Assert.Single(original.ViewIds);
        Assert.Equal("Original", captured.Name);
        Assert.Equal("Persisted", saved.Name);
        Assert.True(session.IsDirty(captured));
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("io")]
    [InlineData("denied")]
    public void FailedSavesKeepDraftAndStableIdForRetry(string failure)
    {
        var session = Session();
        session.SetBaseline(session.Document);
        var captured = JsonFile.Clone(session.Document);
        if (failure == "validation") captured.Name = "";
        var result = session.Save(captured, _ =>
        {
            if (failure == "io") throw new IOException("Locked");
            if (failure == "denied") throw new UnauthorizedAccessException("Denied");
        });
        Assert.Null(result.Saved);
        Assert.NotNull(result.Error);
        var id = session.Document.Id;
        Assert.NotEmpty(id);
        captured.Name = "Retry";
        Assert.Equal(id, session.Save(captured, _ => { }).Saved!.Id);
        Assert.Equal("Draft", session.Document.Name);
    }

    [Fact]
    public void SuccessfulSaveAcceptsOnlyTheSavedBaseline()
    {
        var session = Session();
        session.SetBaseline(session.Document);
        var captured = JsonFile.Clone(session.Document);
        captured.Name = "Saved";
        var result = session.Save(captured, _ => { });
        Assert.False(session.IsDirty(result.Saved!));
        var later = JsonFile.Clone(result.Saved!);
        later.Name = "Later writing";
        Assert.True(session.IsDirty(later));
    }

    [Fact]
    public void FailedPersistenceDoesNotAdvanceTheBaseline()
    {
        var session = Session(new() { Id = "mine", Name = "Original", TemplateId = "blank" });
        session.SetBaseline(session.Document);
        var captured = JsonFile.Clone(session.Document);
        captured.Name = "Changed";
        Assert.Null(session.Save(captured, _ => throw new IOException()).Saved);
        Assert.True(session.IsDirty(captured));
        Assert.False(session.IsDirty(session.Document));
    }
}
