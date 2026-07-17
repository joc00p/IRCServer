using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using IRCServer.Shared;

namespace IRCServer;

public class IrcServer(string hostname, int port, int adminPort, string? adminPass)
{
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IrcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BanInfo> _serverBans = new(StringComparer.OrdinalIgnoreCase);

    public string Hostname => hostname;
    public int Port => port;
    public int AdminPort => adminPort;
    public string? AdminPass => adminPass;
    public DateTime StartTimeUtc { get; } = DateTime.UtcNow;

    private long _messagesReceived;
    private long _messagesSent;
    private long _totalConnections;
    private int _peakUsers;

    public void CountReceived() => Interlocked.Increment(ref _messagesReceived);
    public void CountSent() => Interlocked.Increment(ref _messagesSent);

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"IRC server listening on {hostname}:{port}");

        // Admin control interface on its own loopback port
        var admin = new AdminInterface(this, adminPort);
        _ = admin.RunAsync();
        Console.WriteLine($"Admin interface listening on 127.0.0.1:{adminPort}" +
                          (adminPass != null ? " (password required)" : " (no password)"));

        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(tcp);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        Interlocked.Increment(ref _totalConnections);
        var session = new ClientSession(tcp, hostname, this);
        try
        {
            await session.RunAsync();
        }
        finally
        {
            if (session.Nick != null)
            {
                _clients.TryRemove(session.Nick, out _);
                RemoveFromAllChannels(session, "Client disconnected");
            }
            Console.WriteLine($"[-] {session.Nick ?? "(unregistered)"} disconnected");
        }
    }

    // Called once registration is complete (NICK + USER received)
    public bool RegisterClient(ClientSession session, string nick)
    {
        if (_clients.ContainsKey(nick))
            return false;
        _clients[nick] = session;
        // Track peak concurrent users
        int now = _clients.Count;
        int peak;
        do { peak = _peakUsers; if (now <= peak) break; } while (Interlocked.CompareExchange(ref _peakUsers, now, peak) != peak);
        Console.WriteLine($"[+] {nick} registered");
        return true;
    }

    public void RenameClient(ClientSession session, string oldNick, string newNick)
    {
        _clients.TryRemove(oldNick, out _);
        _clients[newNick] = session;
        foreach (var ch in _channels.Values)
            ch.RenameMember(oldNick, newNick);
    }

    public IrcChannel GetOrCreateChannel(string name) =>
        _channels.GetOrAdd(name, n => new IrcChannel(n));

    public bool TryGetChannel(string name, out IrcChannel channel) =>
        _channels.TryGetValue(name, out channel!);

    public void RemoveFromAllChannels(ClientSession session, string reason)
    {
        foreach (var ch in _channels.Values)
        {
            ch.Remove(session, reason);
            // Drop empty channels so stats stay meaningful
            if (ch.MemberNicks.Count() == 0 && ch.Bans.Count == 0)
                _channels.TryRemove(ch.Name, out _);
        }
    }

    public ClientSession? FindClient(string nick) =>
        _clients.TryGetValue(nick, out var s) ? s : null;

    public IEnumerable<string> ChannelNames => _channels.Keys;
    public IEnumerable<ClientSession> Clients => _clients.Values;
    public IEnumerable<IrcChannel> Channels => _channels.Values;

    // ── Server-wide bans ────────────────────────────────────────────────
    public IReadOnlyCollection<BanInfo> ServerBans => (IReadOnlyCollection<BanInfo>)_serverBans.Values;

    public bool IsServerBanned(string mask, out BanInfo? match)
    {
        foreach (var b in _serverBans.Values)
            if (Glob.IsMatch(b.Mask, mask)) { match = b; return true; }
        match = null;
        return false;
    }

    public void AddServerBan(string mask, string reason, string setBy)
    {
        _serverBans[mask] = new BanInfo { Scope = "*", Mask = mask, Reason = reason, SetBy = setBy, SetUtc = DateTime.UtcNow };
        // Kill any currently-connected users matching the mask
        foreach (var c in _clients.Values.ToList())
            if (Glob.IsMatch(mask, c.FullMask))
                c.KillByServer($"Banned: {reason}");
    }

    public bool RemoveServerBan(string mask) => _serverBans.TryRemove(mask, out _);

    // ── Stats snapshot ──────────────────────────────────────────────────
    public ServerStats Snapshot() => new()
    {
        Hostname = hostname,
        Port = port,
        AdminPort = adminPort,
        StartTimeUtc = StartTimeUtc,
        UptimeSeconds = (DateTime.UtcNow - StartTimeUtc).TotalSeconds,
        CurrentUsers = _clients.Count,
        PeakUsers = _peakUsers,
        TotalConnections = Interlocked.Read(ref _totalConnections),
        ChannelCount = _channels.Count,
        MessagesReceived = Interlocked.Read(ref _messagesReceived),
        MessagesSent = Interlocked.Read(ref _messagesSent),
        ServerBanCount = _serverBans.Count,
        ChannelBanCount = _channels.Values.Sum(c => c.Bans.Count)
    };
}

