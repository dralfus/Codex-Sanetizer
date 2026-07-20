using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public static class OsInteractionStatusIds
{
    public const string SupportedSurface = "supported_surface";
    public const string UnsupportedSurface = "unsupported_surface";
    public const string UnsupportedPlatform = "unsupported_platform";
    public const string AmbiguousSurface = "ambiguous_surface";
    public const string CaptureFailed = "capture_failed";
    public const string WriteFailed = "write_failed";
    public const string SubmitFailed = "submit_failed";
    public const string VerificationFailed = "verification_failed";
    public const string FocusLost = "focus_lost";
    public const string StaleComposer = "stale_composer";
    public const string DryRunAllow = "dry_run_allow";
    public const string DryRunConfirm = "dry_run_confirm";
    public const string Blocked = "blocked";
    public const string Canceled = "canceled";
    public const string Applied = "applied";
    public const string Submitted = "submitted";
    public const string FailedClosed = "failed_closed";
    public const string SafetyDisabled = "safety_disabled";
    public const string NotComposer = "not_composer";
    public const string SupportedComposer = "supported_composer";
    public const string EvidenceMissing = "evidence_missing";
    public const string Protected = "protected";
    public const string NotConfigured = "not_configured";
    public const string BindingUnknown = "binding_unknown";
    public const string SurfaceUnverified = "surface_unverified";
    public const string DegradedHotkeyOnly = "degraded_hotkey_only";
    public const string NativeSubmitGuarded = "native_submit_guarded";
    public const string NativeSubmitPassThrough = "native_submit_pass_through";
    public const string EmergencyDisabled = "emergency_disabled";
    public const string EnterpriseBlocked = "enterprise_blocked";
}

public sealed record TextSurfaceDescriptor(
    string SurfaceId,
    string ProfileId,
    string DisplayName,
    bool Supported,
    bool CanCaptureText,
    bool CanReplaceText,
    bool CanSubmit,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record TextSurfaceDiscoveryResult(
    bool Succeeded,
    string Status,
    TextSurfaceDescriptor? Surface,
    IReadOnlyDictionary<string, string> Diagnostics)
{
    public static TextSurfaceDiscoveryResult Failure(string status, IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return new TextSurfaceDiscoveryResult(false, status, null, diagnostics ?? EmptyDiagnostics);
    }

    public static TextSurfaceDiscoveryResult Success(TextSurfaceDescriptor surface, IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return new TextSurfaceDiscoveryResult(true, OsInteractionStatusIds.SupportedSurface, surface, diagnostics ?? EmptyDiagnostics);
    }

    private static IReadOnlyDictionary<string, string> EmptyDiagnostics { get; } = new Dictionary<string, string>();
}

public sealed record TextCaptureResult(
    bool Succeeded,
    string Status,
    string? Text,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record TextReplacementResult(
    bool Succeeded,
    string Status,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record SubmitActionResult(
    bool Succeeded,
    string Status,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record HotkeyBinding(
    string Id,
    string DisplayText,
    string Scope);

public interface IActiveTextSurfaceDiscovery
{
    TextSurfaceDiscoveryResult DiscoverActiveSurface();
}

public interface ITextSurfaceReader
{
    TextCaptureResult CaptureText(TextSurfaceDescriptor surface);
}

public interface ITextSurfaceWriter
{
    TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text);
}

public interface ISubmitAction
{
    SubmitActionResult Submit(TextSurfaceDescriptor surface);
}

public interface IHotkeyTrigger
{
    HotkeyBinding Binding { get; }
}

public interface IConfirmationOverlay
{
    ConfirmationDecision RequestConfirmation(ConfirmationUiModel model);
}
