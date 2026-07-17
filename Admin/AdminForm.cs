using IRCServer.Shared;

namespace IRCServer.Admin;

public sealed class AdminForm : Form
{
    private readonly AdminClient _client = new();

    // Connection bar
    private readonly TextBox _host = new() { Text = "127.0.0.1", Width = 90 };
    private readonly TextBox _port = new() { Text = "6668", Width = 55 };
    private readonly TextBox _pass = new() { Width = 90, UseSystemPasswordChar = true, PlaceholderText = "password" };
    private readonly Button _connectBtn = new() { Text = "Connect", Width = 90 };
    private readonly CheckBox _autoRefresh = new() { Text = "Auto-refresh (3s)", Checked = true, AutoSize = true };
    private readonly Label _status = new() { Text = "Disconnected", AutoSize = true, ForeColor = Color.Firebrick };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

    // Tabs
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // Stats
    private readonly ListView _statsView = NewListView("Metric", 220, "Value", 320);

    // Users
    private readonly ListView _usersView = NewListView("Nick", 110, "User", 90, "Host", 90, "Modes", 70, "Idle (s)", 70, "Channels", 260);

    // Channels
    private readonly ListView _channelsView = NewListView("Channel", 150, "Modes", 90, "Members", 70, "Bans", 50, "Topic", 300);
    private readonly ListView _membersView = NewListView("Prefix", 55, "Nick", 160);
    private readonly ListView _chanBansView = NewListView("Mask", 200, "Reason", 180, "Set By", 90);

    // Bans
    private readonly ListView _bansView = NewListView("Scope", 110, "Mask", 200, "Reason", 200, "Set By", 90, "When (UTC)", 150);

