using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal enum TrayProtectionIntent
{
    ToggleProtection,
    OpenProtectionStatus,
    OpenLocalRestore,
    OpenSensitiveTerms,
    SetupPromptProtection,
    RepairLocalProtection,
    Exit
}

/// <summary>
/// Keeps WinForms event handlers declarative: UI emits an intent, while the
/// resident application owns the workflow behind that intent.
/// </summary>
internal sealed class TrayProtectionIntentDispatcher
{
    private readonly IReadOnlyDictionary<TrayProtectionIntent, Action> _handlers;

    public TrayProtectionIntentDispatcher(IReadOnlyDictionary<TrayProtectionIntent, Action> handlers)
    {
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    public bool TryDispatch(TrayProtectionIntent intent)
    {
        if (!_handlers.TryGetValue(intent, out var handler))
        {
            return false;
        }

        handler();
        return true;
    }
}
