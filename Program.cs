using System.Diagnostics;
using System.Runtime.InteropServices;
using Chordl;

namespace ZETL;

internal static partial class Program
{
    private static LowLevelKeyboardProc? hookProc;
    private static IntPtr hookId = IntPtr.Zero;
    private static ChordlProcessor? chordlProcessor;
    private static ZetlApplicationContext? appContext;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTests.Run();
        }

        ApplicationConfiguration.Initialize();

        try
        {
            var chordlConfig = ChordlConfigLoader.LoadFromDefaultLocation();
            appContext = new ZetlApplicationContext(chordlConfig.HoldDelay);
            chordlProcessor = new ChordlProcessor(
                chordlConfig.Actions,
                chordlConfig.ConfiguredKeyCodes,
                chordlConfig.RepeatSuppressionDelay,
                chordlConfig.HoldDelay,
                DispatchOriginalAction,
                appContext.OnPhysicalShortcutPassedThrough,
                appContext.OnTapDispatched,
                appContext.OnHoldDetected,
                LogEvent,
                GetClipboardSequenceNumber);

            hookProc = HookCallback;
            hookId = SetHook(hookProc);
            if (hookId == IntPtr.Zero)
            {
                MessageBox.Show(
                    "Failed to install the global keyboard hook.",
                    "Zetl",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            LogEvent($"Loaded {chordlConfig.Actions.Count} Chordl definitions.");
            Application.Run(appContext);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Zetl startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            chordlProcessor?.Dispose();
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
            }

            appContext?.Dispose();
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        var hook = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((hook.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        var vkCode = (int)hook.vkCode;
        var message = wParam.ToInt32();
        var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (chordlProcessor?.HandleKeyEvent(vkCode, isKeyDown, isKeyUp) == true)
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private static void LogEvent(string message)
    {
        appContext?.Log(message);
    }

    private static void DispatchOriginalAction(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift)
    {
        var sent = ChordlInput.SendCtrlChord(vkCode, includeShift, restoreCtrl, restoreShift, LogEvent);
        LogEvent(sent
            ? $"Sent synthetic {ChordlKeys.FormatComboName(vkCode, includeShift)}."
            : $"Failed to send synthetic {ChordlKeys.FormatComboName(vkCode, includeShift)}.");
    }
}
