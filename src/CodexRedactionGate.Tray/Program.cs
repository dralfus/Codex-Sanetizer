using System;
using CodexRedactionGate;

namespace CodexRedactionGate.Tray;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        return WindowsTrayApp.Run(Sanitizer.CreateProduction(Array.Empty<DictionaryTerm>()));
    }
}
