using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CodexRedactionGate;

public static class WindowsHotkeyDemoLoop
{
    private const int HotkeyId = 0x5247;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF9 = 0x78;

    internal static string LiveAdapterKind => "verified-composer";
    internal static string DefaultHotkeyDisplayText => "Ctrl+Shift+F9";

    public static int Run(ISanitizer sanitizer, WindowsHotkeyDemoMode mode)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"status: {OsInteractionStatusIds.UnsupportedPlatform}");
            return 1;
        }

        if (mode == WindowsHotkeyDemoMode.ConfirmAndSend)
        {
            var gate = LiveOsDemoEvidence.Check();
            if (!gate.Enabled)
            {
                PrintSendGateDisabled(gate);
                return 1;
            }
        }

        if (!NativeMethods.RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModShift, VkF9))
        {
            Console.WriteLine("status: hotkey_register_failed");
            Console.WriteLine($"win32_error: {Marshal.GetLastPInvokeError()}");
            return 1;
        }

        try
        {
            Console.WriteLine("status: hotkey_ready");
            Console.WriteLine($"hotkey: {DefaultHotkeyDisplayText}");
            Console.WriteLine($"mode: {mode}");
            Console.WriteLine($"adapter: {LiveAdapterKind}");
            Console.WriteLine("Press Ctrl+C in this console to stop.");

            var liveAdapter = new WindowsVerifiedComposerSurfaceAdapter();
            var orchestrator = new OsInteractionOrchestrator(
                sanitizer,
                WindowsFocusedComposerDiscovery.CreateDefault(),
                liveAdapter,
                liveAdapter,
                liveAdapter,
                new WindowsConfirmationOverlay());

            int getMessageResult;
            while ((getMessageResult = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                if (message.message != WmHotkey || message.wParam.ToInt32() != HotkeyId)
                {
                    continue;
                }

                Console.WriteLine("status: hotkey_triggered");
                var result = orchestrator.RunOnce(mode switch
                {
                    WindowsHotkeyDemoMode.DryRun => OsInteractionRunOptions.DryRunOnly,
                    WindowsHotkeyDemoMode.ApplyOnly => OsInteractionRunOptions.ApplyOnly,
                    WindowsHotkeyDemoMode.ConfirmAndSend => OsInteractionRunOptions.ConfirmAndSend,
                    _ => OsInteractionRunOptions.ApplyOnly
                });

                if (mode == WindowsHotkeyDemoMode.DryRun && result.ConfirmationModel is not null)
                {
                    _ = new WindowsConfirmationOverlay().RequestConfirmation(result.ConfirmationModel);
                }

                PrintRawFreeResult(result);
                if (mode == WindowsHotkeyDemoMode.ApplyOnly
                    && result.Applied
                    && result.Surface is not null
                    && result.Surface.ProfileId is "codex-desktop" or "chatgpt-desktop" or "redaction-gate-demo")
                {
                    LiveOsDemoEvidence.MarkApplyOnlyPassed(result.Surface.ProfileId);
                    Console.WriteLine("apply_evidence_written: true");
                }
            }

            if (getMessageResult < 0)
            {
                Console.WriteLine("status: hotkey_message_loop_failed");
                Console.WriteLine($"win32_error: {Marshal.GetLastPInvokeError()}");
                return 1;
            }

            return 0;
        }
        finally
        {
            NativeMethods.UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }
    }

    public enum WindowsHotkeyDemoMode
    {
        DryRun,
        ApplyOnly,
        ConfirmAndSend
    }

    private static void PrintSendGateDisabled(LiveOsDemoSendGateResult gate)
    {
        Console.WriteLine($"status: {gate.Status}");
        Console.WriteLine("send_mode: disabled");
        Console.WriteLine("reason: live_confirm_and_send_requires_apply_only_evidence_and_explicit_enable");
        foreach (var item in gate.Diagnostics)
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }
    }

    private static void PrintRawFreeResult(OsInteractionResult result)
    {
        Console.WriteLine($"status: {result.Status}");
        if (result.Surface is not null)
        {
            Console.WriteLine($"profile_id: {result.Surface.ProfileId}");
        }

        if (result.SanitizationResult is not null)
        {
            Console.WriteLine($"decision: {FormatDecision(result.SanitizationResult.Decision)}");
            Console.WriteLine($"replacement_count: {result.SanitizationResult.Replacements.Count}");
        }

        Console.WriteLine($"applied: {result.Applied.ToString().ToLowerInvariant()}");
        Console.WriteLine($"submitted: {result.Submitted.ToString().ToLowerInvariant()}");
    }

    private static string FormatDecision(SanitizeDecision decision)
    {
        return decision switch
        {
            SanitizeDecision.Allow => "allow",
            SanitizeDecision.Confirm => "confirm",
            SanitizeDecision.Block => "block",
            _ => decision.ToString()
        };
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }
}
