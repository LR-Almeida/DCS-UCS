using System.Diagnostics;
using UCS.Common;

namespace UCS.Client;

public sealed class MainForm : Form
{
    private readonly SyncEngine _engine;
    private ClientConfig _cfg;

    private readonly TextBox _host = new() { Width = 220 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly CheckBox _autoConnect = new() { Text = "Aceitar Auto-Connect do Servidor DCS", AutoSize = true };
    private readonly CheckBox _connectOnStart = new() { Text = "Conectar ao iniciar", AutoSize = true };
    private readonly TextBox _room = new() { Width = 220, PlaceholderText = "(vazio = pareamento automático)" };
    private readonly TextBox _pilot = new() { Width = 220, MaxLength = 32 };
    private readonly Button _connect = new() { Text = "Conectar", Width = 110, Height = 30 };
    private readonly Button _save = new() { Text = "Salvar", Width = 80, Height = 30 };
    private readonly Button _profilesBtn = new() { Text = "Pasta de perfis", Width = 110, Height = 30 };

    private readonly Label _dcs = Value();
    private readonly Label _profile = Value();
    private readonly Label _session = Value();
    private readonly Label _conn = Value();
    private readonly Label _crew = Value();
    private readonly Label _traffic = Value();
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 300 };

    private static Label Value() => new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };

    public MainForm()
    {
        _cfg = ClientConfig.Load();
        _engine = new SyncEngine(_cfg);

        Text = $"DCS-UCS Client {Protocol.Version} - Universal Control Sync";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 560);
        Size = new Size(620, 640);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildLayout();
        LoadConfigToUi(_cfg);

        _engine.Log += s => SafeUi(() => AddLog(s));
        _engine.ConfigChanged += c => SafeUi(() => { _cfg = c.Clone(); LoadConfigToUi(c); });
        _connect.Click += async (_, _) => await ToggleConnectAsync();
        _save.Click += (_, _) => SaveConfig(showMessage: true);
        _profilesBtn.Click += (_, _) => OpenProfilesFolder();
        _autoConnect.CheckedChanged += (_, _) => SaveConfig(showMessage: false);
        _uiTimer.Tick += (_, _) => RefreshStatus();

        Shown += (_, _) =>
        {
            _engine.Start();
            _uiTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            _uiTimer.Stop();
            SaveConfig(showMessage: false);
            _engine.Dispose();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // --- Settings
        var settings = new GroupBox { Text = "Servidor UCS", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(grid, "IP / Host do servidor:", _host);
        AddRow(grid, "Porta:", _port);
        AddRow(grid, "Override manual de sala:", _room);
        AddRow(grid, "Nome do piloto:", _pilot);
        var checks = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        checks.Controls.Add(_autoConnect);
        checks.Controls.Add(_connectOnStart);
        grid.Controls.Add(new Label(), 0, grid.RowCount);
        grid.Controls.Add(checks, 1, grid.RowCount);
        grid.RowCount++;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_connect);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_profilesBtn);
        grid.Controls.Add(new Label(), 0, grid.RowCount);
        grid.Controls.Add(buttons, 1, grid.RowCount);
        grid.RowCount++;
        settings.Controls.Add(grid);
        root.Controls.Add(settings, 0, 0);

        // --- Status
        var status = new GroupBox { Text = "Status", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var sgrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(sgrid, "DCS:", _dcs);
        AddRow(sgrid, "Perfil:", _profile);
        AddRow(sgrid, "Sessão:", _session);
        AddRow(sgrid, "Conexão:", _conn);
        AddRow(sgrid, "Tripulação:", _crew);
        AddRow(sgrid, "Tráfego:", _traffic);
        status.Controls.Add(sgrid);
        root.Controls.Add(status, 0, 1);

        // --- Log
        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(6) };
        logBox.Controls.Add(_log);
        root.Controls.Add(logBox, 0, 2);
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) }, 0, row);
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(3, 4, 3, 3);
        grid.Controls.Add(control, 1, row);
        grid.RowCount++;
    }

    private void LoadConfigToUi(ClientConfig c)
    {
        _host.Text = c.ServerHost;
        _port.Value = Math.Clamp(c.ServerPort, 1, 65535);
        _autoConnect.Checked = c.AcceptAutoConnect;
        _connectOnStart.Checked = c.ConnectOnStartup;
        _room.Text = c.ManualRoom;
        _pilot.Text = c.PilotName;
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
        c.ManualRoom = _room.Text.Trim();
        c.PilotName = string.IsNullOrWhiteSpace(_pilot.Text) ? "Pilot" : _pilot.Text.Trim();
        return c;
    }

    private void SaveConfig(bool showMessage)
    {
        _cfg = ReadUi();
        _engine.UpdateConfig(_cfg);
        try
        {
            _cfg.Save();
            if (showMessage) AddLog($"Configuração salva em {ClientConfig.FilePath}");
        }
        catch (Exception ex)
        {
            AddLog("Falha ao salvar configuração: " + ex.Message);
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

    private void OpenProfilesFolder()
    {
        var dir = AppPaths.ProfileDirs().FirstOrDefault(Directory.Exists) ?? AppPaths.WritableProfileDir();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("Não foi possível abrir a pasta: " + ex.Message);
        }
    }

    private void RefreshStatus()
    {
        var s = _engine.GetStatus();

        if (!s.ExportAlive)
        {
            SetLabel(_dcs, s.Unit.Length > 0 ? $"Standby (Export inativo) - {s.Aircraft}" : "Standby (DCS não detectado / menu)", Color.DimGray);
        }
        else if (s.ExportActive)
        {
            SetLabel(_dcs, $"Ativo: {s.Aircraft}", Color.ForestGreen);
        }
        else
        {
            SetLabel(_dcs, s.Aircraft.Length > 0 ? $"Standby ({s.Aircraft})" : "Standby", Color.DarkOrange);
        }

        SetLabel(_profile, s.ProfileName != null ? $"{s.ProfileName} ({s.ProfileArgs} args)" : "nenhum", s.ProfileName != null ? Color.Black : Color.DimGray);

        string session;
        if (s.ManualRoom.Length > 0) session = $"Sala manual: {s.ManualRoom}";
        else if (s.Unit.Length > 0) session = $"{s.DcsServer} | {s.Group} | {s.Unit}";
        else session = "Aguardando slot no DCS";
        SetLabel(_session, session, Color.Black);

        if (!s.ConnectionRequested) SetLabel(_conn, "Desconectado", Color.Firebrick);
        else if (s.Connected) SetLabel(_conn, $"Conectado ({s.ServerAddress})", Color.ForestGreen);
        else SetLabel(_conn, $"Conectando... ({s.ServerAddress})", Color.DarkOrange);

        SetLabel(_crew, s.Peers.Count > 0 ? string.Join(", ", s.Peers) : (s.Connected ? "sozinho na cabine" : "-"), Color.Black);
        SetLabel(_traffic, $"enviados {s.SentArgs} / recebidos {s.ReceivedArgs}", Color.Black);

        _connect.Text = s.ConnectionRequested ? "Desconectar" : "Conectar";
    }

    private static void SetLabel(Label l, string text, Color color)
    {
        if (l.Text != text) l.Text = text;
        if (l.ForeColor != color) l.ForeColor = color;
    }

    private void AddLog(string text)
    {
        _log.Items.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (_log.Items.Count > 500) _log.Items.RemoveAt(0);
        _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
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
}
