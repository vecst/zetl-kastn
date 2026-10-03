using Avalonia.Controls;

namespace KASTN;

internal static class KastnPanelReconciler
{
    // Make the panel's children match the desired sequence with minimal moves, so
    // untouched controls keep their layout and scroll state. A control arriving
    // from another panel (a card moved across columns) is detached first.
    public static void SyncChildren(Avalonia.Controls.Controls children, IReadOnlyList<Control> desired)
    {
        var desiredSet = new HashSet<Control>(desired);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(children[i]))
            {
                children.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var control = desired[i];
            if (i < children.Count && ReferenceEquals(children[i], control))
            {
                continue;
            }

            if (control.Parent is Panel elsewhere && !ReferenceEquals(elsewhere.Children, children))
            {
                elsewhere.Children.Remove(control);
            }

            var existing = children.IndexOf(control);
            if (existing >= 0)
            {
                children.Move(existing, i);
            }
            else
            {
                children.Insert(i, control);
            }
        }
    }

}
