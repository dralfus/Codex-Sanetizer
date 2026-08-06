using System;
using System.Collections.Generic;
using System.Linq;

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
    public const string NativeSubmitInProgress = "native_submit_in_progress";
    public const string NativeSubmitPassThrough = "native_submit_pass_through";
    public const string NativeSubmitCrashed = "native_submit_crashed";
    public const string TraceUnavailable = "trace_unavailable";
    public const string EmergencyDisabled = "emergency_disabled";
    public const string EnterpriseBlocked = "enterprise_blocked";
    public const string NativeSubmitSetupRequired = "native_submit_setup_required";
    public const string ProgrammaticUiaInvokeUnsupported = "programmatic_uia_invoke_unsupported";
}

/// <summary>
/// Encapsulates metadata for a text surface descriptor.
/// Provides named fields instead of raw dictionary for better type safety.
/// Supports both typed fields and arbitrary key-value pairs.
/// </summary>
/// <param name="SurfaceKind">The kind of surface (e.g., "disposable_local_target").</param>
/// <param name="CloudSubmission">Whether cloud submission is enabled.</param>
/// <param name="ComposerStatus">The composer status (e.g., "supported_composer").</param>
/// <param name="WindowHandle">The window handle for identity verification (hex string).</param>
/// <param name="ElementAutomationId">The element automation ID for composer identity (optional).</param>
/// <param name="ArbitraryMetadata">Additional arbitrary key-value pairs.</param>
public sealed record SurfaceMetadata(
    string? SurfaceKind = null,
    string? CloudSubmission = null,
    string? ComposerStatus = null,
    string? WindowHandle = null,
    string? ElementAutomationId = null,
    IReadOnlyDictionary<string, string>? ArbitraryMetadata = null)
{
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (SurfaceKind is not null)
            dict["surface_kind"] = SurfaceKind;
        if (CloudSubmission is not null)
            dict["cloud_submission"] = CloudSubmission;
        if (ComposerStatus is not null)
            dict["composer_status"] = ComposerStatus;
        if (WindowHandle is not null)
            dict["window_handle"] = WindowHandle;
        if (ElementAutomationId is not null)
            dict["element_automation_id"] = ElementAutomationId;
        if (ArbitraryMetadata is not null)
        {
            foreach (var kvp in ArbitraryMetadata)
            {
                dict[kvp.Key] = kvp.Value;
            }
        }
        return dict;
    }

    public static SurfaceMetadata FromDictionary(IReadOnlyDictionary<string, string> dict)
    {
        return new SurfaceMetadata(
            SurfaceKind: dict.TryGetValue("surface_kind", out var sk) ? sk : null,
            CloudSubmission: dict.TryGetValue("cloud_submission", out var cs) ? cs : null,
            ComposerStatus: dict.TryGetValue("composer_status", out var cs2) ? cs2 : null,
            WindowHandle: dict.TryGetValue("window_handle", out var wh) ? wh : null,
            ElementAutomationId: dict.TryGetValue("element_automation_id", out var ea) ? ea : null,
            ArbitraryMetadata: dict.Where(kvp => kvp.Key is not ("surface_kind" or "cloud_submission" or "composer_status" or "window_handle" or "element_automation_id"))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));
    }

    public bool TryGetValue(string key, out string? value)
    {
        value = key switch
        {
            "surface_kind" => SurfaceKind,
            "cloud_submission" => CloudSubmission,
            "composer_status" => ComposerStatus,
            "window_handle" => WindowHandle,
            "element_automation_id" => ElementAutomationId,
            _ => ArbitraryMetadata?.TryGetValue(key, out value) == true ? value : null
        };
        return value != null || SurfaceKind != null || CloudSubmission != null || ComposerStatus != null || WindowHandle != null || ElementAutomationId != null || (ArbitraryMetadata?.Count > 0);
    }

    public string? TryGetValue(string key)
    {
        return key switch
        {
            "surface_kind" => SurfaceKind,
            "cloud_submission" => CloudSubmission,
            "composer_status" => ComposerStatus,
            "window_handle" => WindowHandle,
            "element_automation_id" => ElementAutomationId,
            _ => ArbitraryMetadata?.TryGetValue(key, out var value) == true ? value : null
        };
    }
}

public sealed record TextSurfaceDescriptor(
    string SurfaceId,
    string ProfileId,
    string DisplayName,
    bool Supported,
    bool CanCaptureText,
    bool CanReplaceText,
    bool CanSubmit,
    SurfaceMetadata Metadata);

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