// Simple IRC-style glob matching (* and ?) for ban masks.
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> _cache = new();

    public static bool IsMatch(string pattern, string input)
    {
        var rx = _cache.GetOrAdd(pattern, p =>
            new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled));
        return rx.IsMatch(input);
    }
}

public class IrcChannel(string name)
{
    public string Name { get; } = name;
    public string Topic { get; private set; } = "";
    public string TopicSetBy { get; private set; } = "";

    private readonly ConcurrentDictionary<string, ClientSession> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _prefixes = new(StringComparer.OrdinalIgnoreCase); // nick -> "@"/"+"/""
    private readonly HashSet<char> _modes = new();
    private readonly ConcurrentDictionary<string, BanInfo> _bans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _modeLock = new();

    public IReadOnlyCollection<BanInfo> Bans => (IReadOnlyCollection<BanInfo>)_bans.Values;

    public string ModeString
    {
        get { lock (_modeLock) return _modes.Count == 0 ? "" : "+" + string.Concat(_modes.OrderBy(c => c)); }
    }

    public bool HasMode(char m) { lock (_modeLock) return _modes.Contains(m); }

    public void Add(ClientSession session)
    {
        if (session.Nick == null) return;
        bool firstMember = _members.IsEmpty;
        _members[session.Nick] = session;
        // First user into a fresh channel becomes operator
        _prefixes[session.Nick] = firstMember ? "@" : "";
    }

    public void Remove(ClientSession session, string reason)
    {
        if (session.Nick != null && _members.TryRemove(session.Nick, out _))
        {
            _prefixes.TryRemove(session.Nick, out _);
            Broadcast($":{session.FullMask} PART {Name} :{reason}", exclude: session);
        }
    }

    public void RenameMember(string oldNick, string newNick)
    {
        if (_members.TryRemove(oldNick, out var s))
            _members[newNick] = s;
        if (_prefixes.TryRemove(oldNick, out var p))
            _prefixes[newNick] = p;
    }

    public bool HasMember(string nick) => _members.ContainsKey(nick);
    public IEnumerable<string> MemberNicks => _members.Keys;
    public IEnumerable<ClientSession> Members => _members.Values;

    public string PrefixOf(string nick) => _prefixes.TryGetValue(nick, out var p) ? p : "";
    public bool IsOp(string nick) => PrefixOf(nick) == "@";
    public bool IsVoiced(string nick) => PrefixOf(nick) is "@" or "+";

    public string NamesList() =>
        string.Join(" ", _members.Keys.Select(n => PrefixOf(n) + n));

    public bool IsBanned(string mask, out BanInfo? match)
    {
        foreach (var b in _bans.Values)
            if (Glob.IsMatch(b.Mask, mask)) { match = b; return true; }
        match = null;
        return false;
    }

    public void AddBan(string mask, string reason, string setBy) =>
        _bans[mask] = new BanInfo { Scope = Name, Mask = mask, Reason = reason, SetBy = setBy, SetUtc = DateTime.UtcNow };

    public bool RemoveBan(string mask) => _bans.TryRemove(mask, out _);

    public void SetTopic(string topic, string setBy)
    {
        Topic = topic;
        TopicSetBy = setBy;
    }

