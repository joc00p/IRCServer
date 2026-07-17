using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IRCServer;

public class IrcServer(string hostname, int port)
{
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IrcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"IRC server listening on {hostname}:{port}");

        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(tcp);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
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

    // Called by a session once registration is complete (NICK + USER received)
    public bool RegisterClient(ClientSession session, string nick)
    {
        if (_clients.ContainsKey(nick))
            return false;
        _clients[nick] = session;
        Console.WriteLine($"[+] {nick} registered");
        return true;
    }

    public void RenameClient(ClientSession session, string oldNick, string newNick)
    {
        _clients.TryRemove(oldNick, out _);
        _clients[newNick] = session;
    }

    public IrcChannel GetOrCreateChannel(string name) =>
        _channels.GetOrAdd(name, n => new IrcChannel(n));

    public void RemoveFromAllChannels(ClientSession session, string reason)
    {
        foreach (var ch in _channels.Values)
            ch.Remove(session, reason);
    }

    public ClientSession? FindClient(string nick) =>
        _clients.TryGetValue(nick, out var s) ? s : null;

    public IEnumerable<string> ChannelNames => _channels.Keys;
}

public class IrcChannel(string name)
{
    public string Name { get; } = name;
    private readonly ConcurrentDictionary<string, ClientSession> _members = new(StringComparer.OrdinalIgnoreCase);

    public void Add(ClientSession session)
    {
        if (session.Nick != null)
            _members[session.Nick] = session;
    }

    public void Remove(ClientSession session, string reason)
    {
        if (session.Nick != null && _members.TryRemove(session.Nick, out _))
            Broadcast($":{session.FullMask} PART {Name} :{reason}", exclude: session);
    }

    public bool HasMember(string nick) => _members.ContainsKey(nick);

    public IEnumerable<string> MemberNicks => _members.Keys;

    public void Broadcast(string line, ClientSession? exclude = null)
    {
        foreach (var m in _members.Values)
            if (m != exclude)
                m.SendRaw(line);
    }
}

public class ClientSession
{
    private readonly TcpClient _tcp;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly string _hostname;
    private readonly IrcServer _server;

    public string? Nick { get; private set; }
    private string? _user;
    private string? _realname;
    private string? _pendingNick; // NICK before USER
    private bool _registered;

