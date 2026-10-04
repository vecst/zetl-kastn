using System.Collections.Specialized;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using ZETL.Contracts;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void HiddenDetailsDeferBacklinksAndOpenWithLatestMetadata()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            project = project with
            {
                ChangeSequence = 2,
                Slips = [project.Slips[0], project.Slips[1] with { Title = "Old source", Text = "[[render-one|Target]]" }]
            };
            PublishRenderSnapshot(controller, project);
            Assert.Empty(window.slipInspectorFieldsPanel.Children);
            var index = WindowField<KastnProjectIndex>(window, "projectIndex");
            Assert.Null(typeof(KastnProjectIndex).GetField("backlinks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index));
            project = project with
            {
                ChangeSequence = 3,
                Slips = [project.Slips[0] with { Revision = 3, Title = "Latest target" }, project.Slips[1] with { Revision = 2, Title = "Latest source" }]
            };
            PublishRenderSnapshot(controller, project);
            Assert.Empty(window.slipInspectorFieldsPanel.Children);
            ClickDetails(window);
            Assert.Contains(window.slipInspectorFieldsPanel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Latest target");
            Assert.Contains(window.slipInspectorFieldsPanel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "3");
            Assert.Contains(window.slipInspectorFieldsPanel.Children.OfType<Button>(), button => Equals(button.Content, "Latest source"));
            Assert.DoesNotContain(window.slipInspectorFieldsPanel.Children.OfType<Button>(), button => Equals(button.Content, "Old source"));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void HiddenDetailsKeepTheirControlsUntilReopenedAndRetireOldActions()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            project = project with { ChangeSequence = 2, Slips = [project.Slips[0], project.Slips[1] with { Title = "Source", Text = "[[render-one|Target]]" }] };
            PublishRenderSnapshot(controller, project);
            ClickDetails(window);
            var oldButton = Assert.Single(window.slipInspectorFieldsPanel.Children.OfType<Button>());
            var oldFields = window.slipInspectorFieldsPanel.Children.ToArray();
            window.detailEditorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            project = project with { ChangeSequence = 3, Slips = [project.Slips[0], project.Slips[1] with { Revision = 2, Text = "Link removed" }] };
            PublishRenderSnapshot(controller, project);
            Assert.Equal(oldFields, window.slipInspectorFieldsPanel.Children);
            oldButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("render-one", Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
            ClickDetails(window);
            Assert.Empty(window.slipInspectorFieldsPanel.Children.OfType<Button>());
            oldButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("render-one", Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void VisibleDetailsRefreshAndNavigationHidesMetadataBeforeRenderingDestination()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            ClickDetails(window);
            PublishRenderSnapshot(controller, project with
            {
                ChangeSequence = 2, Slips = [project.Slips[0] with { Revision = 2, Title = "Visible update" }, project.Slips[1]]
            });
            Assert.True(window.inspectorPanel.IsVisible);
            Assert.Contains(window.slipInspectorFieldsPanel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Visible update");
            var fields = window.slipInspectorFieldsPanel.Children.ToArray();
            window.projectTree.SelectedItem = window.treeProjection.Find("render-two");
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.editorPanel.IsVisible);
            Assert.False(window.inspectorPanel.IsVisible);
            Assert.Equal(fields, window.slipInspectorFieldsPanel.Children);
            ClickDetails(window);
            Assert.DoesNotContain(window.slipInspectorFieldsPanel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Visible update");
            Assert.Equal("render-two", Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailsForABatchOrBucketShowsTheEmptySelectionMessage(bool bucket)
    {
        var (window, _, project) = WorkflowWindow();
        try
        {
            ClickDetails(window);
            if (bucket) window.projectTree.SelectedItem = window.treeProjection.Find(project.Buckets[0].Id);
            else window.projectTree.SelectedItems.Add(window.treeProjection.Find("render-two"));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.slipInspectorFieldsPanel.Children);
            ClickDetails(window);
            Assert.Equal("No slip is available in the current view.", Assert.IsType<TextBlock>(Assert.Single(window.slipInspectorFieldsPanel.Children)).Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailsProjectChangesAndRemovedSelectionsDiscardStaleFields(bool anotherProject)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            ClickDetails(window);
            window.detailEditorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PublishRenderSnapshot(controller, anotherProject
                ? project with { Id = "replacement-project", Slips = [project.Slips[0] with { Title = "Replacement target" }, project.Slips[1]] }
                : project with { ChangeSequence = 2, Slips = [] });
            Assert.Empty(window.slipInspectorFieldsPanel.Children);
            ClickDetails(window);
            Assert.Contains(window.slipInspectorFieldsPanel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text ==
                (anotherProject ? "Replacement target" : "No slip is available in the current view."));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotSelectionRestorationSkipsUnchangedNodesAndReselectsReplacements(bool replaceNode)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.projectTree.SelectedItems.Add(window.treeProjection.Find("render-two"));
            var original = window.treeProjection.Find("render-one");
            var notifications = 0;
            ((INotifyCollectionChanged)window.projectTree.SelectedItems).CollectionChanged += (_, _) => notifications++;
            PublishRenderSnapshot(controller, project with
            {
                ChangeSequence = 2,
                Slips = [project.Slips[0] with { Revision = 2, Type = replaceNode ? ZetlSlipType.Picture : ZetlSlipType.Text }, project.Slips[1] with { Revision = 2 }]
            });
            Assert.Equal(new[] { "render-one", "render-two" }, window.projectTree.SelectedItems.Cast<KastnTreeNode>().Select(node => node.Id));
            if (replaceNode)
            {
                Assert.True(notifications > 0);
                Assert.NotSame(original, window.treeProjection.Find("render-one"));
                Assert.DoesNotContain(original, window.projectTree.SelectedItems.Cast<KastnTreeNode>());
            }
            else
            {
                Assert.Equal(0, notifications);
                Assert.Same(original, window.projectTree.SelectedItem);
            }
            Assert.All(window.projectTree.SelectedItems.Cast<KastnTreeNode>(), node => Assert.Equal(2, node.Slip!.Revision));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void TreeIconsShareGeometryAndUpdateKindsVisibilityAndThemeBrushesInPlace()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.UpdateLayout();
            ShapePath Icon(string id, string name) => Assert.Single(TreeRow(window, id).GetVisualDescendants().OfType<ShapePath>().Where(path => path.Classes.Contains(name)));
            var primary = Icon("render-one", "tree-node-icon");
            var visibility = Icon("render-one", "tree-visibility-icon");
            Assert.Same(primary.Data, Icon("render-two", "tree-node-icon").Data);
            Assert.Equal(2, TreeRow(window, "render-one").GetVisualDescendants().OfType<ShapePath>().Count());
            var textGeometry = primary.Data;
            var openEye = visibility.Data;
            project = project with
            {
                ChangeSequence = 2,
                Buckets = [project.Buckets[0] with { Revision = 2, RenderKind = ZetlBucketRenderKinds.Group }],
                Slips = [project.Slips[0] with { Revision = 2, BlockKind = ZetlBlockKinds.Divider, ExcludedFromViews = true }, project.Slips[1] with { Revision = 2, Type = ZetlSlipType.Picture }]
            };
            PublishRenderSnapshot(controller, project);
            window.UpdateLayout();
            Assert.Same(primary, Icon("render-one", "tree-node-icon"));
            Assert.Same(visibility, Icon("render-one", "tree-visibility-icon"));
            Assert.NotSame(textGeometry, primary.Data);
            Assert.NotSame(openEye, visibility.Data);
            Assert.Contains("accent", primary.Classes);
            Assert.DoesNotContain("accent", Icon("render-two", "tree-node-icon").Classes);
            var converter = new KastnTreeIconConverter();
            object? Geometry(KastnTreeIconKind kind) => converter.Convert(kind, typeof(Avalonia.Media.Geometry), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Same(Geometry(KastnTreeIconKind.Structural), primary.Data);
            Assert.Same(Geometry(KastnTreeIconKind.Picture), Icon("render-two", "tree-node-icon").Data);
            Assert.Same(Geometry(KastnTreeIconKind.Container), Icon(project.Buckets[0].Id, "tree-node-icon").Data);
            Assert.Same(Geometry(KastnTreeIconKind.ClosedEye), visibility.Data);
            window.Resources["ZetlAccentBrush"] = Brushes.Cyan;
            window.Resources["ZetlMutedTextBrush"] = Brushes.Yellow;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Brushes.Cyan, primary.Stroke);
            Assert.Same(Brushes.Yellow, visibility.Stroke);
            Assert.Same(Brushes.Yellow, Icon("render-two", "tree-node-icon").Stroke);
        }
        finally { CloseWindow(window); }
    }

    private static void ClickDetails(MainWindow window)
    {
        window.detailDetailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