    // Apply a mode change string; returns the applied changes for broadcast. paramNicks map o/v to a target.
    public string ApplyModes(char sign, char mode, string? param)
    {
        switch (mode)
        {
            case 'o' or 'v' when param != null && _members.ContainsKey(param):
                var pfx = mode == 'o' ? "@" : "+";
                if (sign == '+') _prefixes[param] = pfx;
                else if (_prefixes.TryGetValue(param, out var cur) && cur == pfx) _prefixes[param] = "";
                return $"{sign}{mode} {param}";
            case 'm' or 'n' or 't' or 'i' or 's' or 'p' or 'k' or 'l':
                lock (_modeLock) { if (sign == '+') _modes.Add(mode); else _modes.Remove(mode); }
                return $"{sign}{mode}";
            default:
                return "";
        }
    }

    public void Broadcast(string line, ClientSession? exclude = null)
    {
        foreach (var m in _members.Values)
            if (m != exclude)
                m.SendRaw(line);
    }

    public ChannelInfo ToInfo() => new()
    {
        Name = Name,
        Topic = Topic,
        Modes = ModeString,
        MemberCount = _members.Count,
        Members = _members.Keys.Select(n => new ChannelMember { Nick = n, Prefix = PrefixOf(n) }).ToList(),
        Bans = _bans.Values.ToList()
    };
}

public class ClientSession
{
    private readonly TcpClient _tcp;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly string _hostname;
    private readonly IrcServer _server;

    public string? Nick { get; private set; }
    public string User { get; private set; } = "";
    public string RealName { get; private set; } = "";
    public string Host { get; } = "localhost";
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    private string? _pendingNick;
    private bool _registered;
    private readonly HashSet<char> _modes = new();

    public string FullMask => $"{Nick}!{User}@{Host}";
    public string ModeString => _modes.Count == 0 ? "" : "+" + string.Concat(_modes.OrderBy(c => c));

    public ClientSession(TcpClient tcp, string hostname, IrcServer server)
    {
        _tcp = tcp;
        _hostname = hostname;
        _server = server;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
    }

    public void SendRaw(string line)
    {
        try { _writer.WriteLine(line); _server.CountSent(); }
        catch { /* client gone */ }
    }

    private void Send(string command, string args) =>
        SendRaw($":{_hostname} {command} {Nick ?? "*"} {args}");

    private void SendNumeric(int num, string args) => Send(num.ToString("D3"), args);

    // Admin-driven modes/actions
    public void ApplyUserMode(char sign, char mode)
    {
        if (sign == '+') _modes.Add(mode); else _modes.Remove(mode);
        SendRaw($":{_hostname} MODE {Nick} {sign}{mode}");
    }

    public void KillByServer(string reason)
    {
        _server.RemoveFromAllChannels(this, reason);
        SendRaw($"ERROR :Closing Link: {Nick} ({reason})");
        try { _tcp.Close(); } catch { }
    }

    public void ServerNotice(string text) => SendRaw($":{_hostname} NOTICE {Nick} :{text}");

    public UserInfo ToInfo()
    {
        var channels = _server.Channels.Where(c => Nick != null && c.HasMember(Nick))
                                       .Select(c => c.PrefixOf(Nick!) + c.Name).ToList();
        return new UserInfo
        {
            Nick = Nick ?? "",
            User = User,
            RealName = RealName,
            Host = Host,
            ConnectedUtc = ConnectedUtc,
            IdleSeconds = (DateTime.UtcNow - LastActivityUtc).TotalSeconds,
            Modes = ModeString,
            Channels = channels
        };
    }