    public string FullMask => $"{Nick}!{_user}@localhost";

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
        try { _writer.WriteLine(line); }
        catch { /* client gone */ }
    }

    private void Send(string command, string args) =>
        SendRaw($":{_hostname} {command} {Nick ?? "*"} {args}");

    private void SendNumeric(int num, string args) =>
        Send(num.ToString("D3"), args);

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

        // Strip prefix if client sends one (unusual but valid)
        if (raw[0] == ':')
        {
            int sp = raw.IndexOf(' ');
            if (sp < 0) return;
            raw = raw[(sp + 1)..];
        }

        // Split command from params
        string command;
        string rest = "";
        int space = raw.IndexOf(' ');
        if (space < 0)
        {
            command = raw.ToUpperInvariant();
        }
        else
        {
            command = raw[..space].ToUpperInvariant();
            rest = raw[(space + 1)..].TrimStart();
        }

        switch (command)
        {
            case "NICK":   HandleNick(rest); break;
            case "USER":   HandleUser(rest); break;
            case "JOIN":   HandleJoin(rest); break;
            case "PART":   HandlePart(rest); break;
            case "PRIVMSG": HandlePrivmsg(rest); break;
            case "NOTICE": HandleNotice(rest); break;
            case "PING":   HandlePing(rest); break;
            case "PONG":   break; // ignore
            case "QUIT":   HandleQuit(rest); break;
            case "LIST":   HandleList(); break;
            case "NAMES":  HandleNames(rest); break;
            case "WHO":    HandleWho(rest); break;
            case "WHOIS":  HandleWhois(rest); break;
            case "MODE":   HandleMode(rest); break;
            case "TOPIC":  HandleTopic(rest); break;
            case "PASS":   break; // no auth needed for local test server
            case "CAP":    HandleCap(rest); break;
            default:
                if (_registered)
                    SendNumeric(421, $"{command} :Unknown command");
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

        // Nick change while registered
        if (_server.FindClient(nick) != null)
        {
            SendNumeric(433, $"{nick} :Nickname is already in use");
            return;
        }

        var old = Nick!;
        _server.RenameClient(this, old, nick);
        Nick = nick;
        // Broadcast to all channels we're in
        SendRaw($":{old}!{_user}@localhost NICK :{nick}");
    }

    private void HandleUser(string rest)
    {
        if (_registered) { SendNumeric(462, ":Already registered"); return; }
        // USER <username> <mode> <unused> :<realname>
        var parts = rest.Split(' ', 4);
        _user = parts.Length > 0 ? parts[0] : "user";
        _realname = parts.Length > 3 ? parts[3].TrimStart(':') : _user;
        TryCompleteRegistration();
    }

    private void TryCompleteRegistration()
    {
        if (_pendingNick == null || _user == null) return;

        if (!_server.RegisterClient(this, _pendingNick))
        {
            SendNumeric(433, $"{_pendingNick} :Nickname is already in use");
            _pendingNick = null;
            return;
        }

        Nick = _pendingNick;
        _registered = true;

        // RPL_WELCOME, RPL_YOURHOST, RPL_CREATED, RPL_MYINFO
        SendNumeric(001, $":Welcome to the local IRC server, {Nick}");
        SendNumeric(002, $":Your host is {_hostname}, running IRCServer/1.0");
        SendNumeric(003, ":This server was created just now");
        SendNumeric(004, $"{_hostname} IRCServer-1.0 o o");
        // RPL_MOTDSTART / RPL_MOTD / RPL_ENDOFMOTD
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
            if (!name.StartsWith('#') && !name.StartsWith('&'))
                name = "#" + name;

            var channel = _server.GetOrCreateChannel(name);
            channel.Add(this);

            // JOIN broadcast to everyone in channel (including joiner)
            channel.Broadcast($":{FullMask} JOIN :{name}");
            SendRaw($":{FullMask} JOIN :{name}"); // also send to self (broadcast excludes self)

            // RPL_NAMREPLY
            var nicks = string.Join(" ", channel.MemberNicks);
            SendNumeric(353, $"= {name} :{nicks}");
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
            var channel = _server.GetOrCreateChannel(ch);
            if (channel.HasMember(Nick!))
            {
                SendRaw($":{FullMask} PART {ch} :{reason}");
                channel.Remove(this, reason);
            }
        }
    }

    private void HandlePrivmsg(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(new[] { ' ' }, 2);
        if (parts.Length < 2) return;
        var target = parts[0];
        var text = parts[1].TrimStart(':');

        if (target.StartsWith('#') || target.StartsWith('&'))
        {
            var ch = _server.GetOrCreateChannel(target);
            ch.Broadcast($":{FullMask} PRIVMSG {target} :{text}", exclude: this);
        }
        else
        {
            var dest = _server.FindClient(target);
            if (dest == null) { SendNumeric(401, $"{target} :No such nick"); return; }
            dest.SendRaw($":{FullMask} PRIVMSG {target} :{text}");
        }
    }

    private void HandleNotice(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(new[] { ' ' }, 2);
        if (parts.Length < 2) return;
        var target = parts[0];
        var text = parts[1].TrimStart(':');

        if (target.StartsWith('#') || target.StartsWith('&'))
            _server.GetOrCreateChannel(target).Broadcast($":{FullMask} NOTICE {target} :{text}", exclude: this);
        else
            _server.FindClient(target)?.SendRaw($":{FullMask} NOTICE {target} :{text}");
    }

    private void HandlePing(string rest)
    {
        SendRaw($":{_hostname} PONG {_hostname} :{rest.TrimStart(':')}");
    }

    private void HandleQuit(string rest)
    {
        var reason = rest.TrimStart(':');
        if (string.IsNullOrWhiteSpace(reason)) reason = "Quit";
        _server.RemoveFromAllChannels(this, reason);
        SendRaw($"ERROR :Closing Link: {Nick} ({reason})");
        _tcp.Close();
    }

    private void HandleList()
    {
        if (!_registered) return;
        SendNumeric(321, "Channel :Users  Name");
        foreach (var name in _server.ChannelNames)
        {
            var ch = _server.GetOrCreateChannel(name);
            var count = ch.MemberNicks.Count();
            SendNumeric(322, $"{name} {count} :");
        }
        SendNumeric(323, ":End of /LIST");
    }

    private void HandleNames(string rest)
    {
        if (!_registered) return;
        var name = rest.Split(' ')[0].TrimStart(':');
        if (string.IsNullOrWhiteSpace(name)) return;
        var ch = _server.GetOrCreateChannel(name);
        SendNumeric(353, $"= {name} :{string.Join(" ", ch.MemberNicks)}");
        SendNumeric(366, $"{name} :End of /NAMES list");
    }

    private void HandleWho(string rest)
    {
        if (!_registered) return;
        // Minimal WHO response
        var target = rest.Split(' ')[0].TrimStart(':');
        SendNumeric(315, $"{target} :End of /WHO list");
    }

    private void HandleWhois(string rest)
    {
        if (!_registered) return;
        var target = rest.Split(' ')[0].TrimStart(':');
        var who = _server.FindClient(target);
        if (who == null) { SendNumeric(401, $"{target} :No such nick"); return; }
        SendNumeric(311, $"{who.Nick} {who._user} localhost * :{who._realname}");
        SendNumeric(318, $"{who.Nick} :End of /WHOIS list");
    }

    private void HandleMode(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(' ');
        var target = parts[0];
        // Just acknowledge — no real mode enforcement needed for a test server
        if (target == Nick)
            SendNumeric(221, "+i"); // user mode
        else
            SendNumeric(324, $"{target} +"); // channel mode
    }

    private void HandleTopic(string rest)
    {
        if (!_registered) return;
        var parts = rest.Split(new[] { ' ' }, 2);
        var chName = parts[0];
        if (parts.Length == 1)
        {
            SendNumeric(331, $"{chName} :No topic is set");
            return;
        }
        var topic = parts[1].TrimStart(':');
        _server.GetOrCreateChannel(chName).Broadcast($":{FullMask} TOPIC {chName} :{topic}");
    }

    private void HandleCap(string rest)
    {
        // CAP negotiation — send NAK to everything so the client falls back to plain IRC
        var parts = rest.Split(' ');
        var subcommand = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        if (subcommand == "LS")
            SendRaw($":{_hostname} CAP * LS :");
        else if (subcommand == "REQ")
            SendRaw($":{_hostname} CAP * NAK :{(parts.Length > 1 ? parts[1].TrimStart(':') : "")}");
        else if (subcommand == "END")
            { /* nothing */ }
    }
}
