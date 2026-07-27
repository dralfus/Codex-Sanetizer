using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal static class DictionaryManagementUiText
{
    public const string Title = "Codex Redaction Gate - Sensitive terms";
    public const string Intro = "Add local terms that must be replaced before text reaches selected AI apps.";
    public const string AddButton = "Add";
    public const string UpdateButton = "Update";
    public const string DeleteButton = "Delete";
    public const string RefreshButton = "Refresh";
    public const string TestButton = "Test text";
    public const string EmptyValueStatus = "Enter a value to protect.";
    public const string NoSelectionStatus = "Select a term first.";

    public static string SupportedTypesText()
    {
        return "Types: " + string.Join(", ", SensitiveEntityTypes.DictionaryTypes.OrderBy(type => type, StringComparer.Ordinal));
    }
}

internal sealed class DictionaryManagementForm : Form
{
    private readonly DefaultStorageLayout _layout;
    private readonly ManagedSensitiveDictionary _store;
    private readonly DataGridView _entriesGrid;
    private readonly ComboBox _typeComboBox;
    private readonly TextBox _valueTextBox;
    private readonly TextBox _notesTextBox;
    private readonly TextBox _sampleTextBox;
    private readonly TextBox _sanitizedTextBox;
    private readonly Label _statusLabel;
    private readonly Label _pathLabel;
    private bool _loading;

    public DictionaryManagementForm(DefaultStorageLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _layout.EnsureDirectories();
        _store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(_layout));

        Text = DictionaryManagementUiText.Title;
        MinimumSize = new Size(920, 660);
        StartPosition = FormStartPosition.CenterScreen;

        _entriesGrid = CreateEntriesGrid();
        _entriesGrid.SelectionChanged += (_, _) => LoadSelectedEntry();

        _typeComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (var type in SensitiveEntityTypes.DictionaryTypes.OrderBy(type => type, StringComparer.Ordinal))
        {
            _typeComboBox.Items.Add(type);
        }

