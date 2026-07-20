using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexRedactionGate;

public sealed record ForegroundWindowSnapshot(
    bool Succeeded,
    string Status,
    string WindowTitle,
    string ProcessName,
    string ClassName,
    IntPtr WindowHandle);

public interface IForegroundWindowSnapshotProvider
{
    ForegroundWindowSnapshot GetForegroundWindow();
}

public sealed class WindowsActiveSurfaceDiscovery : IActiveTextSurfaceDiscovery
{
    private readonly SurfaceProfileCatalog _profiles;
    private readonly IForegroundWindowSnapshotProvider _snapshotProvider;

    public WindowsActiveSurfaceDiscovery(
        SurfaceProfileCatalog profiles,
        IForegroundWindowSnapshotProvider snapshotProvider)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    public static WindowsActiveSurfaceDiscovery CreateDefault()
    {
        return new WindowsActiveSurfaceDiscovery(
            SurfaceProfileCatalog.Default,
            OperatingSystem.IsWindows()
                ? new NativeForegroundWindowSnapshotProvider()
                : new UnsupportedForegroundWindowSnapshotProvider());
    }

    public TextSurfaceDiscoveryResult DiscoverActiveSurface()
    {
        var snapshot = _snapshotProvider.GetForegroundWindow();
        if (!snapshot.Succeeded)
        {
            return TextSurfaceDiscoveryResult.Failure(
                snapshot.Status,
                new Dictionary<string, string> { ["platform_status"] = snapshot.Status });
        }

        var match = _profiles.Match(snapshot.WindowTitle, snapshot.ProcessName);
        if (!match.Matched || match.Profile is null)
        {
            return TextSurfaceDiscoveryResult.Failure(
                match.Status,
                Merge(
                    match.Diagnostics,
                    ("class_name_length", snapshot.ClassName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        var surface = new TextSurfaceDescriptor(
            SurfaceId: $"foreground:{match.Profile.ProfileId}",
            ProfileId: match.Profile.ProfileId,
            DisplayName: match.Profile.DisplayName,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string>
            {
                ["read_strategy"] = match.Profile.ReadStrategy,
                ["write_strategy"] = match.Profile.WriteStrategy,
                ["submit_strategy"] = match.Profile.SubmitStrategy,
                ["window_handle"] = snapshot.WindowHandle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                ["window_title_length"] = snapshot.WindowTitle.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["process_name_length"] = snapshot.ProcessName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["class_name_length"] = snapshot.ClassName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        return TextSurfaceDiscoveryResult.Success(surface, match.Diagnostics);
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> diagnostics,
        params (string Key, string Value)[] values)
    {
        var merged = new Dictionary<string, string>(diagnostics, StringComparer.Ordinal);
        foreach (var value in values)
        {
            merged[value.Key] = value.Value;
        }

        return merged;
    }
}

public sealed class UnsupportedForegroundWindowSnapshotProvider : IForegroundWindowSnapshotProvider
{
    public ForegroundWindowSnapshot GetForegroundWindow()
    {
        return new ForegroundWindowSnapshot(
            false,
            OsInteractionStatusIds.UnsupportedPlatform,
            string.Empty,
            string.Empty,
            string.Empty,
            IntPtr.Zero);
    }
}

public sealed class NativeForegroundWindowSnapshotProvider : IForegroundWindowSnapshotProvider
{
    public ForegroundWindowSnapshot GetForegroundWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ForegroundWindowSnapshot(false, OsInteractionStatusIds.UnsupportedPlatform, string.Empty, string.Empty, string.Empty, IntPtr.Zero);
        }

        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return new ForegroundWindowSnapshot(false, OsInteractionStatusIds.UnsupportedSurface, string.Empty, string.Empty, string.Empty, IntPtr.Zero);
        }

        var title = GetWindowText(handle);
        var className = GetClassName(handle);
        var processName = GetProcessName(handle);

        return new ForegroundWindowSnapshot(true, OsInteractionStatusIds.SupportedSurface, title, processName, className, handle);
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        var builder = new StringBuilder(Math.Max(length + 1, 1));
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetProcessName(IntPtr handle)
    {
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
