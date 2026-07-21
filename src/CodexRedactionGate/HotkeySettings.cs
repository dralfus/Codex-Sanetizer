using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

internal sealed record HotkeyDefinition(
    HotkeyBinding Binding,
    uint Modifiers,
    uint VirtualKey);

internal sealed record HotkeySettings(
    HotkeyDefinition ProtectionHotkey);

internal sealed record HotkeySettingsLoadResult(
    bool Usable,
    string Code,
    HotkeySettings Settings);

internal sealed record HotkeySettingsMutationResult(
    bool Succeeded,
    string Code,
    HotkeyDefinition? Hotkey);

internal static class HotkeySettingsStore
{
    private const string SettingsFileName = "tray-settings.json";

    public static HotkeyDefinition DefaultProtectionHotkey { get; } = HotkeyParser.Parse("Ctrl+Shift+F9").Hotkey!;

    public static HotkeyDefinition InvalidConfiguredHotkey { get; } = new(
        new HotkeyBinding("windows-tray-invalid", "configured_invalid", "windows"),
        Modifiers: 0,
        VirtualKey: 0);

    public static string DefaultPath(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SettingsDirectory, SettingsFileName);
    }

    public static HotkeySettings LoadOrDefault(DefaultStorageLayout layout)
    {
        return Load(layout).Settings;
    }

    public static HotkeySettingsLoadResult Load(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var path = DefaultPath(layout);
        if (!File.Exists(path))
        {
            return new HotkeySettingsLoadResult(
                true,
                "hotkey_default",
                new HotkeySettings(DefaultProtectionHotkey));
        }

        try
        {
            var model = JsonSerializer.Deserialize<HotkeySettingsFile>(
                File.ReadAllText(path),
                JsonOptions);
            var parsed = HotkeyParser.Parse(model?.ProtectionHotkey);
            return parsed.Succeeded && parsed.Hotkey is not null
                ? new HotkeySettingsLoadResult(true, "hotkey_loaded", new HotkeySettings(parsed.Hotkey))
                : new HotkeySettingsLoadResult(false, parsed.Code, new HotkeySettings(InvalidConfiguredHotkey));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new HotkeySettingsLoadResult(
                false,
                "hotkey_settings_unavailable",
                new HotkeySettings(InvalidConfiguredHotkey));
        }
    }

    public static HotkeySettingsMutationResult SaveProtectionHotkey(DefaultStorageLayout layout, string hotkeyText)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var parsed = HotkeyParser.Parse(hotkeyText);
        if (!parsed.Succeeded || parsed.Hotkey is null)
        {
            return new HotkeySettingsMutationResult(false, parsed.Code, null);
        }

        var payload = JsonSerializer.Serialize(
            new HotkeySettingsFile(parsed.Hotkey.Binding.DisplayText),
            JsonOptions);
        AtomicFileWriter.WriteAllBytes(DefaultPath(layout), Encoding.UTF8.GetBytes(payload + Environment.NewLine));
        return new HotkeySettingsMutationResult(true, "hotkey_saved", parsed.Hotkey);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record HotkeySettingsFile(
        [property: JsonPropertyName("protection_hotkey")] string? ProtectionHotkey);
}

internal static class HotkeyParser
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private static readonly ISet<string> ReservedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "F12"
    };

    public static HotkeySettingsMutationResult Parse(string? hotkeyText)
    {
        if (string.IsNullOrWhiteSpace(hotkeyText))
        {
            return new HotkeySettingsMutationResult(false, "hotkey_invalid_empty", null);
        }

        var parts = hotkeyText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return new HotkeySettingsMutationResult(false, "hotkey_invalid_missing_modifier", null);
        }

        uint modifiers = 0;
        string? key = null;
        foreach (var part in parts)
        {
            if (TryParseModifier(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                return new HotkeySettingsMutationResult(false, "hotkey_invalid_multiple_keys", null);
            }

            key = NormalizeKey(part);
        }

        if (modifiers == 0 || key is null)
        {
            return new HotkeySettingsMutationResult(false, "hotkey_invalid_missing_modifier", null);
        }

        if ((modifiers & ModWin) != 0)
        {
            return new HotkeySettingsMutationResult(false, "hotkey_reserved_windows_modifier", null);
        }

        if (!TryParseVirtualKey(key, out var virtualKey))
        {
            return new HotkeySettingsMutationResult(false, "hotkey_invalid_key", null);
        }

        if (ReservedKeys.Contains(key))
        {
            return new HotkeySettingsMutationResult(false, "hotkey_reserved", null);
        }

        var displayText = FormatDisplayText(modifiers, key);
        return new HotkeySettingsMutationResult(
            true,
            "hotkey_valid",
            new HotkeyDefinition(
                new HotkeyBinding("windows-tray-configured", displayText, "windows"),
                modifiers,
                virtualKey));
    }

    private static bool TryParseModifier(string value, out uint modifier)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                modifier = ModControl;
                return true;
            case "SHIFT":
                modifier = ModShift;
                return true;
            case "ALT":
                modifier = ModAlt;
                return true;
            case "WIN":
            case "WINDOWS":
                modifier = ModWin;
                return true;
            default:
                modifier = 0;
                return false;
        }
    }

    private static string NormalizeKey(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized == "RETURN" ? "ENTER" : normalized;
    }

    private static bool TryParseVirtualKey(string key, out uint virtualKey)
    {
        if (key.Length >= 2
            && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z')
        {
            virtualKey = key[0];
            return true;
        }

        if (key is "ENTER" or "RETURN")
        {
            virtualKey = 0x0D;
            return true;
        }

        virtualKey = 0;
        return false;
    }

    private static string FormatDisplayText(uint modifiers, string key)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKeyDisplayText(key));
        return string.Join("+", parts);
    }

    private static string FormatKeyDisplayText(string key)
    {
        return key == "ENTER" ? "Enter" : key;
    }
}
