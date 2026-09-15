using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KwiImportLauncher;

public sealed class MainForm : Form
{
    private static readonly Color Navy = Color.FromArgb(10, 47, 105);
    private static readonly Color NavyDark = Color.FromArgb(5, 27, 61);
    private static readonly Color Red = Color.FromArgb(185, 28, 48);
    private static readonly Color LightBlue = Color.FromArgb(232, 239, 250);
    private static readonly Color Border = Color.FromArgb(214, 222, 235);
    private static readonly Color Surface = Color.FromArgb(244, 247, 251);
    private static readonly Color TextDark = Color.FromArgb(24, 31, 42);
    private static readonly Color Muted = Color.FromArgb(87, 98, 115);

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private JsonObject _settings = new();
    private string _importerDirectory = string.Empty;
    private string _settingsPath = string.Empty;
    private Process? _process;
    private bool _importPromptAnswered;
    private bool _affiliationPromptAnswered;
    private string _promptBuffer = string.Empty;

    private TextBox _programFolderText = null!;
    private Label _phase0Status = null!;
    private RadioButton _dryRunRadio = null!;
    private RadioButton _liveRadio = null!;
    private CheckBox _liveConfirmCheck = null!;
    private TextBox _importIdText = null!;
    private TextBox _affiliationText = null!;
    private Button _runButton = null!;
    private Button _stopButton = null!;
    private RichTextBox _console = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ListBox _sectionList = null!;
    private FlowLayoutPanel _settingsPanel = null!;
    private Label _settingsHeading = null!;
    private TabControl _tabs = null!;

    public MainForm()
    {
        Text = "KWI Account Import";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 780);
        Size = new Size(1360, 880);
        BackColor = Surface;
        Font = new Font("Segoe UI", 10f);
        Icon = TryLoadIcon();

        BuildUi();

        Shown += (_, _) =>
        {
            _importerDirectory = FindImporterDirectory();
            _programFolderText.Text = _importerDirectory;
            LoadSettings();
        };

        FormClosing += (_, e) =>
        {
            if (_process is { HasExited: false })
            {
                var answer = MessageBox.Show(
                    "The importer is still running. Stop it and close the launcher?",
                    "Importer is running",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                TryStopProcess();
            }
        };
    }

