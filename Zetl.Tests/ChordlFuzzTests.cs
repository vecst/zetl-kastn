using System;
using System.Collections.Generic;
using System.Linq;
using Chordl;
using static Chordl.ChordlKeys;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ChordlFuzzTests
{
    [Fact] public void Run()
    {
        Console.WriteLine("Running Chordl Input Fuzz Test...");

        var chordlMap = new Dictionary<ChordlChord, ChordlAction>
        {
            [new ChordlChord(VK_B, Ctrl: true, Shift: false)] = new("Ctrl+B", ChordlDispatchMode.TapOnly, ReplayShift: false),
            [new ChordlChord(VK_C, Ctrl: true, Shift: false)] = new("Ctrl+C", ChordlDispatchMode.None, ReplayShift: false),
            [new ChordlChord(VK_C, Ctrl: true, Shift: true)] = new("Ctrl+Shift+C", ChordlDispatchMode.Immediate, ReplayShift: false),
            [new ChordlChord(VK_P, Ctrl: true, Shift: false)] = new("Ctrl+P", ChordlDispatchMode.TapOnly, ReplayShift: false),
            [new ChordlChord(VK_V, Ctrl: true, Shift: false)] = new("Ctrl+V", ChordlDispatchMode.TapOnly, ReplayShift: false)
        };
        var keys = chordlMap.Keys.Select(chord => chord.KeyCode).ToHashSet();

        var dispatched = new List<int>();
        var passThrough = new List<ChordlEventContext>();
        var taps = new List<ChordlEventContext>();
        var holds = new List<ChordlEventContext>();

        using var processor = new ChordlProcessor(
            chordlMap,
            keys,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(60),
            (key, _, _, _) => dispatched.Add(key),
            passThrough.Add,
            context =>
            {
                taps.Add(context);
                return true;
            },
            holds.Add,
            _ => { },
            () => 0);

        var random = new Random(42); // deterministic seed
        var vks = new[] { VK_CONTROL, VK_SHIFT, VK_B, VK_C, VK_P, VK_V, VK_X, VK_A, VK_Z };
        var activeKeys = new HashSet<int>();

        for (int i = 0; i < 10000; i++)
        {
            int vk = vks[random.Next(vks.Length)];
            bool isKeyDown = random.NextDouble() > 0.4; // 60% chance of key down
            bool isRepeat = isKeyDown && activeKeys.Contains(vk) && random.NextDouble() > 0.5;

            if (isKeyDown)
            {
                activeKeys.Add(vk);
            }
            else
            {
                if (!activeKeys.Contains(vk))
                {
                    // If trying to release a key that isn't down, maybe force a down first, 
                    // or just send a spurious release (which the hook might see in reality).
                    isKeyDown = false; 
                }
                activeKeys.Remove(vk);
            }

            try
            {
                processor.HandleKeyEvent(vk, isKeyDown, !isKeyDown, isRepeat);
            }
        catch (Exception ex)
        {
            Console.WriteLine($"Fuzzing failed on iteration {i} with key {vk}, Down={isKeyDown}, Repeat={isRepeat}");
            Console.WriteLine(ex.ToString());
            Assert.Fail("Fuzzing failed");
        }
    }

    Console.WriteLine($"Fuzzing passed! Dispatched: {dispatched.Count}, Taps: {taps.Count}, Holds: {holds.Count}, PassThrough: {passThrough.Count}");
    }
}
