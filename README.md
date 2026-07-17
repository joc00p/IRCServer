# IRCServer

A minimal, local-only IRC server (RFC 1459 / RFC 2812) plus a WinForms
administration application. Binds to `127.0.0.1` only — never reachable from
the public internet. Intended for testing IRC clients and experimenting with
server administration.

## Projects

| Project | Output | Description |
|---------|--------|-------------|
| `Shared/` | library | `IRCServer.Shared` — DTOs and JSON protocol for the admin control port |
| `Server/` | console exe | `IRCServer` — the IRC server + loopback admin control port |
| `Admin/`  | WinForms exe | `IRCServerAdmin` — GUI admin console |

## Build

```
dotnet build IRCServer.slnx -c Release
```

## Run the server

```
dotnet run --project Server -- [ircPort] [adminPort] [adminPassword]
```

- `ircPort` — IRC listen port (default `6667`)
- `adminPort` — loopback-only admin control port (default `6668`)
- `adminPassword` — optional; if set, the admin app must authenticate

Point any IRC client at `localhost:6667`.

### Supported IRC commands

`NICK` `USER` `PASS` `JOIN` `PART` `PRIVMSG` `NOTICE` `PING`/`PONG` `QUIT`
`LIST` `NAMES` `WHO` `WHOIS` `MODE` `TOPIC` `CAP`

- The first user into a fresh channel is granted operator (`@`).
- Channel modes enforced: `+t` (ops-only topic), `+n` (no external messages),
  `+m` (moderated — only `@`/`+` may talk), `+i` (invite-only blocks new joins),
  `+b` (ban masks, matched on join).
- Server-wide bans block matching masks at registration and disconnect any
  matching connected user.

## Run the admin application

```
dotnet run --project Admin
```

The admin app can **launch the server for you**: set the IRC port (and
optionally an admin password), click **Launch Server**, and it starts the
server process and auto-connects to its control port. **Stop Server** shuts it
down; the process is also stopped when the admin app closes. If the server
binary can't be found automatically, you'll be prompted to locate
`IRCServer.exe`.

Alternatively, connect to an already-running server's admin port (default
`127.0.0.1:6668`). The app provides:

- **Server Stats** — uptime, current/peak users, total connections, channel
  count, messages sent/received, ban counts.
- **Users** — every connected user with modes, idle time, and channels. Kill a
  user or set user modes.
- **Channels** — channels with modes, member list (and prefixes), and per-channel
  bans. Set channel modes, set topic, kick a member, add/remove channel bans.
- **Bans** — all server-wide and channel bans. Add/remove bans.
- **Broadcast** — send a server notice to every connected user.

Auto-refreshes every 3 seconds (toggle in the toolbar).

## Admin control protocol

Line-delimited JSON on the admin port. Request:

```json
{"cmd":"CMODE","args":{"channel":"#test","modes":"+o Alice"}}
```

Response:

```json
{"ok":true,"message":"Set channel modes on #test: +o Alice"}
```

Commands: `AUTH` `STATS` `USERS` `CHANNELS` `BANS` `KILL` `KICK` `BAN` `UNBAN`
`UMODE` `CMODE` `TOPIC` `BROADCAST`. See `Shared/AdminProtocol.cs`.
