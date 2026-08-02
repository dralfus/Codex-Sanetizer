namespace CodexRedactionGate;

/// <summary>
/// Raw-free progress published by the setup workflow into the resident state.
/// </summary>
internal sealed record PromptProtectionSetupProgress(
    string Status,
    string Action,
    string? ProfileId = null,
    string Binding = "not_configured",
    long AttemptId = 0);

internal static class PromptProtectionSetupLifecycle
{
    public static string SafeStatus(string value) => value is "idle" or "waiting_for_focus"
        or "composer_recognized" or "verifying_binding" or "activating_protection"
        or "protected" or "verification_failed" or "unsupported_surface"
        or "activation_failed" or "setup_cancelled"
        ? value
        : "verification_failed";

    public static string SafeAction(string value) => value is "none" or "focus_message_composer"
        or "wait_for_verification" or "retry_setup" or "restart_protection"
        ? value
        : "retry_setup";

    public static string SafeProfileId(string? value) => value is "codex-desktop" or "chatgpt-desktop"
        ? value
        : "selected_desktop_app";

    public static string SafeBinding(string? value) => value is "Enter" or "Ctrl+Enter"
        ? value
        : "not_configured";

    public static bool IsAllowedTransition(string from, string to) => (from, to) switch
    {
        ("idle", "waiting_for_focus") => true,
        ("waiting_for_focus", "composer_recognized" or "unsupported_surface" or "verification_failed" or "setup_cancelled") => true,
        ("composer_recognized", "verifying_binding") => true,
        ("verifying_binding", "activating_protection" or "verification_failed" or "unsupported_surface") => true,
        ("activating_protection", "protected" or "activation_failed") => true,
        ("protected", "waiting_for_focus") => true,
        ("verification_failed" or "unsupported_surface" or "activation_failed" or "setup_cancelled", "waiting_for_focus") => true,
        _ => false
    };
}
