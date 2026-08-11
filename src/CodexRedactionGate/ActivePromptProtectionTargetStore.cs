using System;
using System.IO;
using System.Text;

namespace CodexRedactionGate;

internal sealed record ActivePromptProtectionTargetStoreResult(
    bool Succeeded,
    string Code,
    string? ProfileId);

/// <summary>
/// Persists the one OpenAI Desktop surface variant that completed setup.
/// Profile history is not setup state: only this target may satisfy startup.
/// </summary>
internal static class ActivePromptProtectionTargetStore
{
    private const string FileName = ".active_prompt_protection_target";

    public static ActivePromptProtectionTargetStoreResult Load(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var path = Path.Combine(layout.SettingsDirectory, FileName);
        if (!File.Exists(path))
        {
            return new ActivePromptProtectionTargetStoreResult(true, "target_missing", null);
        }

        try
        {
            var profileId = File.ReadAllText(path, Encoding.UTF8).Trim();
            return IsSupportedProfileId(profileId)
                ? new ActivePromptProtectionTargetStoreResult(true, "target_loaded", profileId)
                : new ActivePromptProtectionTargetStoreResult(false, "target_invalid", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ActivePromptProtectionTargetStoreResult(false, "target_unavailable", null);
        }
    }

    public static ActivePromptProtectionTargetStoreResult Save(DefaultStorageLayout layout, string profileId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsSupportedProfileId(profileId))
        {
            return new ActivePromptProtectionTargetStoreResult(false, "target_invalid", null);
        }

        try
        {
            layout.EnsureDirectories();
            AtomicFileWriter.WriteAllBytes(
                Path.Combine(layout.SettingsDirectory, FileName),
                Encoding.UTF8.GetBytes(profileId + Environment.NewLine));
            return new ActivePromptProtectionTargetStoreResult(true, "target_saved", profileId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ActivePromptProtectionTargetStoreResult(false, "target_unavailable", null);
        }
    }

    public static bool Clear(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            var path = Path.Combine(layout.SettingsDirectory, FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSupportedProfileId(string? profileId)
    {
        return profileId is "codex-desktop" or "chatgpt-desktop";
    }
}
