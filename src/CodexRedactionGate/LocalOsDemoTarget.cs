using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexRedactionGate;

public static class LocalOsDemoTarget
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"status: {OsInteractionStatusIds.UnsupportedPlatform}");
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new DemoTargetForm();
        Application.Run(form);
        return 0;
    }

    private sealed class DemoTargetForm : Form
    {
        public DemoTargetForm()
        {
            Text = "Redaction Gate Demo Target";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 760;
            Height = 420;
            MinimizeBox = true;
            MaximizeBox = true;

            var composer = new TextBox
            {
                Name = "RedactionGateDemoComposer",
                AccessibleName = "Redaction Gate Demo Composer",
                Multiline = true,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 11),
                ScrollBars = ScrollBars.Vertical,
                Text = "Connect to 192.168.10.25"
            };

            var status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                Text = "Focus this composer, then trigger Ctrl+Enter from a separate hotkey loop."
            };

            Controls.Add(composer);
            Controls.Add(status);
            Shown += (_, _) => composer.Focus();
        }
    }
}
