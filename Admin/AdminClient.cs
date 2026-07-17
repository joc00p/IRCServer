using System.Net.Sockets;
using System.Text;
using IRCServer.Shared;

namespace IRCServer.Admin;

// Thin client for the server's line-delimited JSON admin control port.
public sealed class AdminClient : IDisposable
{
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsConnected => _tcp?.Connected ?? false;

    public async Task ConnectAsync(string host, int port, string? password)
    {
        Dispose();
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);
        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        if (!string.IsNullOrEmpty(password))
        {
            var resp = await SendAsync(new AdminRequest { Cmd = AdminCommands.Auth, Args = { ["pass"] = password } });
            if (!resp.Ok) throw new InvalidOperationException(resp.Error ?? "Authentication failed");
        }
    }

    public async Task<AdminResponse> SendAsync(AdminRequest req)
    {
        if (_writer == null || _reader == null) throw new InvalidOperationException("Not connected");
        await _lock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(AdminJson.Serialize(req));
            var line = await _reader.ReadLineAsync();
            if (line == null) throw new IOException("Connection closed by server");
            return AdminJson.Deserialize<AdminResponse>(line) ?? new AdminResponse { Ok = false, Error = "Empty response" };
        }
        finally { _lock.Release(); }
    }

    public Task<AdminResponse> SimpleAsync(string cmd) => SendAsync(new AdminRequest { Cmd = cmd });

    public Task<AdminResponse> ActionAsync(string cmd, params (string, string)[] args)
    {
        var req = new AdminRequest { Cmd = cmd };
        foreach (var (k, v) in args) req.Args[k] = v;
        return SendAsync(req);
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _tcp = null; _reader = null; _writer = null;
    }
}
