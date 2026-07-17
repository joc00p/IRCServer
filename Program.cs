using IRCServer;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 6667;
var server = new IrcServer("localhost", port);
await server.RunAsync();
