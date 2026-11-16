using System.Diagnostics;
using UCS.Common;

namespace UCS.Client;

public sealed class MainForm : Form
{
    private const string RepoUrl = "https://github.com/LR-Almeida/DCS-UCS";

    private readonly SyncEngine _engine;
    private readonly UpdateService _updates;
    private bool _promptOpen;

    // Update tab
    private readonly TabPage _updateTab = new("Update");
    private readonly CheckBox _chkUpdates = new() { Text = "Check for updates automatically (at startup and every 6 hours)", AutoSize = true };
    private readonly CheckBox _chkPre = new() { Text = "Include pre-releases (alpha / beta)", AutoSize = true };
    private readonly Label _updCurrent = Value();
    private readonly Label _updLatest = Value();
    private readonly Label _updStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly ProgressBar _updProgress = new() { Width = 300, Height = 16, Visible = false };
    private readonly TextBox _updNotes = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, Dock = DockStyle.Fill };
    private readonly Button _btnCheck = new() { Text = "Check now", AutoSize = true, Height = 30 };
    private readonly Button _btnUpdNow = new() { Text = "Update now", AutoSize = true, Height = 30 };
    private readonly Button _btnCloseDcs = new() { Text = "Close DCS and update", AutoSize = true, Height = 30 };
    private readonly Button _btnWhenDcs = new() { Text = "Update when DCS closes", AutoSize = true, Height = 30 };
    private readonly Button _btnCancelSched = new() { Text = "Cancel scheduled update", AutoSize = true, Height = 30 };
    private readonly Button _btnSkip = new() { Text = "Skip this version", AutoSize = true, Height = 30 };
    private ClientConfig _cfg;

    // Settings
    private readonly TextBox _host = new() { Width = 240 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly TextBox _room = new() { Width = 240, PlaceholderText = "(empty = automatic pairing)" };
    private readonly TextBox _pilot = new() { Width = 240, MaxLength = 32 };
    private readonly CheckBox _autoPilot = new() { Text = "Detect from DCS", AutoSize = true };
    private readonly Label _detected = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly CheckBox _autoConnect = new() { Text = "Accept Auto-Connect from the DCS server", AutoSize = true };
    private readonly CheckBox _connectOnStart = new() { Text = "Connect on startup", AutoSize = true };
    private readonly CheckBox _autoProfiles = new() { Text = "Generate profiles automatically from installed mods", AutoSize = true };
    private readonly Button _connect = new() { Text = "Connect", Width = 110, Height = 30 };
    private readonly Button _save = new() { Text = "Save", Width = 80, Height = 30 };
    private readonly Button _profilesBtn = new() { Text = "Profiles folder", Width = 120, Height = 30 };

    // Status
    private readonly Label _dcs = Value();
    private readonly Label _profile = Value();
    private readonly Label _session = Value();
    private readonly Label _conn = Value();
    private readonly Label _crew = Value();
    private readonly Label _compat = Value();
    private readonly Label _traffic = Value();

    // Flight controls (experimental)
    private readonly CheckBox _flSend = new() { Text = "Send my stick / pedals / collective positions to the crew", AutoSize = true };
    private readonly CheckBox _flInject = new()
    {
        Text = "While I do not have the controls, move my stick / pedals / collective like the crew member flying (automatic, experimental)",
        AutoSize = true,
    };
    private readonly CheckBox _flDiag = new() { Text = @"Diagnostic recording (Saved Games\DCS\Logs\UCS\UCS-diag-*.csv)", AutoSize = true };
    private readonly Label _flStatus = Value();

    // Log
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    private readonly TabPage _logTab = new("Log");
    private int _unseenLog;
    private readonly ClientLogFile _logFile = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 300 };

    private static Label Value() => new() { AutoSize = true, MaximumSize = new Size(430, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };

    public MainForm()
    {
        _cfg = ClientConfig.Load();
        _engine = new SyncEngine(_cfg);
        _updates = new UpdateService(() => _cfg);

        Text = $"DCS-UCS Client {Protocol.Version} - Universal Control Sync";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 360);
        Size = new Size(640, 700);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildLogTab());
        _tabs.TabPages.Add(BuildUpdateTab());
        _tabs.TabPages.Add(BuildTextTab("Help", HelpText));
        _tabs.TabPages.Add(BuildAboutTab());
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _logTab) { _unseenLog = 0; _logTab.Text = "Log"; }
            if (_tabs.SelectedTab == _updateTab) _updateTab.Text = "Update";
        };
        Controls.Add(_tabs);

        LoadConfigToUi(_cfg);

        _engine.Log += s => SafeUi(() => AddLog(s));
        _engine.ConfigChanged += c => SafeUi(() => { _cfg = c.Clone(); LoadConfigToUi(c); });
        _connect.Click += async (_, _) => await ToggleConnectAsync();
        _save.Click += (_, _) => SaveConfig(showMessage: true);
        _profilesBtn.Click += (_, _) => OpenFolder(AppPaths.ProfileDirs().FirstOrDefault(Directory.Exists) ?? AppPaths.WritableProfileDir());
        _autoConnect.CheckedChanged += (_, _) => SaveConfig(showMessage: false);
        _autoProfiles.CheckedChanged += (_, _) => SaveConfig(showMessage: false);
        _autoPilot.CheckedChanged += (_, _) => { _pilot.Enabled = !_autoPilot.Checked; SaveConfig(showMessage: false); };
        _uiTimer.Tick += (_, _) => RefreshStatus();

        _updates.Log += s => SafeUi(() => AddLog(s));
        _updates.StateChanged += _ => SafeUi(RefreshUpdateTab);
        _updates.UpdateFound += r => SafeUi(() => ShowUpdatePrompt(r));
        _updates.ExitRequested += () => SafeUi(Close);
        _chkUpdates.CheckedChanged += (_, _) => SaveConfig(showMessage: false);
        _flSend.CheckedChanged += (_, _) => { if (!_loadingUi) SaveConfig(showMessage: false); };
        _flDiag.CheckedChanged += (_, _) => { if (!_loadingUi) SaveConfig(showMessage: false); };
        _flInject.CheckedChanged += (_, _) => { if (!_loadingUi) SaveConfig(showMessage: false); };
        _chkPre.CheckedChanged += (_, _) => SaveConfig(showMessage: false);
        _btnCheck.Click += async (_, _) => await _updates.CheckAsync(manual: true);
        _btnUpdNow.Click += async (_, _) => await _updates.UpdateNowAsync();
        _btnCloseDcs.Click += async (_, _) => await _updates.CloseDcsAndUpdateAsync();
        _btnWhenDcs.Click += async (_, _) => await _updates.UpdateWhenDcsClosesAsync();
        _btnCancelSched.Click += (_, _) => _updates.CancelScheduled();
        _btnSkip.Click += (_, _) => SkipCurrentRelease();

        Shown += (_, _) =>
        {
            _engine.Start();
            _uiTimer.Start();
            _updates.StartAutomaticChecks();
            RefreshUpdateTab();
        };
        FormClosing += (_, _) =>
        {
            _uiTimer.Stop();
            SaveConfig(showMessage: false);
            _updates.Dispose();
            _logFile.Dispose();
            _engine.Dispose();
        };
    }

    // ------------------------------------------------------------------ layout

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General");
        // Scrollable container: the content keeps its natural size, the panel scrolls
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        // Not docked: a docked child is ignored by AutoScroll, so no scroll bars would appear
        var root = new TableLayoutPanel { Location = new Point(8, 8), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };

        var settings = new GroupBox { Text = "UCS server", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var grid = NewGrid();
        AddRow(grid, "Server IP / host:", _host);
        AddRow(grid, "Port:", _port);
        AddRow(grid, "Manual room override:", _room);
        var pilotRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        pilotRow.Controls.Add(_pilot);
        _autoPilot.Margin = new Padding(8, 6, 3, 3);
        pilotRow.Controls.Add(_autoPilot);
        AddRow(grid, "Pilot name:", pilotRow);
        AddRow(grid, "", _detected);
        var checks = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        checks.Controls.Add(_autoConnect);
        checks.Controls.Add(_autoProfiles);
        checks.Controls.Add(_connectOnStart);
        AddRow(grid, "", checks);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_connect);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_profilesBtn);
        AddRow(grid, "", buttons);
        settings.Controls.Add(grid);
        root.Controls.Add(settings);

        var status = new GroupBox { Text = "Status", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var sgrid = NewGrid();
        AddRow(sgrid, "DCS:", _dcs);
        AddRow(sgrid, "Profile:", _profile);
        AddRow(sgrid, "Session:", _session);
        AddRow(sgrid, "Connection:", _conn);
        AddRow(sgrid, "Crew:", _crew);
        AddRow(sgrid, "Compatibility:", _compat);
        AddRow(sgrid, "Traffic:", _traffic);
        status.Controls.Add(sgrid);
        root.Controls.Add(status);

        var flight = new GroupBox { Text = "Flight controls (experimental)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var fgrid = NewGrid();
        var fchecks = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        fchecks.Controls.Add(_flSend);
        fchecks.Controls.Add(_flInject);
        fchecks.Controls.Add(_flDiag);
        AddRow(fgrid, "", fchecks);
        AddRow(fgrid, "Flight:", _flStatus);
        flight.Controls.Add(fgrid);
        root.Controls.Add(flight);

        scroll.Controls.Add(root);
        // Tell the panel how big the content is so it always shows scroll bars when needed
        void UpdateScrollArea() => scroll.AutoScrollMinSize = new Size(root.PreferredSize.Width + 16, root.PreferredSize.Height + 16);
        root.SizeChanged += (_, _) => UpdateScrollArea();
        scroll.Layout += (_, _) => UpdateScrollArea();
        page.Controls.Add(scroll);
        return page;
    }

    private TabPage BuildUpdateTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var options = new GroupBox { Text = "Settings", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        opts.Controls.Add(_chkUpdates);
        opts.Controls.Add(_chkPre);
        opts.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.DimGray,
            Text = "Updates are downloaded from the project's GitHub releases and verified (SHA-256) before installing. " +
                   "The installer also updates the DCS scripts; DCS loads them on its next start.",
        });
        options.Controls.Add(opts);
        root.Controls.Add(options, 0, 0);

        var info = NewGrid();
        AddRow(info, "Installed version:", _updCurrent);
        AddRow(info, "Latest version:", _updLatest);
        AddRow(info, "Status:", _updStatus);
        AddRow(info, "", _updProgress);
        var check = new FlowLayoutPanel { AutoSize = true };
        check.Controls.Add(_btnCheck);
        check.Controls.Add(Link("Open release page", GitHubReleases.ReleasesPage));
        AddRow(info, "", check);
        root.Controls.Add(info, 0, 1);

        var notesBox = new GroupBox { Text = "Release notes", Dock = DockStyle.Fill, Padding = new Padding(6) };
        notesBox.Controls.Add(_updNotes);
        root.Controls.Add(notesBox, 0, 2);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        actions.Controls.AddRange(new Control[] { _btnUpdNow, _btnCloseDcs, _btnWhenDcs, _btnCancelSched, _btnSkip });
        root.Controls.Add(actions, 0, 3);

        _updateTab.Controls.Add(root);
        return _updateTab;
    }

    private void RefreshUpdateTab()
    {
        var st = _updates.State;
        var dcs = UpdateService.IsDcsRunning();
        _updCurrent.Text = UpdateService.CurrentVersion;
        _updLatest.Text = st.Release?.Version?.ToString() ?? (st.Stage == UpdateStage.UpToDate ? UpdateService.CurrentVersion : "-");
        _updStatus.Text = st.Message;
        _updStatus.ForeColor = st.Stage switch
        {
            UpdateStage.Error => Color.Firebrick,
            UpdateStage.Available or UpdateStage.WaitingForDcs => Color.DarkOrange,
            UpdateStage.UpToDate => Color.ForestGreen,
            _ => SystemColors.ControlText,
        };
        _updProgress.Visible = st.Stage == UpdateStage.Downloading;
        _updProgress.Value = Math.Clamp(st.Progress, 0, 100);
        var notes = st.Release?.Body ?? "";
        notes = notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        if (_updNotes.Text != notes) _updNotes.Text = notes;

        var busy = st.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.ClosingDcs or UpdateStage.Installing;
        var canUpdate = st.Release != null && !busy && st.Stage != UpdateStage.WaitingForDcs;
        _btnCheck.Enabled = !busy && st.Stage != UpdateStage.WaitingForDcs;
        _btnUpdNow.Enabled = canUpdate;
        _btnCloseDcs.Enabled = canUpdate && dcs;
        _btnWhenDcs.Enabled = canUpdate && dcs;
        _btnSkip.Enabled = canUpdate;
        _btnCancelSched.Visible = st.Stage == UpdateStage.WaitingForDcs;
        if (st.Release != null && st.Stage == UpdateStage.Available && _tabs.SelectedTab != _updateTab)
            _updateTab.Text = "Update (1)";
    }

    private void ShowUpdatePrompt(ReleaseInfo release)
    {
        if (_promptOpen) return;
        _promptOpen = true;
        try
        {
            using var dlg = new UpdatePromptForm(release);
            dlg.ShowDialog(this);
            switch (dlg.Choice)
            {
                case UpdateChoice.UpdateNow: _ = _updates.UpdateNowAsync(); break;
                case UpdateChoice.CloseDcsAndUpdate: _ = _updates.CloseDcsAndUpdateAsync(); break;
                case UpdateChoice.WhenDcsCloses: _ = _updates.UpdateWhenDcsClosesAsync(); break;
                case UpdateChoice.Skip: SkipCurrentRelease(); break;
            }
        }
        finally
        {
            _promptOpen = false;
        }
    }

    private void SkipCurrentRelease()
    {
        var v = _updates.State.Release?.Version?.ToString();
        if (v == null) return;
        _cfg.SkippedVersion = v;
        SaveConfig(showMessage: false);
        AddLog($"Version {v} skipped. It will not be offered automatically again (use 'Check now' to see it).");
    }

    private TabPage BuildLogTab()
    {
        var copy = new Button { Text = "Copy all", Width = 90, Height = 28 };
        var saveBtn = new Button { Text = "Save to file...", Width = 110, Height = 28 };
        var clear = new Button { Text = "Clear", Width = 80, Height = 28 };
        copy.Click += (_, _) =>
        {
            var text = string.Join(Environment.NewLine, _log.Items.Cast<object>());
            if (text.Length > 0) Clipboard.SetText(text);
        };
        saveBtn.Click += (_, _) => SaveLog();
        clear.Click += (_, _) => _log.Items.Clear();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        bar.Controls.AddRange(new Control[] { copy, saveBtn, clear });
        _logTab.Padding = new Padding(6);
        _logTab.Controls.Add(_log);
        _logTab.Controls.Add(bar);
        return _logTab;
    }

    private static TabPage BuildTextTab(string title, string text)
    {
        var page = new TabPage(title) { Padding = new Padding(6) };
        page.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Font = new Font("Segoe UI", 9.5f),
            Text = text.Replace("\n", Environment.NewLine),
        });
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = new TabPage("About") { Padding = new Padding(6) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoScroll = true };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = $"DCS-UCS - Universal Control Sync\nVersion {Protocol.Version}",
            AutoSize = true,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(6, 6, 6, 10),
        }, 0, 0);
        panel.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
            Text = AboutText.Replace("\n", Environment.NewLine),
        }, 0, 1);
        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(6) };
        links.Controls.Add(Link("Project page / releases", RepoUrl));
        links.Controls.Add(Link("Report an issue", RepoUrl + "/issues"));
        links.Controls.Add(Link("Data folder", null, () => OpenFolder(AppPaths.UserDir)));
        panel.Controls.Add(links, 0, 2);
        page.Controls.Add(panel);
        return page;
    }

    private static LinkLabel Link(string text, string? url, Action? action = null)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 18, 0) };
        l.LinkClicked += (_, _) =>
        {
            if (action != null) action();
            else if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        };
        return l;
    }

    private static TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) }, 0, row);
        control.Anchor = AnchorStyles.Left;
        if (control is not FlowLayoutPanel) control.Margin = new Padding(3, 4, 3, 3);
        grid.Controls.Add(control, 1, row);
        grid.RowCount++;
    }

    // ------------------------------------------------------------------ config

    private bool _loadingUi;

    private void LoadConfigToUi(ClientConfig c)
    {
        _loadingUi = true;
        _flSend.Checked = c.FlightMirrorSend;
        _flInject.Checked = c.FlightMirrorInject;
        _flDiag.Checked = c.FlightDiagnostics;
        _host.Text = c.ServerHost;
        _port.Value = Math.Clamp(c.ServerPort, 1, 65535);
        _autoConnect.Checked = c.AcceptAutoConnect;
        _connectOnStart.Checked = c.ConnectOnStartup;
        _autoProfiles.Checked = c.AutoGenerateProfiles;
        _autoPilot.Checked = c.AutoDetectPilotName;
        _chkUpdates.Checked = c.CheckForUpdates && InstallOptions.CheckForUpdatesAllowed;
        if (!InstallOptions.CheckForUpdatesAllowed)
        {
            _chkUpdates.Enabled = false;
            _chkUpdates.Text = "Check for updates automatically (turned off in the installer - run Setup again to change)";
        }
        _chkPre.Checked = c.IncludePreReleases;
        _pilot.Enabled = !c.AutoDetectPilotName;
        _room.Text = c.ManualRoom;
        _pilot.Text = c.PilotName;
        _loadingUi = false;
    }

    private ClientConfig ReadUi()
    {
        var c = _cfg.Clone();
        if (UdpHelper.TryParseAddress(_host.Text, (int)_port.Value, out var host, out var port))
        {
            c.ServerHost = host;
            c.ServerPort = port;
        }
        else
        {
            c.ServerHost = _host.Text.Trim();
            c.ServerPort = (int)_port.Value;
        }
        c.AcceptAutoConnect = _autoConnect.Checked;
        c.ConnectOnStartup = _connectOnStart.Checked;
        c.AutoGenerateProfiles = _autoProfiles.Checked;
        c.AutoDetectPilotName = _autoPilot.Checked;
        c.CheckForUpdates = _chkUpdates.Checked && InstallOptions.CheckForUpdatesAllowed;
        c.IncludePreReleases = _chkPre.Checked;
        c.ManualRoom = _room.Text.Trim();
        c.PilotName = string.IsNullOrWhiteSpace(_pilot.Text) ? "Pilot" : _pilot.Text.Trim();
        c.FlightMirrorSend = _flSend.Checked;
        c.FlightMirrorInject = _flInject.Checked;
        c.FlightDiagnostics = _flDiag.Checked;
        return c;
    }

    private void SaveConfig(bool showMessage)
    {
        _cfg = ReadUi();
        _engine.UpdateConfig(_cfg);
        try
        {
            _cfg.Save();
            if (showMessage) AddLog($"Settings saved to {ClientConfig.FilePath}");
        }
        catch (Exception ex)
        {
            AddLog("Could not save settings: " + ex.Message);
        }
        LoadConfigToUi(_cfg);
    }

    private async Task ToggleConnectAsync()
    {
        _connect.Enabled = false;
        try
        {
            if (_engine.GetStatus().ConnectionRequested)
            {
                _engine.Disconnect();
            }
            else
            {
                SaveConfig(showMessage: false);
                await _engine.ConnectAsync();
            }
        }
        finally
        {
            _connect.Enabled = true;
            RefreshStatus();
        }
    }

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("Could not open folder: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ status

    private void RefreshStatus()
    {
        var s = _engine.GetStatus();

        if (!s.ExportAlive)
            SetLabel(_dcs, s.Unit.Length > 0 ? $"Standby (Export not running) - {s.Aircraft}" : "Standby (DCS not detected / menu)", Color.DimGray);
        else if (s.ExportActive)
            SetLabel(_dcs, $"Active: {s.Aircraft}", Color.ForestGreen);
        else
            SetLabel(_dcs, s.Aircraft.Length > 0 ? $"Standby ({s.Aircraft})" : "Standby", Color.DarkOrange);

        if (s.GeneratingProfile)
            SetLabel(_profile, "generating from the installed mod...", Color.DarkOrange);
        else if (s.ProfileName != null)
            SetLabel(_profile, $"{s.ProfileName} ({s.ProfileArgs} controls) - mod {(s.ModVersion.Length > 0 ? s.ModVersion : "no version")} - profile {s.ProfileHash}", Color.Black);
        else
            SetLabel(_profile, "none", Color.DimGray);

        string session;
        if (s.ManualRoom.Length > 0) session = $"Manual room: {s.ManualRoom}";
        else if (s.Unit.Length > 0) session = $"{s.DcsServer} | {s.Group} | {s.Unit}";
        else session = "Waiting for a DCS slot";
        SetLabel(_session, session, Color.Black);

        if (!s.ConnectionRequested) SetLabel(_conn, "Disconnected", Color.Firebrick);
        else if (s.Connected) SetLabel(_conn, $"Connected ({s.ServerAddress})" + (s.ServerVersion.Length > 0 ? $" - server {s.ServerVersion}" : ""), Color.ForestGreen);
        else SetLabel(_conn, $"Connecting... ({s.ServerAddress})", Color.DarkOrange);

        SetLabel(_crew, s.Peers.Count > 0 ? string.Join(", ", s.Peers) : (s.Connected ? "alone in the cockpit" : "-"), Color.Black);

        if (s.Incompatible.Count > 0)
            SetLabel(_compat, "ERROR: " + string.Join(" | ", s.Incompatible.Select(i => SyncEngine.DescribeIncompatibility(i, s.ModVersion, s.ProfileHash))), Color.Firebrick);
        else if (s.Peers.Count > 0)
            SetLabel(_compat, "OK - same mod version and profile", Color.ForestGreen);
        else
            SetLabel(_compat, "-", Color.DimGray);

        SetLabel(_traffic, $"sent {s.SentArgs} / received {s.ReceivedArgs}", Color.Black);

        if (s.ProfileName == null)
            SetLabel(_flStatus, "-", Color.DimGray);
        else if (s.FlightArgs == 0)
            SetLabel(_flStatus, "no flight-control animations found in this mod", Color.DimGray);
        else if (s.Connected && !s.FlightServerSupport)
            SetLabel(_flStatus, $"{s.FlightArgs} animations - server too old (needs UCS-Server 0.1.2+)", Color.DarkOrange);
        else
        {
            var who = s.HasControls switch
            {
                true => "you have the controls",
                false => "crew member has the controls",
                _ => $"seat {s.Seat}",
            };
            SetLabel(_flStatus, $"{s.FlightArgs} animations - {who} - packets sent {s.FlightSent} / received {s.FlightReceived}" +
                                (s.FlightInjectActive ? " - MIRRORING" : ""),
                     s.FlightInjectActive ? Color.DarkBlue : Color.Black);
        }

        var detected = _autoPilot.Checked
            ? (s.DetectedPilot.Length > 0 ? $"Using \"{s.DetectedPilot}\" (from DCS)" : "Not detected yet - start DCS (using the name above)")
            : "";
        SetLabel(_detected, detected, Color.DimGray);

        _connect.Text = s.ConnectionRequested ? "Disconnect" : "Connect";
    }

    private static void SetLabel(Label l, string text, Color color)
    {
        if (l.Text != text) l.Text = text;
        if (l.ForeColor != color) l.ForeColor = color;
    }

    // ------------------------------------------------------------------ log

    private void AddLog(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _log.Items.Add(line);
        _logFile.Write(line);
        while (_log.Items.Count > 2000) _log.Items.RemoveAt(0);
        _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
        if (_tabs.SelectedTab != _logTab)
        {
            _unseenLog++;
            _logTab.Text = $"Log ({_unseenLog})";
        }
    }

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text file (*.txt)|*.txt",
            InitialDirectory = _logFile.Directory,
            FileName = $"ucs-client-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllLines(dlg.FileName, _log.Items.Cast<object>().Select(o => o.ToString() ?? ""));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DCS-UCS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException)
        {
            // Form not created yet / closing
        }
    }

    // ------------------------------------------------------------------ texts

    private const string HelpText = """
QUICK START

1. Start the UCS server
   One crew member (or your squadron / server admin) runs UCS-Server.exe.
   It listens on UDP port 5055 and shows at startup the address to share ("PUBLIC address"
   for players over the internet, "Local address" for the same network or a VPN like Radmin).
   For players over the internet, open/forward UDP 5055 on the server's router.

2. Connect the client
   - General tab: type the server IP or host name (default port 5055) and click Connect.
   - Or leave "Accept Auto-Connect from the DCS server" checked: if the DCS server runs
     the UCS server hook, you are connected automatically when you join it.
   - "Connection" shows Connected when the server answers.

3. Fly
   - Join the same DCS server and the same aircraft (same unit, or another seat of it).
   - The first time you enter a mod aircraft, UCS generates its profile from your installed
     mod ("Profile: generating..."). This takes a few seconds and happens again only when
     the mod is updated.
   - "DCS" shows Active: <aircraft> when cockpit sync is running.
   - "Crew" lists the other crew members and "Compatibility" must say OK.

4. Flip a switch: it moves in your crew mate's cockpit too.

FLIGHT CONTROLS (EXPERIMENTAL)
   - "Send my stick / pedals / collective positions": your flight-control positions are sent to
     the crew (view only). Keep it on.
   - "While I do not have the controls, move my stick...": automatic. While the other crew
     member has the controls, their stick, pedals and collective are fed into your seat so
     your cockpit shows what they do. When you take the controls (key C) it stops at once,
     and it resumes when you hand them back. "Flight:" shows who has the controls. Moving
     your own controls while it runs pauses that axis for 3 s.
   - "Diagnostic recording": writes Saved Games\DCS\Logs\UCS\UCS-diag-*.csv and lists in the Log
     tab any cockpit animation that flickers. Send the file from both seats with bug reports.

PAIRING
   Players are paired automatically by DCS server + group + unit.
   If that does not work (single player tests, mods where each crew member uses a different
   unit), type the same word in "Manual room override" on every client.

STANDBY
   UCS does nothing (Standby) in the menu, in official ED modules (they have their own
   multicrew) and in aircraft without a profile. This is normal.

COMPATIBILITY ERROR
   Every crew member must have the same mod version. If versions match but the profiles
   differ, delete the aircraft profile (Profiles folder button) on every PC and fly again.

PROBLEMS?
   - "Standby (DCS not detected)": check that UCS-Export.lua is loaded (look for "UCS" in
     Saved Games\DCS\Logs\dcs.log).
   - "Connecting..." forever: is UCS-Server.exe running? Is UDP 5055 open?
   - The log is also saved automatically to Saved Games\DCS\Logs\UCS\UCS-Client.log
     (previous session: UCS-Client.old.log). Attach it to your bug report.
""";

    private const string AboutText = """
DCS-UCS (Universal Control Sync) is a free, open-source and decentralized tool that
synchronizes cockpit switches, buttons and knobs between crew members of DCS World
community mods that have no native multicrew, in the spirit of SRS and YourControls.

How it works
- UCS-Export.lua reads the cockpit state inside DCS and applies the commands received
  from your crew mates. UCS-Hook.lua detects your server, slot and aircraft.
- UCS-Client.exe (this program) links DCS to a UCS-Server.exe, which any player,
  squadron or DCS server admin can host. There is no central service.
- Aircraft profiles are generated locally from the mods installed on your PC. No mod
  files or profiles are distributed, and only crew members with the same mod version and
  profile are synchronized.
- UCS stays in Standby on official Eagle Dynamics modules and never interferes with them.

License
MIT License - free for any use. Provided "as is", without warranty.

DCS World is a trademark of Eagle Dynamics SA. This project is not affiliated with or
endorsed by Eagle Dynamics or by the authors of the community mods it works with.
""";
}
