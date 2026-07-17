using IRCServer;

// Usage: IRCServer [ircPort] [adminPort] [adminPassword]
//   ircPort       default 6667
//   adminPort     default 6668  (loopback-only control port for the admin app)
//   adminPassword optional; if omitted the admin port requires no auth
int ircPort   = args.Length > 0 && int.TryParse(args[0], out var p1) ? p1 : 6667;
int adminPort = args.Length > 1 && int.TryParse(args[1], out var p2) ? p2 : 6668;
string? adminPass = args.Length > 2 ? args[2] : null;

var server = new IrcServer("localhost", ircPort, adminPort, adminPass);
await server.RunAsync();
