using System.Net;
using System.Net.Sockets;
using System.Text;
using IRCServer.Shared;

namespace IRCServer;

// Loopback-only JSON control port. The WinForms admin app connects here to
// read stats and drive moderation (kill/kick/ban/mode/topic/broadcast).
public class AdminInterface(IrcServer server, int port)
{
    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            var tcp = await listener.AcceptTcpClientAsync();
            _ = HandleAsync(tcp);
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        using var _ = tcp;
        var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        bool authed = server.AdminPass == null;
        Console.WriteLine("[admin] control connection opened");
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                AdminResponse resp;
                try
                {
                    var req = AdminJson.Deserialize<AdminRequest>(line) ?? new AdminRequest();
                    resp = Dispatch(req, ref authed);
                }
                catch (Exception ex)
                {
                    resp = new AdminResponse { Ok = false, Error = ex.Message };
                }
                await writer.WriteLineAsync(AdminJson.Serialize(resp));
            }
        }
        catch { /* admin disconnected */ }
        finally { Console.WriteLine("[admin] control connection closed"); }
    }

    private AdminResponse Dispatch(AdminRequest req, ref bool authed)
    {
        var cmd = req.Cmd.ToUpperInvariant();

        if (cmd == AdminCommands.Auth)
        {
            authed = server.AdminPass != null && req.Arg("pass") == server.AdminPass;
            return authed
                ? new AdminResponse { Ok = true, Message = "Authenticated" }
                : new AdminResponse { Ok = false, Error = "Invalid password" };
        }

        if (!authed)
            return new AdminResponse { Ok = false, Error = "Authentication required" };

        switch (cmd)
        {
            case AdminCommands.Stats:
                return new AdminResponse { Ok = true, Stats = server.Snapshot() };

            case AdminCommands.Users:
                return new AdminResponse { Ok = true, Users = server.Clients.Where(c => c.Nick != null).Select(c => c.ToInfo()).ToList() };

            case AdminCommands.Channels:
                return new AdminResponse { Ok = true, Channels = server.Channels.Select(c => c.ToInfo()).ToList() };

            case AdminCommands.Bans:
            {
                var bans = server.ServerBans.ToList();
                foreach (var ch in server.Channels) bans.AddRange(ch.Bans);
                return new AdminResponse { Ok = true, Bans = bans };
            }

            case AdminCommands.Kill:
            {
                var nick = req.Arg("nick");
                var target = server.FindClient(nick);
                if (target == null) return Fail($"No such user: {nick}");
                target.KillByServer(req.Arg("reason") is { Length: > 0 } r ? r : "Killed by admin");
                return Ok($"Killed {nick}");
            }

            case AdminCommands.Kick:
            {
                var chName = req.Arg("channel");
                var nick = req.Arg("nick");
                if (!server.TryGetChannel(chName, out var ch)) return Fail($"No such channel: {chName}");
                var target = server.FindClient(nick);
                if (target == null || !ch.HasMember(nick)) return Fail($"{nick} is not in {chName}");
                var reason = req.Arg("reason") is { Length: > 0 } r ? r : "Kicked by admin";
                ch.Broadcast($":{server.Hostname} KICK {chName} {nick} :{reason}");
                target.SendRaw($":{server.Hostname} KICK {chName} {nick} :{reason}");
                ch.Remove(target, $"Kicked: {reason}");
                return Ok($"Kicked {nick} from {chName}");
            }

            case AdminCommands.Ban:
            {
                var scope = req.Arg("scope");
                var mask = req.Arg("mask");
                var reason = req.Arg("reason") is { Length: > 0 } r ? r : "Banned by admin";
                if (string.IsNullOrWhiteSpace(mask)) return Fail("Ban mask required");
                if (scope == "*")
                {
                    server.AddServerBan(mask, reason, "admin");
                    return Ok($"Server ban added: {mask}");
                }
                if (!server.TryGetChannel(scope, out var ch)) return Fail($"No such channel: {scope}");
                ch.AddBan(mask, reason, "admin");
                ch.Broadcast($":{server.Hostname} MODE {scope} +b {mask}");
                return Ok($"Ban added on {scope}: {mask}");
            }

            case AdminCommands.Unban:
            {
                var scope = req.Arg("scope");
                var mask = req.Arg("mask");
                if (scope == "*")
                    return server.RemoveServerBan(mask) ? Ok($"Server ban removed: {mask}") : Fail("No such ban");
                if (!server.TryGetChannel(scope, out var ch)) return Fail($"No such channel: {scope}");
                if (!ch.RemoveBan(mask)) return Fail("No such ban");
                ch.Broadcast($":{server.Hostname} MODE {scope} -b {mask}");
                return Ok($"Ban removed on {scope}: {mask}");
            }

            case AdminCommands.UserMode:
            {
                var nick = req.Arg("nick");
                var modes = req.Arg("modes");
                var target = server.FindClient(nick);
                if (target == null) return Fail($"No such user: {nick}");
                char sign = '+';
                foreach (var c in modes)
                {
                    if (c is '+' or '-') { sign = c; continue; }
                    target.ApplyUserMode(sign, c);
                }
                return Ok($"Set user modes on {nick}: {modes}");
            }

            case AdminCommands.ChanMode:
            {
                var chName = req.Arg("channel");
                var modes = req.Arg("modes");
                if (!server.TryGetChannel(chName, out var ch)) return Fail($"No such channel: {chName}");
                var tokens = modes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) return Fail("No modes given");
                var flags = tokens[0];
                var args = tokens.Skip(1).ToArray();
                int argIdx = 0;
                char sign = '+';
                var applied = new StringBuilder();
                foreach (var c in flags)
                {
                    if (c is '+' or '-') { sign = c; continue; }
                    string? param = (c is 'o' or 'v' or 'k' or 'l') && argIdx < args.Length ? args[argIdx++] : null;
                    var res = ch.ApplyModes(sign, c, param);
                    if (res.Length > 0) applied.Append(applied.Length > 0 ? " " + res : res);
                }
                if (applied.Length == 0) return Fail("No applicable modes");
                ch.Broadcast($":{server.Hostname} MODE {chName} {applied}");
                return Ok($"Set channel modes on {chName}: {applied}");
            }

            case AdminCommands.Topic:
            {
                var chName = req.Arg("channel");
                var topic = req.Arg("topic");
                if (!server.TryGetChannel(chName, out var ch)) return Fail($"No such channel: {chName}");
                ch.SetTopic(topic, "admin");
                ch.Broadcast($":{server.Hostname} TOPIC {chName} :{topic}");
                return Ok($"Topic set on {chName}");
            }

            case AdminCommands.Broadcast:
            {
                var text = req.Arg("text");
                int n = 0;
                foreach (var c in server.Clients) { c.ServerNotice($"[SERVER] {text}"); n++; }
                return Ok($"Broadcast sent to {n} user(s)");
            }

            default:
                return Fail($"Unknown command: {req.Cmd}");
        }
    }

    private static AdminResponse Ok(string msg) => new() { Ok = true, Message = msg };
    private static AdminResponse Fail(string err) => new() { Ok = false, Error = err };
}
