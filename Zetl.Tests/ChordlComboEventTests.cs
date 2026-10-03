using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Chordl;
using static Chordl.ChordlKeys;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

// The combo lifetime callbacks that drive the hold indicator.
[Collection(RealTimeCollection.Name)]
public class ChordlComboEventTests
{
    private readonly List<string> events = [];

    private ChordlProcessor CreateProcessor(int holdMs = 60)
    {
        var map = new Dictionary<ChordlChord, ChordlAction>
        {
            [new ChordlChord(VK_X, Ctrl: true, Shift: false)] = new("Ctrl+X", ChordlDispatchMode.None, ReplayShift: false),
            [new ChordlChord(VK_X, Ctrl: true, Shift: true)] = new("Ctrl+Shift+X", ChordlDispatchMode.None, ReplayShift: false),
            [new ChordlChord(VK_V, Ctrl: true, Shift: false)] = new("Ctrl+V", ChordlDispatchMode.TapOnly, ReplayShift: false)
        };
        return new ChordlProcessor(
            map,
            map.Keys.Select(chord => chord.KeyCode).ToHashSet(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(holdMs),
            (_, _, _, _) => { },
            _ => { },
            _ => false,
            context => { lock (events) { events.Add($"hold:{context.Name}"); } },
            _ => { },
            () => 0,
            context => { lock (events) { events.Add($"start:{context.Name}"); } },
            held => { lock (events) { events.Add($"end:{held}"); } });
    }

    private string Events()
    {
        lock (events)
        {
            return string.Join(",", events);
        }
    }

    [Fact] public void ATapStartsAndEndsWithoutAHold()
    {
        using var processor = CreateProcessor();
        processor.HandleKeyEvent(VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true);

        AssertEqual("start:Ctrl+V,end:False", Events(), "A tap starts the combo and ends it unheld.");
    }

    [Fact] public void AHoldReportsStartHoldAndAHeldEnd()
    {
        using var processor = CreateProcessor();
        processor.HandleKeyEvent(VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_X, isKeyDown: true, isKeyUp: false);
        Thread.Sleep(200);
        processor.HandleKeyEvent(VK_X, isKeyDown: false, isKeyUp: true);

        AssertEqual("start:Ctrl+X,hold:Ctrl+X,end:True", Events(), "The end reports that the hold fired.");
    }

    [Fact] public void AddingShiftRestartsTheCombo()
    {
        using var processor = CreateProcessor(holdMs: 5000);
        processor.HandleKeyEvent(VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_X, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_LSHIFT, isKeyDown: true, isKeyUp: false);

        AssertEqual("start:Ctrl+X,start:Ctrl+Shift+X", Events(), "The indicator restarts with the Shift combo.");
    }

    [Fact] public void ReleasingCtrlFirstEndsAHeldBackTap()
    {
        using var processor = CreateProcessor(holdMs: 5000);
        processor.HandleKeyEvent(VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false);
        processor.HandleKeyEvent(VK_LCONTROL, isKeyDown: false, isKeyUp: true);

        AssertEqual("start:Ctrl+V,end:False", Events(), "Once Ctrl is up no hold can fire, so the indicator ends.");
    }
}
