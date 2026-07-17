using System.Text.Json;

namespace IRCServer.Shared;

// Line-delimited JSON protocol spoken on the server's admin control port.
// Each request is one JSON line; each response is one JSON line.

public static class AdminCommands
{
    public const string Auth      = "AUTH";      // args: pass
    public const string Stats     = "STATS";     // -> Stats
    public const string Users     = "USERS";     // -> Users
    public const string Channels  = "CHANNELS";  // -> Channels
    public const string Bans      = "BANS";      // -> Bans
    public const string Kill      = "KILL";      // args: nick, reason
    public const string Kick      = "KICK";      // args: channel, nick, reason
    public const string Ban       = "BAN";       // args: scope, mask, reason
    public const string Unban     = "UNBAN";     // args: scope, mask
    public const string UserMode  = "UMODE";     // args: nick, modes   (e.g. "+i")
    public const string ChanMode  = "CMODE";     // args: channel, modes (e.g. "+o Alice", "+mt")
    public const string Topic     = "TOPIC";     // args: channel, topic
    public const string Broadcast = "BROADCAST"; // args: text
}

public sealed class AdminRequest
{
    public string Cmd { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();

    public string Arg(string key) => Args.TryGetValue(key, out var v) ? v : "";
}

public sealed class AdminResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }

    public ServerStats? Stats { get; set; }
    public List<UserInfo>? Users { get; set; }
    public List<ChannelInfo>? Channels { get; set; }
    public List<BanInfo>? Bans { get; set; }
}

public sealed class ServerStats
{
    public string Hostname { get; set; } = "";
    public int Port { get; set; }
    public int AdminPort { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public double UptimeSeconds { get; set; }
    public int CurrentUsers { get; set; }
    public int PeakUsers { get; set; }
    public long TotalConnections { get; set; }
    public int ChannelCount { get; set; }
    public long MessagesReceived { get; set; }
    public long MessagesSent { get; set; }
    public int ServerBanCount { get; set; }
    public int ChannelBanCount { get; set; }
    public string Version { get; set; } = "IRCServer/1.0";
}

public sealed class UserInfo
{
    public string Nick { get; set; } = "";
    public string User { get; set; } = "";
    public string RealName { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime ConnectedUtc { get; set; }
    public double IdleSeconds { get; set; }
    public string Modes { get; set; } = "";
    public List<string> Channels { get; set; } = new();
}

public sealed class ChannelMember
{
    public string Nick { get; set; } = "";
    public string Prefix { get; set; } = ""; // "@" op, "+" voice, "" none
}

public sealed class ChannelInfo
{
    public string Name { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Modes { get; set; } = "";
    public int MemberCount { get; set; }
    public List<ChannelMember> Members { get; set; } = new();
    public List<BanInfo> Bans { get; set; } = new();
}

public sealed class BanInfo
{
    public string Scope { get; set; } = "";  // "*" = server-wide, otherwise "#channel"
    public string Mask { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SetBy { get; set; } = "";
    public DateTime SetUtc { get; set; }
}

public static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
