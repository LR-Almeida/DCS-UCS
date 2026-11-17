using UCS.Common;

namespace UCS.Client;

/// <summary>A saved UCS server (Favorites tab).</summary>
public sealed class FavoriteServer
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = Protocol.DefaultServerPort;

    public string Address => Port == Protocol.DefaultServerPort ? Host : $"{Host}:{Port}";
}

/// <summary>
/// Favorites tab: list of saved UCS servers. Connect (button or double click) copies the address to the
/// General tab and connects; "Add current server" saves what is in the General tab.
/// </summary>
internal sealed class FavoritesTab : TabPage
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Func<List<FavoriteServer>> _get;
    private readonly Action _save;
    private readonly Func<(string Host, int Port)> _current;
    private readonly Action<FavoriteServer> _connect;

    public FavoritesTab(Func<List<FavoriteServer>> get, Action save, Func<(string Host, int Port)> current, Action<FavoriteServer> connect)
        : base("Favorites")
    {
        _get = get;
        _save = save;
        _current = current;
        _connect = connect;

        _list.Columns.Add("Name", 220);
        _list.Columns.Add("Address", 220);
        _list.DoubleClick += (_, _) => ConnectSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) ConnectSelected();
            if (e.KeyCode == Keys.Delete) RemoveSelected();
        };
        _list.Resize += (_, _) => FitColumns();
        _list.Layout += (_, _) => FitColumns();

        Button Btn(string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(3, 3, 6, 3) };
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        buttons.Controls.Add(Btn("Connect", ConnectSelected));
        buttons.Controls.Add(Btn("Add current server", AddCurrent));
        buttons.Controls.Add(Btn("Add...", AddNew));
        buttons.Controls.Add(Btn("Edit...", EditSelected));
        buttons.Controls.Add(Btn("Remove", RemoveSelected));

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 6, 3, 3),
            Text = "Double-click a server to connect. \"Add current server\" saves the address typed in the General tab.",
        };
        // narrow window: buttons wrap onto a second line and the hint onto more lines (no horizontal scroll)
        Resize += (_, _) =>
        {
            var w = Math.Max(200, ClientSize.Width - 26);
            buttons.MaximumSize = new Size(w, 0);
            hint.MaximumSize = new Size(w, 0);
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_list, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        Controls.Add(layout);
        Refill();
    }

    private bool _fitting;

    private void FitColumns()
    {
        if (_fitting || _list.Columns.Count < 2) return;
        _fitting = true;
        try
        {
            // the vertical bar may appear later: keep its width free so no horizontal bar shows up
            var w = Math.Max(160, _list.ClientSize.Width - (_list.Items.Count > 12 ? 0 : SystemInformation.VerticalScrollBarWidth) - 2);
            _list.BeginUpdate();   // makes the list recompute its scroll bars after shrinking
            _list.Columns[0].Width = (int)(w * 0.58);
            _list.Columns[1].Width = -2;   // LVSCW_AUTOSIZE_USEHEADER: the last column fills exactly what is left
            _list.EndUpdate();
        }
        finally { _fitting = false; }
    }

    private void Refill(FavoriteServer? select = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var f in _get())
        {
            var item = new ListViewItem(new[] { f.Name, f.Address }) { Tag = f };
            _list.Items.Add(item);
            if (ReferenceEquals(f, select)) item.Selected = true;
        }
        _list.EndUpdate();
    }

    private FavoriteServer? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as FavoriteServer : null;

    private void ConnectSelected()
    {
        if (Selected is { } f) _connect(f);
        else MessageBox.Show(this, "Select a server in the list first.", "DCS-UCS", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddCurrent()
    {
        var (host, port) = _current();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Type a server address in the General tab first.", "DCS-UCS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var existing = _get().FirstOrDefault(f => string.Equals(f.Host, host, StringComparison.OrdinalIgnoreCase) && f.Port == port);
        if (existing != null)
        {
            Refill(existing);
            MessageBox.Show(this, $"{existing.Address} is already saved as \"{existing.Name}\".", "DCS-UCS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var fav = new FavoriteServer { Name = host, Host = host, Port = port };
        if (!FavoriteDialog.Edit(this, fav, "Add current server")) return;
        _get().Add(fav);
        _save();
        Refill(fav);
    }

    private void AddNew()
    {
        var fav = new FavoriteServer();
        if (!FavoriteDialog.Edit(this, fav, "Add server")) return;
        _get().Add(fav);
        _save();
        Refill(fav);
    }

    private void EditSelected()
    {
        if (Selected is not { } f) return;
        if (!FavoriteDialog.Edit(this, f, "Edit server")) return;
        _save();
        Refill(f);
    }

    private void RemoveSelected()
    {
        if (Selected is not { } f) return;
        if (MessageBox.Show(this, $"Remove \"{f.Name}\" ({f.Address}) from the favorites?", "DCS-UCS",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _get().Remove(f);
        _save();
        Refill();
    }
}

/// <summary>Small dialog to type a favorite's name and address (host or host:port).</summary>
internal static class FavoriteDialog
{
    public static bool Edit(IWin32Window owner, FavoriteServer fav, string title)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };
        var name = new TextBox { Width = 260, Text = fav.Name };
        var address = new TextBox { Width = 260, Text = fav.Host.Length > 0 ? fav.Address : "" };
        var error = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        grid.Controls.Add(new Label { Text = "Name:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, 0);
        grid.Controls.Add(name, 1, 0);
        grid.Controls.Add(new Label { Text = "Address:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, 1);
        grid.Controls.Add(address, 1, 1);
        grid.Controls.Add(new Label { Text = $"host or host:port (default port {Protocol.DefaultServerPort})", AutoSize = true, ForeColor = Color.DimGray }, 1, 2);
        grid.Controls.Add(error, 1, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons, 1, 4);
        form.Controls.Add(grid);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (!UdpHelper.TryParseAddress(address.Text, Protocol.DefaultServerPort, out var host, out var port))
            {
                error.Text = "Invalid address.";
                return;
            }
            fav.Host = host;
            fav.Port = port;
            fav.Name = string.IsNullOrWhiteSpace(name.Text) ? host : name.Text.Trim();
            form.DialogResult = DialogResult.OK;
        };
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
