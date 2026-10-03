using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTreeHoverAcrossRowAndChildBoundariesKeepsTheGapMarker(bool horizontal)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            project = project with { ChangeSequence = 2, Slips = [project.Slips[0], project.Slips[1], project.Slips[1] with { Id = "render-three", Text = "third" }] };
            PublishRenderSnapshot(controller, project);
            window.UpdateLayout();
            SetTreeDrag(window, "render-three");
            var first = TreeRow(window, "render-one");
            var second = TreeRow(window, "render-two");
            var start = first.TranslatePoint(new Point(horizontal ? 1 : first.Bounds.Width / 2, first.Bounds.Height * .8), window)!.Value;
            var end = horizontal
                ? first.TranslatePoint(new Point(first.Bounds.Width - 1, first.Bounds.Height * .8), window)!.Value
                : second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height * .2), window)!.Value;
            var device = (IInputDevice)Activator.CreateInstance(typeof(DragDrop).Assembly.GetType("Avalonia.Input.DragDropDevice")!, nonPublic: true)!;
            var data = TreeDragData(window);
            // Seed the slot inside the content before traversing header padding.
            var initial = first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height * .8), window)!.Value;
            NativeTreeDrag(device, window, initial, data, RawDragEventType.DragEnter);
            NativeTreeDrag(device, window, start, data);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(KastnDropEdge.Before, window.treeProjection.Find("render-two")!.DropEdge);
            var notifications = 0;
            window.treeProjection.Find("render-two")!.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName is "ShowDropBefore" or "ShowDropAfter") notifications++;
            };
            var steps = (int)Math.Ceiling(Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)));
            for (var i = 1; i <= steps; i++)
            {
                var point = start + (end - start) * ((double)i / steps);
                NativeTreeDrag(device, window, point, data);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(KastnDropEdge.Before, window.treeProjection.Find("render-two")!.DropEdge);
            }
            Assert.Equal(0, notifications);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTreeExitClearsFeedbackUnlessAReentryHasReplacedIt(bool reenter)
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            window.UpdateLayout();
            SetTreeDrag(window, "render-one");
            var row = TreeRow(window, "render-two");
            var inside = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height * .8), window)!.Value;
            var outside = window.slipEditor.TranslatePoint(new Point(20, 20), window)!.Value;
            var device = (IInputDevice)Activator.CreateInstance(typeof(DragDrop).Assembly.GetType("Avalonia.Input.DragDropDevice")!, nonPublic: true)!;
            var data = TreeDragData(window);
            NativeTreeDrag(device, window, inside, data, RawDragEventType.DragEnter);
            Assert.Equal(KastnDropEdge.After, window.treeProjection.Find("render-two")!.DropEdge);
            NativeTreeDrag(device, window, outside, data);
            if (reenter) NativeTreeDrag(device, window, inside, data);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(reenter ? KastnDropEdge.After : KastnDropEdge.None, window.treeProjection.Find("render-two")!.DropEdge);
            Assert.Equal(reenter, WindowField<bool>(window, "dragPointerInsideTree"));
        }
        finally { CloseWindow(window); }
    }

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

    // Avalonia's platform drag device performs hit testing and emits leave/enter
    // events when the deepest child changes. It is hidden from the reference API.
    private static void NativeTreeDrag(IInputDevice device, MainWindow window, Point point, DataTransfer data,
        RawDragEventType type = RawDragEventType.DragOver)
    {
        var constructor = typeof(RawDragEvent).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(ctor => ctor.GetParameters()[4].ParameterType == typeof(IDataTransfer));
        device.GetType().GetMethod("ProcessRawEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(RawInputEventArgs)])!.Invoke(device,
            [constructor.Invoke([device, type, window, point, data, DragDropEffects.Move, RawInputModifiers.None])]);
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
        var data = TreeDragData(window);
        var args = new DragEventArgs(DragDrop.DragOverEvent, data, row,
            new Point(row.Bounds.Width / 2, row.Bounds.Height * fraction), KeyModifiers.None) { Source = row };
        row.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        return args;
    }
    private static DataTransfer TreeDragData(MainWindow window)
    {
        var format = (DataFormat<string>)typeof(MainWindow).GetField("DragNodeFormat", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(format, WindowField<KastnTreeNode>(window, "draggingNode").Id));
        return data;
    }
}