    public async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null) break;
                HandleLine(line.Trim());
            }
        }
        catch { /* disconnected */ }
    }

    private void HandleLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        LastActivityUtc = DateTime.UtcNow;
        if (_registered) _server.CountReceived();

        if (raw[0] == ':')
        {
            int sp = raw.IndexOf(' ');
            if (sp < 0) return;
            raw = raw[(sp + 1)..];
        }

        string command;
        string rest = "";
        int space = raw.IndexOf(' ');
        if (space < 0) command = raw.ToUpperInvariant();
        else { command = raw[..space].ToUpperInvariant(); rest = raw[(space + 1)..].TrimStart(); }

        switch (command)
        {
            case "NICK":    HandleNick(rest); break;
            case "USER":    HandleUser(rest); break;
            case "JOIN":    HandleJoin(rest); break;
            case "PART":    HandlePart(rest); break;
            case "PRIVMSG": HandlePrivmsg(rest); break;
            case "NOTICE":  HandleNotice(rest); break;
            case "PING":    HandlePing(rest); break;
            case "PONG":    break;
            case "QUIT":    HandleQuit(rest); break;
            case "LIST":    HandleList(); break;
            case "NAMES":   HandleNames(rest); break;
            case "WHO":     HandleWho(rest); break;
            case "WHOIS":   HandleWhois(rest); break;
            case "MODE":    HandleMode(rest); break;
            case "TOPIC":   HandleTopic(rest); break;
            case "PASS":    break;
            case "CAP":     HandleCap(rest); break;
            default:
                if (_registered) SendNumeric(421, $"{command} :Unknown command");
                break;
        }
    }

    private void HandleNick(string rest)
    {
        var nick = rest.Split(' ')[0].TrimStart(':');
        if (string.IsNullOrWhiteSpace(nick)) { SendNumeric(431, ":No nickname given"); return; }

        if (!_registered)
        {
            _pendingNick = nick;
            TryCompleteRegistration();
            return;
        }

        if (_server.FindClient(nick) != null) { SendNumeric(433, $"{nick} :Nickname is already in use"); return; }

        var old = Nick!;
        _server.RenameClient(this, old, nick);
        Nick = nick;
        SendRaw($":{old}!{User}@{Host} NICK :{nick}");
    }

    private void HandleUser(string rest)
    {
        if (_registered) { SendNumeric(462, ":Already registered"); return; }
        var parts = rest.Split(' ', 4);
        User = parts.Length > 0 ? parts[0] : "user";
        RealName = parts.Length > 3 ? parts[3].TrimStart(':') : User;
        TryCompleteRegistration();
    }

    private void TryCompleteRegistration()
    {
        if (_pendingNick == null || string.IsNullOrEmpty(User)) return;

        // Enforce server-wide bans at registration
        var mask = $"{_pendingNick}!{User}@{Host}";
        if (_server.IsServerBanned(mask, out var ban))
        {
            SendRaw($"ERROR :Closing Link: You are banned from this server ({ban!.Reason})");
            try { _tcp.Close(); } catch { }
            return;
        }

        if (!_server.RegisterClient(this, _pendingNick))
        {
            SendNumeric(433, $"{_pendingNick} :Nickname is already in use");
            _pendingNick = null;
            return;
        }

        Nick = _pendingNick;
        _registered = true;

        SendNumeric(001, $":Welcome to the local IRC server, {Nick}");
        SendNumeric(002, $":Your host is {_hostname}, running IRCServer/1.0");
        SendNumeric(003, $":This server was created {_server.StartTimeUtc:u}");
        SendNumeric(004, $"{_hostname} IRCServer-1.0 io mntispkl");
        SendNumeric(375, $":- {_hostname} Message of the day -");
        SendNumeric(372, ":- Local test IRC server. Have fun!");
        SendNumeric(376, ":End of /MOTD command.");
    }

    private void HandleJoin(string rest)
    {
        if (!_registered) return;
        var channels = rest.Split(' ')[0].Split(',');
        foreach (var ch in channels)
        {
            var name = ch.TrimStart(':');
            if (!name.StartsWith('#') && !name.StartsWith('&')) name = "#" + name;

            var channel = _server.GetOrCreateChannel(name);

            // Enforce +b bans (unless already a member)
            if (!channel.HasMember(Nick!) && channel.IsBanned(FullMask, out var b))
            {
                SendNumeric(474, $"{name} :Cannot join channel (+b) — {b!.Reason}");
                continue;
            }
            // Enforce +i invite-only (no INVITE support, so just block newcomers)
            if (!channel.HasMember(Nick!) && channel.HasMode('i'))
            {
                SendNumeric(473, $"{name} :Cannot join channel (+i)");
                continue;
            }

            channel.Add(this);
            channel.Broadcast($":{FullMask} JOIN :{name}");
            SendRaw($":{FullMask} JOIN :{name}");

            if (!string.IsNullOrEmpty(channel.Topic))
                SendNumeric(332, $"{name} :{channel.Topic}");
            SendNumeric(353, $"= {name} :{channel.NamesList()}");
            SendNumeric(366, $"{name} :End of /NAMES list");
        }
    }

    private void HandlePart(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(new[] { ' ' }, 2);
        var channels = parts[0].Split(',');
        var reason = parts.Length > 1 ? parts[1].TrimStart(':') : "Leaving";

        foreach (var ch in channels)
        {
            if (_server.TryGetChannel(ch, out var channel) && channel.HasMember(Nick!))
            {
                SendRaw($":{FullMask} PART {ch} :{reason}");
                channel.Remove(this, reason);
            }
        }
    }

    private void HandlePrivmsg(string rest) => RelayMessage(rest, "PRIVMSG");
    private void HandleNotice(string rest) => RelayMessage(rest, "NOTICE");

    private void RelayMessage(string rest, string verb)
    {
        if (!_registered) return;
        var parts = rest.Split(new[] { ' ' }, 2);
        if (parts.Length < 2) return;
        var target = parts[0];
        var text = parts[1].TrimStart(':');

        if (target.StartsWith('#') || target.StartsWith('&'))
        {
            if (!_server.TryGetChannel(target, out var ch))
            {
                if (verb == "PRIVMSG") SendNumeric(401, $"{target} :No such channel");
                return;
            }
            // +n: must be a member to message the channel
            if (ch.HasMode('n') && !ch.HasMember(Nick!))
            {
                if (verb == "PRIVMSG") SendNumeric(404, $"{target} :Cannot send to channel (+n)");
                return;
            }
            // +m: moderated — only ops/voiced may talk
            if (ch.HasMode('m') && !ch.IsVoiced(Nick!))
            {
                if (verb == "PRIVMSG") SendNumeric(404, $"{target} :Cannot send to channel (+m)");
                return;
            }
            ch.Broadcast($":{FullMask} {verb} {target} :{text}", exclude: this);
        }
        else
        {
            var dest = _server.FindClient(target);
            if (dest == null) { if (verb == "PRIVMSG") SendNumeric(401, $"{target} :No such nick"); return; }
            dest.SendRaw($":{FullMask} {verb} {target} :{text}");
        }
    }

    private void HandlePing(string rest) =>
        SendRaw($":{_hostname} PONG {_hostname} :{rest.TrimStart(':')}");

    private void HandleQuit(string rest)
    {
        var reason = rest.TrimStart(':');
        if (string.IsNullOrWhiteSpace(reason)) reason = "Quit";
        _server.RemoveFromAllChannels(this, reason);
        SendRaw($"ERROR :Closing Link: {Nick} ({reason})");
        try { _tcp.Close(); } catch { }
    }

    private void HandleList()
    {
        if (!_registered) return;
        SendNumeric(321, "Channel :Users  Name");
        foreach (var ch in _server.Channels)
            SendNumeric(322, $"{ch.Name} {ch.MemberNicks.Count()} :{ch.Topic}");
        SendNumeric(323, ":End of /LIST");
    }

    private void HandleNames(string rest)
    {
        if (!_registered) return;
        var name = rest.Split(' ')[0].TrimStart(':');
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_server.TryGetChannel(name, out var ch))
            SendNumeric(353, $"= {name} :{ch.NamesList()}");
        SendNumeric(366, $"{name} :End of /NAMES list");
    }

    private void HandleWho(string rest)
    {
        if (!_registered) return;
        var target = rest.Split(' ')[0].TrimStart(':');
        if (_server.TryGetChannel(target, out var ch))
            foreach (var m in ch.Members)
                SendNumeric(352, $"{target} {m.User} {m.Host} {_hostname} {m.Nick} H{ch.PrefixOf(m.Nick!)} :0 {m.RealName}");
        SendNumeric(315, $"{target} :End of /WHO list");
    }

    private void HandleWhois(string rest)
    {
        if (!_registered) return;
        var target = rest.Split(' ')[0].TrimStart(':');
        var who = _server.FindClient(target);
        if (who == null) { SendNumeric(401, $"{target} :No such nick"); return; }
        SendNumeric(311, $"{who.Nick} {who.User} {who.Host} * :{who.RealName}");
        var chans = _server.Channels.Where(c => c.HasMember(who.Nick!)).Select(c => c.PrefixOf(who.Nick!) + c.Name);
        if (chans.Any()) SendNumeric(319, $"{who.Nick} :{string.Join(" ", chans)}");
        if (!string.IsNullOrEmpty(who.ModeString)) SendNumeric(379, $"{who.Nick} :is using modes {who.ModeString}");
        SendNumeric(317, $"{who.Nick} {(int)(DateTime.UtcNow - who.LastActivityUtc).TotalSeconds} :seconds idle");
        SendNumeric(318, $"{who.Nick} :End of /WHOIS list");
    }

    private void HandleMode(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(' ');
        var target = parts[0];

        if (target.StartsWith('#') || target.StartsWith('&'))
        {
            if (!_server.TryGetChannel(target, out var ch)) { SendNumeric(403, $"{target} :No such channel"); return; }
            if (parts.Length == 1) { SendNumeric(324, $"{target} {(string.IsNullOrEmpty(ch.ModeString) ? "+" : ch.ModeString)}"); return; }

            if (!ch.IsOp(Nick!)) { SendNumeric(482, $"{target} :You're not channel operator"); return; }

            var applied = ApplyChannelModeString(ch, parts.Skip(1).ToArray());
            if (applied.Length > 0)
                ch.Broadcast($":{FullMask} MODE {target} {applied}");
        }
        else
        {
            if (target == Nick)
            {
                if (parts.Length == 1) { SendNumeric(221, string.IsNullOrEmpty(ModeString) ? "+" : ModeString); return; }
                ApplySelfModeString(parts[1]);
            }
            else SendNumeric(502, ":Cannot change mode for other users");
        }
    }

    private void ApplySelfModeString(string modes)
    {
        char sign = '+';
        foreach (var c in modes)
        {
            if (c is '+' or '-') { sign = c; continue; }
            if (sign == '+') _modes.Add(c); else _modes.Remove(c);
        }
        SendRaw($":{FullMask} MODE {Nick} {modes}");
    }

    // Parse "+ov Alice Bob" style; returns normalized applied string
    private string ApplyChannelModeString(IrcChannel ch, string[] tokens)
    {
        var flags = tokens[0];
        var args = tokens.Skip(1).ToArray();
        int argIdx = 0;
        char sign = '+';
        var sb = new StringBuilder();
        foreach (var c in flags)
        {
            if (c is '+' or '-') { sign = c; continue; }
            string? param = (c is 'o' or 'v' or 'k' or 'l') && argIdx < args.Length ? args[argIdx++] : null;
            var res = ch.ApplyModes(sign, c, param);
            if (res.Length > 0) sb.Append(sb.Length > 0 ? " " + res : res);
        }
        return sb.ToString();
    }

    private void HandleTopic(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(new[] { ' ' }, 2);
        var chName = parts[0];
        if (!_server.TryGetChannel(chName, out var ch)) { SendNumeric(403, $"{chName} :No such channel"); return; }

        if (parts.Length == 1)
        {
            if (string.IsNullOrEmpty(ch.Topic)) SendNumeric(331, $"{chName} :No topic is set");
            else SendNumeric(332, $"{chName} :{ch.Topic}");
            return;
        }
        // +t: only ops may set topic
        if (ch.HasMode('t') && !ch.IsOp(Nick!)) { SendNumeric(482, $"{chName} :You're not channel operator"); return; }

        var topic = parts[1].TrimStart(':');
        ch.SetTopic(topic, Nick!);
        ch.Broadcast($":{FullMask} TOPIC {chName} :{topic}");
    }

    private void HandleCap(string rest)
    {
        var parts = rest.Split(' ');
        var subcommand = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        if (subcommand == "LS") SendRaw($":{_hostname} CAP * LS :");
        else if (subcommand == "REQ") SendRaw($":{_hostname} CAP * NAK :{(parts.Length > 1 ? parts[1].TrimStart(':') : "")}");
    }
}
