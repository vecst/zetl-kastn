using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaFact]
    public void TreeHoverKeepsOneMarkerForBothSidesOfTheSameGap()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            project = project with { ChangeSequence = 2, Slips = [project.Slips[0], project.Slips[1], project.Slips[1] with { Id = "render-three", Text = "third" }] };
            PublishRenderSnapshot(controller, project);
            window.UpdateLayout();
            SetTreeDrag(window, "render-three");
            var before = TreeRow(window, "render-two");
            var notifications = 0;
            window.treeProjection.Find("render-two")!.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName is "ShowDropBefore" or "ShowDropAfter") notifications++;
            };
            Assert.Equal(DragDropEffects.Move, HoverTree(window, TreeRow(window, "render-one"), .8).DragEffects);
            Assert.Equal(KastnDropEdge.Before, window.treeProjection.Find("render-two")!.DropEdge);
            Assert.Equal(KastnDropEdge.None, window.treeProjection.Find("render-one")!.DropEdge);
            var initialNotifications = notifications;
            for (var i = 0; i < 4; i++)
            {
                HoverTree(window, before, .2);
                HoverTree(window, TreeRow(window, "render-one"), .8);
            }
            Assert.Equal(initialNotifications, notifications);
            Assert.Equal(KastnDropEdge.Before, window.treeProjection.Find("render-two")!.DropEdge);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void TreeHoverUnchangedOrInvalidTargetsClearThePreviousSlotWhileGapsKeepIt()
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            window.UpdateLayout();
            SetTreeDrag(window, "render-one");
            var second = TreeRow(window, "render-two");
            Assert.Equal(DragDropEffects.Move, HoverTree(window, second, .8).DragEffects);
            Assert.Equal(KastnDropEdge.After, window.treeProjection.Find("render-two")!.DropEdge);
            Assert.Equal(DragDropEffects.Move, HoverTree(window, window.projectTree, 0).DragEffects);
            // Before the second slip leaves the first exactly where it started.
            Assert.Equal(DragDropEffects.None, HoverTree(window, second, .2).DragEffects);
            Assert.Equal(KastnDropEdge.None, window.treeProjection.Find("render-two")!.DropEdge);
            Assert.Null(WindowField<KastnDropPlan?>(window, "stickyDropPlan"));
            HoverTree(window, second, .8);
            Assert.Equal(DragDropEffects.None, HoverTree(window, TreeRow(window, "render-one"), .2).DragEffects);
            Assert.Null(WindowField<KastnDropPlan?>(window, "stickyDropPlan"));
        }
        finally { CloseWindow(window); }
    }

    private static Control TreeRow(MainWindow window, string id) => window.projectTree.GetVisualDescendants()
        .OfType<Control>().Single(control => Equals(control.Tag, "dragRow") && control.DataContext is KastnTreeNode node && node.Id == id);
    private static void SetTreeDrag(MainWindow window, string id)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("draggingNode", flags)!.SetValue(window, window.treeProjection.Find(id));
        typeof(MainWindow).GetField("draggingProjectId", flags)!.SetValue(window, WindowField<KastnProjectIndex>(window, "projectIndex").Project.Id);
    }
    private static DragEventArgs HoverTree(MainWindow window, Control row, double fraction)
    {
        var format = (DataFormat<string>)typeof(MainWindow).GetField("DragNodeFormat", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(format, WindowField<KastnTreeNode>(window, "draggingNode").Id));
        var args = new DragEventArgs(DragDrop.DragOverEvent, data, row,
            new Point(row.Bounds.Width / 2, row.Bounds.Height * fraction), KeyModifiers.None) { Source = row };
        row.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        return args;
    }
}