    // Log
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9) };

    public AdminForm()
    {
        Text = "IRC Server Administration";
        Width = 1000;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 560);

        BuildLayout();

        _connectBtn.Click += async (_, _) => await ToggleConnectAsync();
        _autoRefresh.CheckedChanged += (_, _) => { if (_client.IsConnected && _autoRefresh.Checked) _timer.Start(); else _timer.Stop(); };
        _timer.Tick += async (_, _) => await RefreshCurrentTabAsync();
        _tabs.SelectedIndexChanged += async (_, _) => await RefreshCurrentTabAsync();
        _channelsView.SelectedIndexChanged += (_, _) => ShowChannelDetail();

        FormClosing += (_, _) => { _timer.Stop(); _client.Dispose(); };
    }

    // ── Layout ──────────────────────────────────────────────────────────
    private void BuildLayout()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
        bar.Controls.Add(new Label { Text = "Host:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 0, 0) });
        bar.Controls.Add(_host);
        bar.Controls.Add(new Label { Text = "Port:", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
        bar.Controls.Add(_port);
        bar.Controls.Add(_pass);
        bar.Controls.Add(_connectBtn);
        bar.Controls.Add(_autoRefresh);
        bar.Controls.Add(_status);

        var bottom = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 520 };
        bottom.Panel1.Controls.Add(_tabs);

        var logPanel = new Panel { Dock = DockStyle.Fill };
        var broadcastBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        var broadcastBtn = new Button { Text = "Broadcast…", Width = 100 };
        broadcastBtn.Click += async (_, _) => await BroadcastAsync();
        var refreshBtn = new Button { Text = "Refresh Now", Width = 100 };
        refreshBtn.Click += async (_, _) => await RefreshCurrentTabAsync();
        broadcastBar.Controls.Add(refreshBtn);
        broadcastBar.Controls.Add(broadcastBtn);
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(broadcastBar);
        bottom.Panel2.Controls.Add(logPanel);

        _tabs.TabPages.Add(BuildStatsTab());
        _tabs.TabPages.Add(BuildUsersTab());
        _tabs.TabPages.Add(BuildChannelsTab());
        _tabs.TabPages.Add(BuildBansTab());

        Controls.Add(bottom);
        Controls.Add(bar);
    }

    private TabPage BuildStatsTab()
    {
        var page = new TabPage("Server Stats");
        _statsView.Dock = DockStyle.Fill;
        page.Controls.Add(_statsView);
        return page;
    }

    private TabPage BuildUsersTab()
    {
        var page = new TabPage("Users");
        _usersView.Dock = DockStyle.Fill;

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        AddButton(actions, "Kill", async () => await KillSelectedUserAsync());
        AddButton(actions, "Set User Mode…", async () => await SetUserModeAsync());
        actions.Controls.Add(new Label { Text = "  Modes: i=invisible, o=oper, w=wallops", AutoSize = true, Margin = new Padding(6, 8, 0, 0), ForeColor = Color.Gray });

        page.Controls.Add(_usersView);
        page.Controls.Add(actions);
        return page;
    }

    private TabPage BuildChannelsTab()
    {
        var page = new TabPage("Channels");
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 480 };

        _channelsView.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_channelsView);

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240 };
        _membersView.Dock = DockStyle.Fill;
        _chanBansView.Dock = DockStyle.Fill;
        right.Panel1.Controls.Add(WithHeader(_membersView, "Members"));
        right.Panel2.Controls.Add(WithHeader(_chanBansView, "Channel Bans"));
        split.Panel2.Controls.Add(right);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        AddButton(actions, "Set Chan Mode…", async () => await SetChanModeAsync());
        AddButton(actions, "Set Topic…", async () => await SetTopicAsync());
        AddButton(actions, "Kick Member…", async () => await KickMemberAsync());
        AddButton(actions, "Ban Mask…", async () => await BanInChannelAsync());
        AddButton(actions, "Remove Chan Ban", async () => await RemoveChanBanAsync());

        page.Controls.Add(split);
        page.Controls.Add(actions);
        return page;
    }

    private TabPage BuildBansTab()
    {
        var page = new TabPage("Bans");
        _bansView.Dock = DockStyle.Fill;

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        AddButton(actions, "Add Server Ban…", async () => await AddServerBanAsync());
        AddButton(actions, "Remove Selected Ban", async () => await RemoveSelectedBanAsync());

        page.Controls.Add(_bansView);
        page.Controls.Add(actions);
        return page;
    }

    // ── Connection ──────────────────────────────────────────────────────
    private async Task ToggleConnectAsync()
    {
        if (_client.IsConnected)
        {
            _timer.Stop();
            _client.Dispose();
            SetStatus("Disconnected", false);
            _connectBtn.Text = "Connect";
            return;
        }

        try
        {
            if (!int.TryParse(_port.Text, out var port)) { Warn("Invalid port"); return; }
            await _client.ConnectAsync(_host.Text.Trim(), port, _pass.Text);
            SetStatus($"Connected to {_host.Text}:{port}", true);
            _connectBtn.Text = "Disconnect";
            Log($"Connected to admin port {_host.Text}:{port}");
            if (_autoRefresh.Checked) _timer.Start();
            await RefreshCurrentTabAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Disconnected", false);
            Warn($"Connection failed: {ex.Message}");
        }
    }

    // ── Refresh ─────────────────────────────────────────────────────────
    private async Task RefreshCurrentTabAsync()
    {
        if (!_client.IsConnected) return;
        try
        {
            switch (_tabs.SelectedIndex)
            {
                case 0: await RefreshStatsAsync(); break;
                case 1: await RefreshUsersAsync(); break;
                case 2: await RefreshChannelsAsync(); break;
                case 3: await RefreshBansAsync(); break;
            }
        }
        catch (Exception ex) { Log($"Refresh error: {ex.Message}"); }
    }

    private async Task RefreshStatsAsync()
    {
        var r = await _client.SimpleAsync(AdminCommands.Stats);
        if (r.Stats is not { } s) return;
        _statsView.BeginUpdate();
        _statsView.Items.Clear();
        void Row(string k, string v) => _statsView.Items.Add(new ListViewItem(new[] { k, v }));
        Row("Version", s.Version);
        Row("Hostname", s.Hostname);
        Row("IRC Port", s.Port.ToString());
        Row("Admin Port", s.AdminPort.ToString());
        Row("Started (UTC)", s.StartTimeUtc.ToString("u"));
        Row("Uptime", TimeSpan.FromSeconds(s.UptimeSeconds).ToString(@"dd\.hh\:mm\:ss"));
        Row("Current Users", s.CurrentUsers.ToString());
        Row("Peak Users", s.PeakUsers.ToString());
        Row("Total Connections", s.TotalConnections.ToString());
        Row("Channels", s.ChannelCount.ToString());
        Row("Messages Received", s.MessagesReceived.ToString());
        Row("Messages Sent", s.MessagesSent.ToString());
        Row("Server Bans", s.ServerBanCount.ToString());
        Row("Channel Bans", s.ChannelBanCount.ToString());
        _statsView.EndUpdate();
    }

    private async Task RefreshUsersAsync()
    {
        var r = await _client.SimpleAsync(AdminCommands.Users);
        if (r.Users is not { } users) return;
        var selected = SelectedText(_usersView, 0);
        _usersView.BeginUpdate();
        _usersView.Items.Clear();
        foreach (var u in users.OrderBy(u => u.Nick))
        {
            var item = new ListViewItem(new[]
            {
                u.Nick, u.User, u.Host, u.Modes, ((int)u.IdleSeconds).ToString(), string.Join(", ", u.Channels)
            });
            _usersView.Items.Add(item);
            if (u.Nick == selected) item.Selected = true;
        }
        _usersView.EndUpdate();
    }

    private List<ChannelInfo> _channels = new();

    private async Task RefreshChannelsAsync()
    {
        var r = await _client.SimpleAsync(AdminCommands.Channels);
        if (r.Channels is not { } channels) return;
        _channels = channels;
        var selected = SelectedText(_channelsView, 0);
        _channelsView.BeginUpdate();
        _channelsView.Items.Clear();
        foreach (var c in channels.OrderBy(c => c.Name))
        {
            var item = new ListViewItem(new[] { c.Name, c.Modes, c.MemberCount.ToString(), c.Bans.Count.ToString(), c.Topic });
            _channelsView.Items.Add(item);
            if (c.Name == selected) item.Selected = true;
        }
        _channelsView.EndUpdate();
        ShowChannelDetail();
    }

    private void ShowChannelDetail()
    {
        var name = SelectedText(_channelsView, 0);
        var ch = _channels.FirstOrDefault(c => c.Name == name);
        _membersView.BeginUpdate();
        _membersView.Items.Clear();
        _chanBansView.BeginUpdate();
        _chanBansView.Items.Clear();
        if (ch != null)
        {
            foreach (var m in ch.Members.OrderByDescending(m => m.Prefix).ThenBy(m => m.Nick))
                _membersView.Items.Add(new ListViewItem(new[] { m.Prefix, m.Nick }));
            foreach (var b in ch.Bans)
                _chanBansView.Items.Add(new ListViewItem(new[] { b.Mask, b.Reason, b.SetBy }));
        }
        _membersView.EndUpdate();
        _chanBansView.EndUpdate();
    }

    private async Task RefreshBansAsync()
    {
        var r = await _client.SimpleAsync(AdminCommands.Bans);
        if (r.Bans is not { } bans) return;
        _bansView.BeginUpdate();
        _bansView.Items.Clear();
        foreach (var b in bans.OrderBy(b => b.Scope).ThenBy(b => b.Mask))
            _bansView.Items.Add(new ListViewItem(new[] { b.Scope, b.Mask, b.Reason, b.SetBy, b.SetUtc.ToString("u") }));
        _bansView.EndUpdate();
    }

    // ── Actions ─────────────────────────────────────────────────────────
    private async Task RunAction(Task<AdminResponse> call)
    {
        try
        {
            var r = await call;
            Log(r.Ok ? "✓ " + (r.Message ?? "OK") : "✗ " + (r.Error ?? "Error"));
            await RefreshCurrentTabAsync();
        }
        catch (Exception ex) { Log("✗ " + ex.Message); }
    }

    private async Task KillSelectedUserAsync()
    {
        var nick = SelectedText(_usersView, 0);
        if (nick == null) { Warn("Select a user"); return; }
        var reason = Prompt($"Kill {nick} — reason:", "Killed by admin");
        if (reason == null) return;
        await RunAction(_client.ActionAsync(AdminCommands.Kill, ("nick", nick), ("reason", reason)));
    }

    private async Task SetUserModeAsync()
    {
        var nick = SelectedText(_usersView, 0);
        if (nick == null) { Warn("Select a user"); return; }
        var modes = Prompt($"Set user modes on {nick} (e.g. +i, -o):", "+i");
        if (string.IsNullOrWhiteSpace(modes)) return;
        await RunAction(_client.ActionAsync(AdminCommands.UserMode, ("nick", nick), ("modes", modes)));
    }

    private async Task SetChanModeAsync()
    {
        var ch = SelectedText(_channelsView, 0);
        if (ch == null) { Warn("Select a channel"); return; }
        var modes = Prompt($"Set channel modes on {ch}\n(e.g. +m, +t, +o Alice, -o Alice, +v Bob):", "+t");
        if (string.IsNullOrWhiteSpace(modes)) return;
        await RunAction(_client.ActionAsync(AdminCommands.ChanMode, ("channel", ch), ("modes", modes)));
    }

    private async Task SetTopicAsync()
    {
        var ch = SelectedText(_channelsView, 0);
        if (ch == null) { Warn("Select a channel"); return; }
        var topic = Prompt($"Set topic for {ch}:", "");
        if (topic == null) return;
        await RunAction(_client.ActionAsync(AdminCommands.Topic, ("channel", ch), ("topic", topic)));
    }

    private async Task KickMemberAsync()
    {
        var ch = SelectedText(_channelsView, 0);
        var nick = SelectedText(_membersView, 1);
        if (ch == null) { Warn("Select a channel"); return; }
        if (nick == null) { Warn("Select a member"); return; }
        var reason = Prompt($"Kick {nick} from {ch} — reason:", "Kicked by admin");
        if (reason == null) return;
        await RunAction(_client.ActionAsync(AdminCommands.Kick, ("channel", ch), ("nick", nick), ("reason", reason)));
    }

    private async Task BanInChannelAsync()
    {
        var ch = SelectedText(_channelsView, 0);
        if (ch == null) { Warn("Select a channel"); return; }
        var mask = Prompt($"Ban mask on {ch} (e.g. *!*@somehost, baduser!*@*):", "*!*@*");
        if (string.IsNullOrWhiteSpace(mask)) return;
        var reason = Prompt("Ban reason:", "Banned by admin") ?? "Banned by admin";
        await RunAction(_client.ActionAsync(AdminCommands.Ban, ("scope", ch), ("mask", mask), ("reason", reason)));
    }

    private async Task RemoveChanBanAsync()
    {
        var ch = SelectedText(_channelsView, 0);
        var mask = SelectedText(_chanBansView, 0);
        if (ch == null || mask == null) { Warn("Select a channel and a ban"); return; }
        await RunAction(_client.ActionAsync(AdminCommands.Unban, ("scope", ch), ("mask", mask)));
    }

    private async Task AddServerBanAsync()
    {
        var mask = Prompt("Server ban mask (e.g. *!*@evilhost, nick!*@*):", "*!*@*");
        if (string.IsNullOrWhiteSpace(mask)) return;
        var reason = Prompt("Ban reason:", "Banned by admin") ?? "Banned by admin";
        await RunAction(_client.ActionAsync(AdminCommands.Ban, ("scope", "*"), ("mask", mask), ("reason", reason)));
    }

    private async Task RemoveSelectedBanAsync()
    {
        var scope = SelectedText(_bansView, 0);
        var mask = SelectedText(_bansView, 1);
        if (scope == null || mask == null) { Warn("Select a ban"); return; }
        await RunAction(_client.ActionAsync(AdminCommands.Unban, ("scope", scope), ("mask", mask)));
    }

    private async Task BroadcastAsync()
    {
        if (!_client.IsConnected) { Warn("Not connected"); return; }
        var text = Prompt("Broadcast a server notice to all users:", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        await RunAction(_client.ActionAsync(AdminCommands.Broadcast, ("text", text)));
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static ListView NewListView(params object[] cols)
    {
        var lv = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false, HideSelection = false };
        for (int i = 0; i < cols.Length; i += 2)
            lv.Columns.Add((string)cols[i], (int)cols[i + 1]);
        return lv;
    }

    private static Control WithHeader(Control inner, string title)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        inner.Dock = DockStyle.Fill;
        panel.Controls.Add(inner);
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 20, Font = new Font(Control.DefaultFont, FontStyle.Bold), Padding = new Padding(3, 3, 0, 0) });
        return panel;
    }

    private static void AddButton(Control parent, string text, Func<Task> onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
        b.Click += async (_, _) => await onClick();
        parent.Controls.Add(b);
    }

    private static string? SelectedText(ListView lv, int col)
    {
        if (lv.SelectedItems.Count == 0) return null;
        var item = lv.SelectedItems[0];
        return col < item.SubItems.Count ? item.SubItems[col].Text : null;
    }

    private void SetStatus(string text, bool connected)
    {
        _status.Text = text;
        _status.ForeColor = connected ? Color.ForestGreen : Color.Firebrick;
    }

    private void Log(string msg) =>
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");

    private void Warn(string msg) => MessageBox.Show(this, msg, "IRC Admin", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // Simple modal text prompt. Returns null if cancelled.
    private string? Prompt(string label, string def)
    {
        using var dlg = new Form { Text = "IRC Admin", Width = 460, Height = 190, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var lbl = new Label { Text = label, Left = 12, Top = 12, Width = 420, Height = 50 };
        var box = new TextBox { Left = 12, Top = 68, Width = 420, Text = def };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 276, Top = 104, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 357, Top = 104, Width = 75 };
        dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }
}