        _valueTextBox = new TextBox { Dock = DockStyle.Fill };
        _notesTextBox = new TextBox { Dock = DockStyle.Fill };
        _sampleTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical
        };
        _sanitizedTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "Ready."
        };
        _pathLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = _store.FilePath
        };

        Controls.Add(CreateLayout());
        RefreshEntries();
    }

    private Control CreateLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 7,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(_entriesGrid, 0, 1);
        root.Controls.Add(CreateEditor(), 0, 2);
        root.Controls.Add(CreateButtons(), 0, 3);
        root.Controls.Add(CreateSampleGroup("Sample text", _sampleTextBox), 0, 4);
        root.Controls.Add(CreateSampleGroup("Sanitized result", _sanitizedTextBox), 0, 5);
        root.Controls.Add(_statusLabel, 0, 6);
        return root;
    }

    private Control CreateHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = DictionaryManagementUiText.Intro,
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 0);
        panel.Controls.Add(new Label
        {
            Text = DictionaryManagementUiText.SupportedTypesText(),
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 1);
        panel.Controls.Add(_pathLabel, 0, 2);
        return panel;
    }

    private Control CreateEditor()
    {
        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 6,
            RowCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        editor.Controls.Add(new Label { Text = "Type", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        editor.Controls.Add(_typeComboBox, 1, 0);
        editor.Controls.Add(new Label { Text = "Value", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        editor.Controls.Add(_valueTextBox, 3, 0);
        editor.Controls.Add(new Label { Text = "Notes", AutoSize = true, Anchor = AnchorStyles.Left }, 4, 0);
        editor.Controls.Add(_notesTextBox, 5, 0);
        return editor;
    }

    private Control CreateButtons()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 8)
        };
        buttons.Controls.Add(CreateButton(DictionaryManagementUiText.AddButton, AddEntry));
        buttons.Controls.Add(CreateButton(DictionaryManagementUiText.UpdateButton, UpdateEntry));
        buttons.Controls.Add(CreateButton(DictionaryManagementUiText.DeleteButton, DeleteEntry));
        buttons.Controls.Add(CreateButton(DictionaryManagementUiText.RefreshButton, RefreshEntries));
        buttons.Controls.Add(CreateButton(DictionaryManagementUiText.TestButton, TestSampleText));
        return buttons;
    }

    private static Button CreateButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Control CreateSampleGroup(string title, TextBox textBox)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        group.Controls.Add(textBox);
        return group;
    }

    private static DataGridView CreateEntriesGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            RowHeadersVisible = false
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ManagedDictionaryEntry.Id), HeaderText = "Id", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ManagedDictionaryEntry.Type), HeaderText = "Type", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ManagedDictionaryEntry.Value), HeaderText = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(ManagedDictionaryEntry.Notes), HeaderText = "Notes", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        return grid;
    }

    private void AddEntry()
    {
        var input = ReadInput();
        if (input is null)
        {
            return;
        }

        var result = _store.Add(input.Value.Type, input.Value.Value, input.Value.Notes);
        FinishMutation(result, selectId: result.EntryId);
    }

    private void UpdateEntry()
    {
        var selected = SelectedEntry();
        if (selected is null)
        {
            SetStatus(DictionaryManagementUiText.NoSelectionStatus);
            return;
        }

        var input = ReadInput();
        if (input is null)
        {
            return;
        }

        FinishMutation(_store.Update(selected.Id, input.Value.Type, input.Value.Value, input.Value.Notes), selected.Id);
    }

    private void DeleteEntry()
    {
        var selected = SelectedEntry();
        if (selected is null)
        {
            SetStatus(DictionaryManagementUiText.NoSelectionStatus);
            return;
        }

        if (MessageBox.Show(
            "Delete the selected local sensitive term?",
            "Codex Redaction Gate - Delete term",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        FinishMutation(_store.Remove(selected.Id), selectId: null);
    }

    private void RefreshEntries()
    {
        RefreshEntries(selectId: SelectedEntry()?.Id);
    }

    private void RefreshEntries(string? selectId)
    {
        _loading = true;
        try
        {
            var entries = _store.ListEntriesForLocalReveal()
                .OrderBy(entry => entry.Type, StringComparer.Ordinal)
                .ThenBy(entry => entry.Value, StringComparer.Ordinal)
                .ToArray();
            _entriesGrid.DataSource = entries;
            SelectEntry(selectId);
            if (_typeComboBox.SelectedIndex < 0 && _typeComboBox.Items.Count > 0)
            {
                _typeComboBox.SelectedItem = SensitiveEntityTypes.Domain;
            }

            SetStatus($"Loaded {entries.Length} local sensitive term(s).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LocalCrashDiagnostics.CaptureDefault(exception, "dictionary_management", "dictionary_load_failed");
            SetStatus(PublicFailureText.Format(exception, "Load"));
        }
        finally
        {
            _loading = false;
        }

        LoadSelectedEntry();
    }

    private void LoadSelectedEntry()
    {
        if (_loading)
        {
            return;
        }

        var selected = SelectedEntry();
        if (selected is null)
        {
            return;
        }

        _typeComboBox.SelectedItem = selected.Type;
        _valueTextBox.Text = selected.Value;
        _notesTextBox.Text = selected.Notes ?? string.Empty;
    }

    private void TestSampleText()
    {
        if (string.IsNullOrWhiteSpace(_sampleTextBox.Text))
        {
            SetStatus("Enter sample text to test.");
            return;
        }

        try
        {
            var sanitizer = Sanitizer.CreateProduction(_layout);
            var result = sanitizer.Sanitize(new SanitizeRequest(
                ContentParts: new[]
                {
                    new ContentPart(
                        Id: "prompt",
                        ContentSource: ContentSources.PromptText,
                        RawText: _sampleTextBox.Text,
                        SourceMetadata: new System.Collections.Generic.Dictionary<string, string>())
                },
                Context: new SanitizationContext(
                    Application: "dictionary-management-ui",
                    WorkspacePath: null,
                    ProjectId: null,
                    SessionId: null,
                    PolicyProfile: "default"),
                Options: new SanitizationOptions(
                    AllowSessionAliases: false,
                    AllowSecretStorage: false,
                    ConfirmationMode: "local")));

            _sanitizedTextBox.Text = result.SanitizedText;
            SetStatus($"Test decision={CliOutputFormatting.FormatDecision(result.Decision)} replacements={result.Replacements.Count}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LocalCrashDiagnostics.CaptureDefault(exception, "dictionary_management", "dictionary_test_failed");
            SetStatus(PublicFailureText.Format(exception, "Test"));
        }
    }

    private (string Type, string Value, string? Notes)? ReadInput()
    {
        var type = _typeComboBox.SelectedItem?.ToString() ?? string.Empty;
        var value = _valueTextBox.Text.Trim();
        var notes = string.IsNullOrWhiteSpace(_notesTextBox.Text) ? null : _notesTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus(DictionaryManagementUiText.EmptyValueStatus);
            return null;
        }

        return (type, value, notes);
    }

    private void FinishMutation(ManagedDictionaryMutationResult result, string? selectId)
    {
        SetStatus($"status={result.Code}");
        if (result.Succeeded)
        {
            RefreshEntries(selectId);
        }
    }

    private ManagedDictionaryEntry? SelectedEntry()
    {
        return _entriesGrid.CurrentRow?.DataBoundItem as ManagedDictionaryEntry;
    }

    private void SelectEntry(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        foreach (DataGridViewRow row in _entriesGrid.Rows)
        {
            if (row.DataBoundItem is ManagedDictionaryEntry entry
                && string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                row.Selected = true;
                _entriesGrid.CurrentCell = row.Cells[0];
                LoadSelectedEntry();
                return;
            }
        }
    }

    private void SetStatus(string status)
    {
        _statusLabel.Text = status;
    }
}