    private Icon? TryLoadIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "KwiImport.ico");
            if (File.Exists(iconPath)) return new Icon(iconPath);
        }
        catch
        {
            // The executable still has its embedded icon even if the loose asset is unavailable.
        }

        return null;
    }

    private void BuildUi()
    {
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildTabs());
        Controls.Add(BuildHeader());
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 104,
            BackColor = Navy,
            Padding = new Padding(32, 18, 32, 14)
        };

        var title = new Label
        {
            AutoSize = true,
            Text = "KWI ACCOUNT IMPORT",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 21f, FontStyle.Bold),
            Location = new Point(32, 20)
        };

        var subtitle = new Label
        {
            AutoSize = true,
            Text = "Safe control center for Momentus account and contact imports",
            ForeColor = Color.FromArgb(222, 232, 246),
            Font = new Font("Segoe UI", 10.5f),
            Location = new Point(35, 62)
        };

        var badge = new Label
        {
            AutoSize = true,
            Text = "DRY RUN DEFAULT",
            ForeColor = Color.White,
            BackColor = Red,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            Padding = new Padding(14, 8, 14, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        badge.Location = new Point(header.Width - badge.Width - 30, 27);
        header.Resize += (_, _) => badge.Location = new Point(header.ClientSize.Width - badge.Width - 28, 27);

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(badge);
        return header;
    }

    private Control BuildTabs()
    {
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(18, 7),
            Font = new Font("Segoe UI Semibold", 10f),
            ItemSize = new Size(132, 40),
            SizeMode = TabSizeMode.Fixed
        };

        _tabs.TabPages.Add(BuildRunTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        _tabs.TabPages.Add(BuildHelpTab());
        return _tabs;
    }

    private TabPage BuildRunTab()
    {
        var tab = new TabPage("Run Import")
        {
            BackColor = Surface,
            Padding = new Padding(24)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Surface,
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 330f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var leftStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 12, 14)
        };
        leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
        leftStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftStack.Controls.Add(BuildModeCard(), 0, 0);
        leftStack.Controls.Add(BuildPromptCard(), 0, 1);

        var rightStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = new Padding(12, 0, 0, 14)
        };
        rightStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        rightStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightStack.Controls.Add(BuildPathCard(), 0, 0);
        rightStack.Controls.Add(BuildActionCard(), 0, 1);

        root.Controls.Add(leftStack, 0, 0);
        root.Controls.Add(rightStack, 1, 0);

        var consoleCard = CreateCard();
        consoleCard.Margin = new Padding(0);
        var consoleHeader = new Label
        {
            Text = "RUN OUTPUT",
            AutoSize = true,
            ForeColor = Navy,
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            Location = new Point(22, 17)
        };
        _console = new RichTextBox
        {
            ReadOnly = true,
            BackColor = Color.FromArgb(20, 24, 33),
            ForeColor = Color.FromArgb(225, 232, 240),
            BorderStyle = BorderStyle.None,
            Font = new Font("Cascadia Mono", 9.25f),
            DetectUrls = false,
            Location = new Point(22, 50),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Size = new Size(consoleCard.ClientSize.Width - 44, consoleCard.ClientSize.Height - 72)
        };
        consoleCard.Resize += (_, _) =>
        {
            _console.Size = new Size(Math.Max(100, consoleCard.ClientSize.Width - 44), Math.Max(100, consoleCard.ClientSize.Height - 72));
        };
        consoleCard.Controls.Add(consoleHeader);
        consoleCard.Controls.Add(_console);
        root.Controls.Add(consoleCard, 0, 1);
        root.SetColumnSpan(consoleCard, 2);

        tab.Controls.Add(root);
        return tab;
    }

    private Control BuildModeCard()
    {
        var card = CreateCard();
        card.Padding = new Padding(22);

        var title = CardTitle("RUN MODE");
        title.Location = new Point(22, 18);

        _dryRunRadio = new RadioButton
        {
            Text = "Dry run",
            Checked = true,
            AutoSize = true,
            ForeColor = TextDark,
            Location = new Point(24, 58),
            Font = new Font("Segoe UI Semibold", 10f)
        };

        _liveRadio = new RadioButton
        {
            Text = "Live production import",
            AutoSize = true,
            ForeColor = TextDark,
            Location = new Point(24, 90),
            Font = new Font("Segoe UI Semibold", 10f)
        };

        _liveConfirmCheck = new CheckBox
        {
            Text = "I understand live mode writes to Momentus",
            AutoSize = true,
            ForeColor = Red,
            Location = new Point(46, 123),
            Visible = false
        };

        _liveRadio.CheckedChanged += (_, _) =>
        {
            _liveConfirmCheck.Visible = _liveRadio.Checked;
            UpdateRunButtonState();
        };
        _liveConfirmCheck.CheckedChanged += (_, _) => UpdateRunButtonState();

        card.Controls.Add(title);
        card.Controls.Add(_dryRunRadio);
        card.Controls.Add(_liveRadio);
        card.Controls.Add(_liveConfirmCheck);
        return card;
    }

    private Control BuildPromptCard()
    {
        var card = CreateCard();
        card.Padding = new Padding(22);
        var title = CardTitle("RUN-TIME VALUES");
        title.Location = new Point(22, 18);

        var importLabel = new Label
        {
            AutoSize = true,
            Text = "Import ID",
            ForeColor = TextDark,
            Location = new Point(24, 58)
        };
        _importIdText = StyledTextBox();
        _importIdText.Location = new Point(24, 82);
        _importIdText.Width = 220;
        _importIdText.CharacterCasing = CharacterCasing.Upper;

        var affiliationLabel = new Label
        {
            AutoSize = true,
            Text = "Affiliation / interest code(s)",
            ForeColor = TextDark,
            Location = new Point(270, 58)
        };
        _affiliationText = StyledTextBox();
        _affiliationText.Location = new Point(270, 82);
        _affiliationText.Width = 220;

        var note = new Label
        {
            AutoSize = false,
            Text = "These values answer the importer's console prompts automatically. Separate multiple interest codes with commas. Leave blank to skip when prompted.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9.25f),
            Location = new Point(24, 124),
            Size = new Size(500, 58),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        card.Resize += (_, _) =>
        {
            int gap = 26;
            int available = Math.Max(320, card.ClientSize.Width - 48);
            int inputWidth = Math.Max(180, (available - gap) / 2);
            _importIdText.Width = inputWidth;
            affiliationLabel.Left = 24 + inputWidth + gap;
            _affiliationText.Left = affiliationLabel.Left;
            _affiliationText.Width = inputWidth;
            note.Width = available;
        };

        card.Controls.Add(title);
        card.Controls.Add(importLabel);
        card.Controls.Add(_importIdText);
        card.Controls.Add(affiliationLabel);
        card.Controls.Add(_affiliationText);
        card.Controls.Add(note);
        return card;
    }

    private Control BuildPathCard()
    {
        var card = CreateCard();
        card.Padding = new Padding(22);
        var title = CardTitle("IMPORTER LOCATION");
        title.Location = new Point(22, 18);

        _programFolderText = StyledTextBox();
        _programFolderText.Location = new Point(24, 56);
        _programFolderText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _programFolderText.Width = 430;
        _programFolderText.TextChanged += (_, _) =>
        {
            _importerDirectory = _programFolderText.Text.Trim();
            _settingsPath = Path.Combine(_importerDirectory, "appsettings.json");
            UpdatePhase0Status();
        };

        var browse = StyledButton("Browse", Navy, Color.White);
        browse.Location = new Point(462, 54);
        browse.Size = new Size(96, 32);
        browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browse.Click += (_, _) => BrowseImporterFolder();

        _phase0Status = new Label
        {
            AutoSize = false,
            ForeColor = Muted,
            Location = new Point(24, 98),
            Size = new Size(530, 42),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var openPhase0 = StyledButton("Open Phase 0", LightBlue, Navy);
        openPhase0.Location = new Point(24, 150);
        openPhase0.Size = new Size(126, 34);
        openPhase0.Click += (_, _) => OpenConfiguredFolder("Phase 0");

        var openArchive = StyledButton("Open Phase 5", LightBlue, Navy);
        openArchive.Location = new Point(160, 150);
        openArchive.Size = new Size(126, 34);
        openArchive.Click += (_, _) => OpenConfiguredFolder("Phase 5 - complete");

        card.Resize += (_, _) =>
        {
            _programFolderText.Width = Math.Max(220, card.ClientSize.Width - 154);
            browse.Left = card.ClientSize.Width - 120;
            _phase0Status.Width = Math.Max(240, card.ClientSize.Width - 48);
        };

        card.Controls.Add(title);
        card.Controls.Add(_programFolderText);
        card.Controls.Add(browse);
        card.Controls.Add(_phase0Status);
        card.Controls.Add(openPhase0);
        card.Controls.Add(openArchive);
        return card;
    }

    private Control BuildActionCard()
    {
        var card = CreateCard();
        card.Padding = new Padding(22);
        var title = CardTitle("ACTIONS");
        title.Location = new Point(22, 18);

        _runButton = StyledButton("RUN DRY IMPORT", Navy, Color.White);
        _runButton.Location = new Point(24, 58);
        _runButton.Size = new Size(210, 42);
        _runButton.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        _runButton.Click += async (_, _) => await RunImporterAsync();

        _stopButton = StyledButton("Stop", Color.White, Red);
        _stopButton.Location = new Point(246, 58);
        _stopButton.Size = new Size(92, 42);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => TryStopProcess();

        var save = StyledButton("Save Settings", LightBlue, Navy);
        save.Location = new Point(350, 58);
        save.Size = new Size(134, 42);
        save.Click += (_, _) => SaveSettings(showConfirmation: true);

        card.Resize += (_, _) =>
        {
            int available = card.ClientSize.Width - 48;
            if (available < 520)
            {
                _runButton.Width = Math.Max(180, available);
                _stopButton.Location = new Point(24, 110);
                save.Location = new Point(126, 110);
            }
            else
            {
                _runButton.Width = 210;
                _stopButton.Location = new Point(246, 58);
                save.Location = new Point(350, 58);
            }
        };

        card.Controls.Add(title);
        card.Controls.Add(_runButton);
        card.Controls.Add(_stopButton);
        card.Controls.Add(save);
        return card;
    }

    private TabPage BuildSettingsTab()
    {
        var tab = new TabPage("Settings")
        {
            BackColor = Surface,
            Padding = new Padding(24)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Surface
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var navCard = CreateCard();
        navCard.Margin = new Padding(0, 0, 12, 0);
        var navTitle = CardTitle("CONFIGURATION");
        navTitle.Location = new Point(22, 18);
        _sectionList = new ListBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 10f),
            Location = new Point(18, 56),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            IntegralHeight = false
        };
        navCard.Resize += (_, _) => _sectionList.Size = new Size(navCard.ClientSize.Width - 36, navCard.ClientSize.Height - 76);
        _sectionList.SelectedIndexChanged += (_, _) => RenderSelectedSettingsSection();
        navCard.Controls.Add(navTitle);
        navCard.Controls.Add(_sectionList);

        var editorCard = CreateCard();
        editorCard.Margin = new Padding(12, 0, 0, 0);
        _settingsHeading = new Label
        {
            AutoSize = true,
            ForeColor = Navy,
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            Location = new Point(24, 22)
        };
        _settingsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Location = new Point(24, 66),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.White,
            Padding = new Padding(4)
        };

        var save = StyledButton("Save Settings", Navy, Color.White);
        save.Size = new Size(125, 34);
        save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        save.Click += (_, _) => SaveSettings(showConfirmation: true);

        var reload = StyledButton("Reload", LightBlue, Navy);
        reload.Size = new Size(90, 34);
        reload.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        reload.Click += (_, _) => LoadSettings();

        editorCard.Resize += (_, _) =>
        {
            _settingsPanel.Size = new Size(Math.Max(300, editorCard.ClientSize.Width - 48), Math.Max(260, editorCard.ClientSize.Height - 128));
            save.Location = new Point(editorCard.ClientSize.Width - save.Width - 24, editorCard.ClientSize.Height - 50);
            reload.Location = new Point(save.Left - reload.Width - 10, save.Top);
            foreach (Control child in _settingsPanel.Controls)
            {
                if (child.Tag as string == "fullwidth") child.Width = Math.Max(260, _settingsPanel.ClientSize.Width - 35);
            }
        };

        editorCard.Controls.Add(_settingsHeading);
        editorCard.Controls.Add(_settingsPanel);
        editorCard.Controls.Add(save);
        editorCard.Controls.Add(reload);

        root.Controls.Add(navCard, 0, 0);
        root.Controls.Add(editorCard, 1, 0);
        tab.Controls.Add(root);
        return tab;
    }

    private TabPage BuildHelpTab()
    {
        var tab = new TabPage("About")
        {
            BackColor = Surface,
            Padding = new Padding(24)
        };

        var card = CreateCard();
        card.Dock = DockStyle.Fill;

        var title = new Label
        {
            Text = "KWI Account Import Launcher",
            ForeColor = Navy,
            Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(32, 32)
        };

        var body = new Label
        {
            AutoSize = false,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 11f),
            Location = new Point(34, 88),
            Size = new Size(900, 330),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text =
                "This is a companion front end for the existing AccountImport console project.\r\n\r\n" +
                "• Dry run is selected by default and does not write accounts or contacts to Momentus.\r\n" +
                "• Live mode requires an explicit confirmation before the Run button is enabled.\r\n" +
                "• Settings are read from and saved back to Program\\appsettings.json.\r\n" +
                "• The Settings page is generated from the JSON file, so newly added settings appear automatically.\r\n" +
                "• Import ID and affiliation values on the Run page answer the importer's console prompts.\r\n\r\n" +
                "Recommended workflow: save settings → run dry → review the output files → only then use live mode."
        };

        card.Controls.Add(title);
        card.Controls.Add(body);
        tab.Controls.Add(card);
        return tab;
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip
        {
            BackColor = Color.White,
            SizingGrip = false
        };
        _statusLabel = new ToolStripStatusLabel
        {
            Text = "Ready",
            ForeColor = Muted
        };
        strip.Items.Add(_statusLabel);
        return strip;
    }

    private Panel CreateCard()
    {
        var panel = new Panel
        {
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(1),
            BorderStyle = BorderStyle.FixedSingle
        };
        return panel;
    }

    private static Label CardTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Navy,
        Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold)
    };

    private static TextBox StyledTextBox() => new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        ForeColor = TextDark,
        Font = new Font("Segoe UI", 10f),
        Height = 30
    };

    private static Button StyledButton(string text, Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI Semibold", 9.5f)
        };
        button.FlatAppearance.BorderColor = backColor == Color.White ? Border : backColor;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private string FindImporterDirectory()
    {
        string candidate = @"C:\kwi-automations\projects\AccountImport\Program";
        if (IsImporterDirectory(candidate)) return candidate;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (IsImporterDirectory(dir.FullName)) return dir.FullName;
            string parentCandidate = Path.Combine(dir.FullName, "Program");
            if (IsImporterDirectory(parentCandidate)) return parentCandidate;
        }

        return Environment.CurrentDirectory;
    }

    private static bool IsImporterDirectory(string path)
    {
        return Directory.Exists(path) &&
               File.Exists(Path.Combine(path, "AccountImport.csproj")) &&
               File.Exists(Path.Combine(path, "appsettings.json"));
    }

    private void BrowseImporterFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the AccountImport Program folder",
            SelectedPath = Directory.Exists(_programFolderText.Text) ? _programFolderText.Text : string.Empty,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (!IsImporterDirectory(dialog.SelectedPath))
            {
                MessageBox.Show(
                    "That folder does not contain both AccountImport.csproj and appsettings.json.",
                    "Not an AccountImport Program folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _programFolderText.Text = dialog.SelectedPath;
            LoadSettings();
        }
    }

    private void LoadSettings()
    {
        try
        {
            _importerDirectory = _programFolderText.Text.Trim();
            _settingsPath = Path.Combine(_importerDirectory, "appsettings.json");

            if (!File.Exists(_settingsPath))
            {
                SetStatus("appsettings.json not found.", isError: true);
                UpdatePhase0Status();
                return;
            }

            string json = File.ReadAllText(_settingsPath);
            _settings = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject ?? new JsonObject();

            EnsureOptionalSettingsVisible();
            PopulateSectionList();
            LoadRunValuesFromSettings();
            UpdatePhase0Status();
            SetStatus("Settings loaded from " + _settingsPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not load settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Could not load settings.", isError: true);
        }
    }

    private void EnsureOptionalSettingsVisible()
    {
        // AppConfig has Tagging defaults even when the section is omitted from appsettings.json.
        // Adding it here makes those settings available from the launcher without changing behavior
        // unless the user explicitly enables them.
        if (_settings["Tagging"] is null)
        {
            _settings["Tagging"] = new JsonObject
            {
                ["Enabled"] = false,
                ["PromptForRunTag"] = false,
                ["RunTagValue"] = string.Empty,
                ["UserTextProperty"] = "UserText02",
                ["OrganizationUserFieldHeader"] = "OrganizationAccountUserFields",
                ["IndividualUserFieldHeader"] = "IndividualAccountUserFields",
                ["UserFieldClass"] = string.Empty,
                ["UserFieldType"] = string.Empty,
                ["ApplyToCreatedOrganizationAccounts"] = false,
                ["ApplyToCreatedContacts"] = false,
                ["ApplyToMatchedExistingAccounts"] = false,
                ["OverwriteExistingValue"] = true
            };
        }
    }

    private void LoadRunValuesFromSettings()
    {
        bool dry = GetBool(_settings["DryRun"], true);
        _dryRunRadio.Checked = dry;
        _liveRadio.Checked = !dry;
        _liveConfirmCheck.Checked = false;

        if (_settings["ImportId"] is JsonObject importId)
        {
            _importIdText.Text = GetString(importId["Value"]);
        }

        UpdateRunButtonState();
    }

    private void PopulateSectionList()
    {
        string? previous = _sectionList.SelectedItem?.ToString();
        _sectionList.Items.Clear();
        _sectionList.Items.Add("General");

        foreach (var kvp in _settings)
        {
            if (kvp.Value is JsonObject)
                _sectionList.Items.Add(kvp.Key);
        }

        _sectionList.Items.Add("Raw JSON");

        if (!string.IsNullOrWhiteSpace(previous) && _sectionList.Items.Contains(previous))
            _sectionList.SelectedItem = previous;
        else
            _sectionList.SelectedIndex = 0;
    }

    private void RenderSelectedSettingsSection()
    {
        if (_sectionList.SelectedItem is not string section) return;

        _settingsHeading.Text = FriendlyName(section);
        _settingsPanel.SuspendLayout();
        _settingsPanel.Controls.Clear();

        if (section == "Raw JSON")
        {
            RenderRawJsonEditor();
        }
        else if (section == "General")
        {
            foreach (var kvp in _settings)
            {
                if (kvp.Value is JsonObject) continue;
                AddSettingEditor(_settings, kvp.Key, kvp.Value);
            }
        }
        else if (_settings[section] is JsonObject obj)
        {
            if (section.Equals("CountryAliases", StringComparison.OrdinalIgnoreCase))
                RenderDictionaryEditor(obj);
            else
                foreach (var kvp in obj.ToList()) AddSettingEditor(obj, kvp.Key, kvp.Value);
        }

        _settingsPanel.ResumeLayout();
    }

    private void AddSettingEditor(JsonObject owner, string key, JsonNode? value)
    {
        var row = new Panel
        {
            Height = value is JsonArray ? 158 : 64,
            Width = Math.Max(350, _settingsPanel.ClientSize.Width - 35),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.White,
            Tag = "fullwidth"
        };

        var label = new Label
        {
            Text = FriendlyName(key),
            AutoSize = true,
            ForeColor = TextDark,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Location = new Point(2, 2)
        };
        row.Controls.Add(label);

        if (TryGetBool(value, out bool boolValue))
        {
            var check = new CheckBox
            {
                Checked = boolValue,
                AutoSize = true,
                Text = boolValue ? "Enabled" : "Disabled",
                ForeColor = Muted,
                Location = new Point(4, 30)
            };
            check.CheckedChanged += (_, _) =>
            {
                owner[key] = check.Checked;
                check.Text = check.Checked ? "Enabled" : "Disabled";
            };
            row.Controls.Add(check);
        }
        else if (TryGetInt(value, out int intValue))
        {
            var numeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100000000,
                Value = Math.Clamp(intValue, 0, 100000000),
                Location = new Point(4, 28),
                Width = 220,
                BorderStyle = BorderStyle.FixedSingle
            };
            numeric.ValueChanged += (_, _) => owner[key] = decimal.ToInt32(numeric.Value);
            row.Controls.Add(numeric);
        }
        else if (value is JsonArray array)
        {
            var box = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Cascadia Mono", 9f),
                ForeColor = TextDark,
                BackColor = Color.White,
                Location = new Point(4, 28),
                Size = new Size(Math.Max(320, row.Width - 15), 120),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = string.Join(Environment.NewLine, array.Select(GetString))
            };
            box.TextChanged += (_, _) =>
            {
                var newArray = new JsonArray();
                foreach (string line in box.Lines.Select(x => x.Trim()).Where(x => x.Length > 0))
                    newArray.Add(line);
                owner[key] = newArray;
            };
            row.Controls.Add(box);
        }
        else if (value is JsonObject nestedObject)
        {
            var box = new TextBox
            {
                Text = nestedObject.ToJsonString(_jsonOptions),
                Location = new Point(4, 28),
                Width = Math.Max(320, row.Width - 15),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle
            };
            box.Validated += (_, _) =>
            {
                try
                {
                    owner[key] = JsonNode.Parse(box.Text);
                    box.BackColor = Color.White;
                }
                catch
                {
                    box.BackColor = Color.MistyRose;
                }
            };
            row.Controls.Add(box);
        }
        else
        {
            var box = StyledTextBox();
            box.Text = GetString(value);
            box.Location = new Point(4, 28);
            box.Width = Math.Max(320, row.Width - 15);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            box.TextChanged += (_, _) => owner[key] = box.Text;
            row.Controls.Add(box);
        }

        _settingsPanel.Controls.Add(row);
    }

    private void RenderDictionaryEditor(JsonObject obj)
    {
        var intro = new Label
        {
            AutoSize = false,
            Height = 42,
            Width = Math.Max(350, _settingsPanel.ClientSize.Width - 35),
            Text = "Edit aliases below. Add a new row for another country alias. Blank keys are ignored when saving.",
            ForeColor = Muted,
            Tag = "fullwidth"
        };
        _settingsPanel.Controls.Add(intro);

        var grid = new DataGridView
        {
            Width = Math.Max(450, _settingsPanel.ClientSize.Width - 35),
            Height = 430,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            RowHeadersVisible = false,
            Tag = "fullwidth"
        };
        grid.Columns.Add("Alias", "Input / Alias");
        grid.Columns.Add("Momentus", "Momentus Country Code");

        foreach (var kvp in obj)
            grid.Rows.Add(kvp.Key, GetString(kvp.Value));

        void RebuildDictionary()
        {
            var replacement = new JsonObject();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string key = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
                string val = Convert.ToString(row.Cells[1].Value)?.Trim() ?? string.Empty;
                if (key.Length == 0) continue;
                replacement[key] = val;
            }

            obj.Clear();
            foreach (var kvp in replacement) obj[kvp.Key] = kvp.Value?.DeepClone();
        }

        grid.CellEndEdit += (_, _) => RebuildDictionary();
        grid.UserDeletedRow += (_, _) => RebuildDictionary();
        grid.RowsAdded += (_, _) => { };

        _settingsPanel.Controls.Add(grid);
    }

    private void RenderRawJsonEditor()
    {
        var editor = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            AcceptsTab = true,
            WordWrap = false,
            Font = new Font("Cascadia Mono", 9f),
            ForeColor = TextDark,
            BackColor = Color.White,
            Width = Math.Max(500, _settingsPanel.ClientSize.Width - 35),
            Height = 470,
            Text = _settings.ToJsonString(_jsonOptions),
            Tag = "fullwidth"
        };

        var apply = StyledButton("Apply JSON", Navy, Color.White);
        apply.Size = new Size(110, 34);
        apply.Margin = new Padding(0, 8, 0, 0);
        apply.Click += (_, _) =>
        {
            try
            {
                var parsed = JsonNode.Parse(editor.Text) as JsonObject
                    ?? throw new InvalidOperationException("The root JSON value must be an object.");
                _settings = parsed;
                EnsureOptionalSettingsVisible();
                editor.BackColor = Color.White;
                PopulateSectionList();
                SetStatus("Raw JSON applied. Save Settings to write it to disk.");
            }
            catch (Exception ex)
            {
                editor.BackColor = Color.MistyRose;
                MessageBox.Show(ex.Message, "Invalid JSON", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        _settingsPanel.Controls.Add(editor);
        _settingsPanel.Controls.Add(apply);
    }

    private void SaveSettings(bool showConfirmation)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settingsPath))
                _settingsPath = Path.Combine(_programFolderText.Text.Trim(), "appsettings.json");

            if (!Directory.Exists(Path.GetDirectoryName(_settingsPath)!))
                throw new DirectoryNotFoundException("The importer Program folder does not exist.");

            _settings["DryRun"] = _dryRunRadio.Checked;

            if (_settings["ImportId"] is JsonObject importId && !GetBool(importId["PromptForImportId"], true))
                importId["Value"] = _importIdText.Text.Trim();

            string temp = _settingsPath + ".launcher.tmp";
            File.WriteAllText(temp, _settings.ToJsonString(_jsonOptions), new UTF8Encoding(false));
            File.Copy(temp, _settingsPath, overwrite: true);
            File.Delete(temp);

            SetStatus("Settings saved.");
            UpdatePhase0Status();

            if (showConfirmation)
                MessageBox.Show("Settings saved to appsettings.json.", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Could not save settings.", isError: true);
        }
    }

    private async Task RunImporterAsync()
    {
        if (_process is { HasExited: false }) return;

        _importerDirectory = _programFolderText.Text.Trim();
        if (!IsImporterDirectory(_importerDirectory))
        {
            MessageBox.Show(
                "Select the AccountImport Program folder first.",
                "Importer not found",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_liveRadio.Checked && !_liveConfirmCheck.Checked)
        {
            MessageBox.Show(
                "Check the live-mode confirmation before running a production import.",
                "Live confirmation required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        int maxImportIdLength = 7;
        if (_settings["ImportId"] is JsonObject importIdObj)
            maxImportIdLength = Math.Max(1, GetInt(importIdObj["MaxLength"], 7));

        string importId = _importIdText.Text.Trim();
        if (importId.Length > 0 && (importId.Length > maxImportIdLength || importId.Any(c => !char.IsLetterOrDigit(c))))
        {
            MessageBox.Show(
                $"Import ID must contain only letters/numbers and be no more than {maxImportIdLength} characters.",
                "Invalid Import ID",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        UpdatePhase0Status();
        if (!HasExactlyOnePhase0Workbook(out string phase0Message))
        {
            var result = MessageBox.Show(
                phase0Message + "\r\n\r\nRun anyway?",
                "Phase 0 check",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;
        }

        SaveSettings(showConfirmation: false);

        string projectFile = Path.Combine(_importerDirectory, "AccountImport.csproj");
        string appArguments = _dryRunRadio.Checked
            ? "--dry-run"
            : "--live --confirm-production-writes";

        _console.Clear();
        AppendOutput($"> dotnet run --project \"{projectFile}\" -- {appArguments}\r\n\r\n", isError: false);

        _importPromptAnswered = false;
        _affiliationPromptAnswered = false;
        _promptBuffer = string.Empty;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectFile}\" -- {appArguments}",
            WorkingDirectory = _importerDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        try
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start()) throw new InvalidOperationException("The dotnet process did not start.");

            SetRunningState(true);
            SetStatus(_dryRunRadio.Checked ? "Dry run in progress..." : "LIVE import in progress...");

            Task stdoutTask = PumpReaderAsync(_process.StandardOutput, isError: false);
            Task stderrTask = PumpReaderAsync(_process.StandardError, isError: true);
            await _process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            int exitCode = _process.ExitCode;
            AppendOutput($"\r\nProcess finished with exit code {exitCode}.\r\n", exitCode != 0);
            SetStatus(exitCode == 0 ? "Import process completed." : $"Importer stopped with exit code {exitCode}.", exitCode != 0);
        }
        catch (Exception ex)
        {
            AppendOutput("\r\nLAUNCHER ERROR: " + ex.Message + "\r\n", isError: true);
            SetStatus("Could not run importer.", isError: true);
            MessageBox.Show(
                ex.Message + "\r\n\r\nMake sure the .NET SDK is installed and 'dotnet' works in PowerShell.",
                "Could not start importer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetRunningState(false);
            _process?.Dispose();
            _process = null;
            UpdatePhase0Status();
        }
    }

    private async Task PumpReaderAsync(TextReader reader, bool isError)
    {
        char[] buffer = new char[256];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length));
            }
            catch
            {
                break;
            }

            if (read <= 0) break;
            string chunk = new(buffer, 0, read);
            AppendOutput(chunk, isError);

            if (!isError)
                await HandlePromptDetectionAsync(chunk);
        }
    }

    private async Task HandlePromptDetectionAsync(string chunk)
    {
        _promptBuffer += chunk;
        if (_promptBuffer.Length > 5000)
            _promptBuffer = _promptBuffer[^5000..];

        if (!_importPromptAnswered && _promptBuffer.Contains("Enter an Import ID for this run", StringComparison.OrdinalIgnoreCase))
        {
            _importPromptAnswered = true;
            await SendProcessInputAsync(_importIdText.Text.Trim());
        }

        if (!_affiliationPromptAnswered &&
            (_promptBuffer.Contains("Enter an affiliation/interest code", StringComparison.OrdinalIgnoreCase) ||
             _promptBuffer.Contains("Enter affiliation/interest code", StringComparison.OrdinalIgnoreCase)))
        {
            _affiliationPromptAnswered = true;
            await SendProcessInputAsync(_affiliationText.Text.Trim());
        }
    }

    private async Task SendProcessInputAsync(string value)
    {
        try
        {
            if (_process is null || _process.HasExited) return;
            await _process.StandardInput.WriteLineAsync(value);
            await _process.StandardInput.FlushAsync();
            AppendOutput(value.Length == 0 ? "[launcher sent blank input]\r\n" : $"[launcher supplied: {value}]\r\n", false);
        }
        catch (Exception ex)
        {
            AppendOutput("[launcher could not answer prompt: " + ex.Message + "]\r\n", true);
        }
    }

    private void TryStopProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                AppendOutput("\r\n[launcher requested stop]\r\n", true);
                SetStatus("Stop requested.", isError: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not stop process", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AppendOutput(string text, bool isError)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendOutput(text, isError)));
            return;
        }

        _console.SelectionStart = _console.TextLength;
        _console.SelectionLength = 0;
        _console.SelectionColor = isError ? Color.FromArgb(255, 150, 150) : Color.FromArgb(225, 232, 240);
        _console.AppendText(text);
        _console.SelectionColor = _console.ForeColor;
        _console.ScrollToCaret();
    }

    private void SetRunningState(bool running)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetRunningState(running)));
            return;
        }

        _runButton.Enabled = !running && (!_liveRadio.Checked || _liveConfirmCheck.Checked);
        _stopButton.Enabled = running;
        _dryRunRadio.Enabled = !running;
        _liveRadio.Enabled = !running;
        _liveConfirmCheck.Enabled = !running;
    }

    private void UpdateRunButtonState()
    {
        if (_runButton is null) return;
        bool processRunning = _process is { HasExited: false };
        _runButton.Enabled = !processRunning && (!_liveRadio.Checked || _liveConfirmCheck.Checked);
        _runButton.Text = _liveRadio.Checked ? "RUN LIVE IMPORT" : "RUN DRY IMPORT";
        _runButton.BackColor = _liveRadio.Checked ? Red : Navy;
        _runButton.FlatAppearance.BorderColor = _runButton.BackColor;
    }

    private void UpdatePhase0Status()
    {
        if (_phase0Status is null) return;

        if (!TryGetRootPath(out string rootPath))
        {
            _phase0Status.Text = "RootPath is not available in appsettings.json.";
            _phase0Status.ForeColor = Red;
            return;
        }

        string folder = Path.Combine(rootPath, "Phase 0");
        if (!Directory.Exists(folder))
        {
            _phase0Status.Text = "Phase 0 folder not found: " + folder;
            _phase0Status.ForeColor = Red;
            return;
        }

        string[] files = Directory.GetFiles(folder, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 1)
        {
            _phase0Status.Text = "Ready: " + Path.GetFileName(files[0]);
            _phase0Status.ForeColor = Color.FromArgb(33, 115, 70);
        }
        else if (files.Length == 0)
        {
            _phase0Status.Text = "No .xlsx file found in Phase 0.";
            _phase0Status.ForeColor = Red;
        }
        else
        {
            _phase0Status.Text = $"Phase 0 contains {files.Length} Excel files. The importer requires exactly one.";
            _phase0Status.ForeColor = Red;
        }
    }

    private bool HasExactlyOnePhase0Workbook(out string message)
    {
        message = string.Empty;
        if (!TryGetRootPath(out string rootPath))
        {
            message = "RootPath is missing from appsettings.json.";
            return false;
        }

        string folder = Path.Combine(rootPath, "Phase 0");
        if (!Directory.Exists(folder))
        {
            message = "Phase 0 folder does not exist: " + folder;
            return false;
        }

        string[] files = Directory.GetFiles(folder, "*.xlsx", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 1) return true;
        message = files.Length == 0
            ? "Phase 0 contains no .xlsx file."
            : $"Phase 0 contains {files.Length} .xlsx files; the importer requires exactly one.";
        return false;
    }

    private bool TryGetRootPath(out string rootPath)
    {
        rootPath = GetString(_settings["RootPath"]);
        return !string.IsNullOrWhiteSpace(rootPath);
    }

    private void OpenConfiguredFolder(string childFolder)
    {
        if (!TryGetRootPath(out string rootPath)) return;
        string path = Path.Combine(rootPath, childFolder);
        if (!Directory.Exists(path))
        {
            MessageBox.Show("Folder not found: " + path, "Folder not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void SetStatus(string message, bool isError = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(message, isError)));
            return;
        }

        _statusLabel.Text = message;
        _statusLabel.ForeColor = isError ? Red : Muted;
    }

    private static string FriendlyName(string key)
    {
        if (key == "Raw JSON") return key;
        string value = Regex.Replace(key, "([a-z0-9])([A-Z])", "$1 $2");
        value = value.Replace("Uri", "URI", StringComparison.Ordinal)
                     .Replace("Id", "ID", StringComparison.Ordinal)
                     .Replace("Api", "API", StringComparison.Ordinal);
        return value;
    }

    private static bool TryGetBool(JsonNode? node, out bool value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out value)) return true;
        value = false;
        return false;
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value)) return true;
        value = 0;
        return false;
    }

    private static bool GetBool(JsonNode? node, bool fallback) => TryGetBool(node, out bool value) ? value : fallback;
    private static int GetInt(JsonNode? node, int fallback) => TryGetInt(node, out int value) ? value : fallback;

    private static string GetString(JsonNode? node)
    {
        if (node is null) return string.Empty;
        if (node is JsonValue value && value.TryGetValue<string>(out string? text)) return text ?? string.Empty;
        return node.ToJsonString().Trim('"');
    }
}
