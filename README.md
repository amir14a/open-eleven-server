# OpenEleven

Reverse-engineered online server for Pro Evolution Soccer 2010 (PC). C# / .NET 9.

`main.py` is the original single-file reference implementation. It is kept **read-only**:
the tooling imports it to generate wire-format golden vectors and to build probe packets,
so the C# server is checked against the exact bytes the Python one produced.

## Layout

```
src/
  OpenEleven.Protocol/     XOR cipher, Blowfish-ECB, packet framing, key=value grammar codec
  OpenEleven.Data/         EF Core entities, DbContext, repositories, seeding
  OpenEleven.Server/       Host, per-service TCP listeners, dispatch, handlers, HTTP surface
tests/
  OpenEleven.Protocol.Tests/   Codec round-trips + byte-for-byte goldens from main.py
  OpenEleven.Server.Tests/     Handler behaviour + reference parity through the real DI stack
conf/server.yaml        All configuration
```

Kept locally but **not tracked** (see `.gitignore`), so a fresh clone will not have
them: `main.py` (the reference implementation — read only, never edited), `tools/`
(`generate_goldens.py`, `probe_client.py` — both import `main.py`), and `docker/`.
The golden vectors those tools produce *are* tracked, at
`tests/OpenEleven.Protocol.Tests/goldens.json`, so the test suite runs without them.

## Run

### Configuration

`conf/server.yaml` is gitignored — it holds the advertised address, certificate paths
and database credentials, which are per machine. Copy the tracked template first:

```bash
cp conf/server.sample.yaml conf/server.yaml
```

Then set **`AdvertiseIp`** to the address the game machine can reach this server on.
Everything else has a working default. Building without a `conf/server.yaml` falls back
to the template, so a fresh clone still builds and runs — but it will advertise `auto`,
which is only right when the resolved NIC is the one the client can see.

### Visual Studio

Set **OpenEleven.Server** as the startup project (right-click → Set as Startup Project) and
press F5. Pick a profile from the dropdown next to the play button:

| Profile | HTTPS | `AdvertiseIp` | Notes |
|---|---|---|---|
| **Server (game ports, loopback)** | 443 | 127.0.0.1 | Default. Game and server on one machine. **Run VS as administrator.** |
| Server (game ports) | 443 | auto | Game on another machine on the LAN. Needs administrator. |
| Server (game ports + MySQL) | 443 | auto | Same, against a local MySQL instead of SQLite. |
| Server (dev ports, no elevation) | 8443 | 127.0.0.1 | Protocol work without an elevated VS. |

**The HTTP surface is HTTPS.** `CMD_GET_URLLIST` advertises
`https://<AdvertiseIp>[:<Listen.Https>]/...`, and the server will not start without
`Https.CertificatePath` / `Https.KeyPath` (PEM, e.g. Let's Encrypt's fullchain.pem and
privkey.pem). The client turns TLS on for every port except 80 (pes2011.exe
`0x00c4965b`), so each title can take its own port. It validates the certificate through
WinInet and cannot ignore errors, so `AdvertiseIp` must be a host name the certificate
covers, never an IP.

For local development set `Https.Enabled: false` (for example in a `server.local.yaml`
next to the running title's `conf/server.yaml`). The server then serves plain HTTP
on port 80, the only plain-HTTP port the client accepts, and `AdvertiseIp` may be an IP.
Only one title can own port 80 at a time.

**The client will not get past discovery unless these URLs load**: it
closes the gate connection after `CMD_GET_URLLIST`, fetches the EULA and posts to
`/gameid_auth`, and only then continues, with no error on the wire if that fails.

Profiles are in `src/OpenEleven.Server/Properties/launchSettings.json`; they set only
`OpenEleven_`-prefixed environment variables, so they layer over `conf/server.yaml` without
duplicating it.

### Command line

Each title is its own executable under `src/OpenEleven.<Title>` (Pes2010Pc, Pes2011Pc,
Pes2012Pc, Pes2013Pc), loading its own `conf/server.<title>.yaml`:

```bash
dotnet run --project src/OpenEleven.Pes2010Pc
dotnet run --project src/OpenEleven.Pes2011Pc
dotnet test                                                   # full suite
```

With no `--config`, the server loads `conf/server.yaml` from next to the binary. The SQLite
file is anchored to the application directory too, so F5, `dotnet run` and the container
all use the same database rather than one per working directory.

For persistent local overrides, copy `conf/server.yaml` to `conf/server.local.yaml`; it is
loaded on top of the main file and is git-ignored.

Probe a running server with packets built by the reference implementation:

```bash
python tools/probe_client.py --port 28010 CMD_GET_SVRLIST
python tools/probe_client.py --port 28014 --expect 2 MSG_REQCCODE MSG_REQAUTH
```

## Service ports

Each logical service now has its own listener, so traffic can be read per stage.

| Service  | gid | Port  | Fixed? |
|----------|-----|-------|--------|
| Gate     | 1   | 28010 | **Yes** — the client hardcodes it |
| FdLobby  | 2   | 28011 | No |
| Lobby    | 3   | 28012 | No |
| Menu     | 4   | 28013 | No |
| Account  | 5   | 28014 | No |
| VdpChat  | 6   | 28015 | No |

Everything except the gate is discovered from the `svrlist` payload the gate returns, so
those ports can be changed freely in `conf/server.yaml`. `svrgid` keys the client's
internal service table and must stay stable even when ports move.

Two consequences of the split, both handled in code:

- **Identity sharing.** The client opens one socket per service and does not repeat the
  login handshake on each. A new connection inherits pid, name and login progression from
  an authenticated session on the same remote address
  (`Protocol.ShareIdentityByRemoteAddress`). Room and match membership are never inherited.
- **Advisory service roles.** Each command declares which services it belongs to, but a
  command arriving elsewhere is logged rather than refused, because the reference server
  answered everything on one port. Set `Protocol.EnforceServiceRoles: true` once captures
  confirm the mapping.

## Configuration

`conf/server.yaml`, overridable by `conf/server.local.yaml` and then by environment
variables using a `OpenEleven_` prefix and `__` for nesting:

```bash
OpenEleven_Server__AdvertiseIp=192.168.1.20
OpenEleven_Server__Database__Provider=MySql
```

Keys worth knowing:

| Key | Why it matters |
|---|---|
| `AdvertiseIp` | Written into every `svraddr`. `auto` resolves the primary NIC. A wrong value sends the client somewhere unreachable — this is the most common misconfiguration. |
| `Lobbies` | The blocks offered by `CMD_GET_BLOCKLIST`, **maximum 10**. The client joins one by its **position** in this list, so reordering changes what a running client selects; set `Id` explicitly to keep occupancy bookkeeping stable across a reorder. |
| `Protocol.RequireRegisteredAccount` | Refuse a client that matches no registered account. A client that does match always has its serial checked regardless. |
| `Protocol.EnforceSessionState` | Rejects commands that arrive before their stage instead of running them against half-initialised state. |
| `Protocol.EmitUnconfirmedMessages` | Messages whose names are inferred rather than captured. Off by default: an unexpected message can crash the client. |
| `Database.Provider` | `Sqlite` for a zero-infrastructure dev run, `MySql` for deployment. |
| `Debug.HexDump` | Hex + ASCII of every packet's data, both directions, including binary packets. On by default for RE work. `Debug.HexDumpMaxBytes` caps it per packet (0 = uncapped). |
| `Debug.PrettyPrintPayloads` | Renders key=value payloads as an indented tree instead of one long line. Turn off to get the raw wire text back. |
| `Debug.Colors` | `Auto` (default), `Always` or `Never`. Auto colours only on a real terminal. **Visual Studio's Output window shows no colour** — it is fed by the Debug logger provider, which prints escape codes literally; run from a terminal to see it. |

## Architecture

```
TCP :2801x ──► GameListener (one per service, BackgroundService)
                   │ accept
                   ▼
              GameConnection ── owns ─► Session (per-connection state)
                   │
                   │  XorCipher → PacketCodec → BlowfishEcb → KvReader
                   ▼
              CommandDispatcher ──► CommandRegistry.TryGet(msg)
                   │                 role check, session-state check
                   ▼
              [Command] handler method (scoped, gets repositories)
                   │
                   ├─ returns KvMessage[]   → the caller's channel
                   └─ Hub.BroadcastToRoom() → other sessions' channels
                   ▼
              writer loop: KvWriter → InnerBody → Blowfish → PacketCodec → Xor → socket
```

Reader and writer are separate loops over a `Channel<OutboundPacket>`, which is what lets
any handler on any connection push a packet to any other session — the prerequisite for
rooms, chat and match offers.

### DI lifetimes

| Type | Lifetime | Why |
|---|---|---|
| `Hub` | Singleton | The one global state store: all sessions and rooms. |
| `CommandRegistry` | Singleton | Built once by assembly scan, immutable after; dispatch uses compiled delegates. |
| `XorCipher`, `BlowfishEcb`, `PacketCodec`, `KvReader`, `KvWriter`, `ProtocolCodecs` | Singleton | Stateless; injected rather than static so tests can swap keys. |
| `ServerCatalog`, `WebAssets` | Singleton | Derived from config at startup. |
| `IOptionsMonitor<ServerOptions>` | Singleton | Config with live reload on YAML change. |
| `GameListener` (one per service) | Singleton `BackgroundService` | Owns a port. |
| `Session` | Per connection, **not** in DI | Owned by the connection so nothing can capture it in a singleton. |
| `GameDbContext`, repositories, handlers | Scoped | One DI scope per dispatched command — a connection can live for hours, a `DbContext` must not. |

A singleton never depends on a scoped service: `Hub` holds `Session` POCOs, and handlers
get their repositories per dispatch.

## Adding a command

Add a method. Nothing in the dispatch core changes.

```csharp
[Command("CMD_GET_SOMETHING", Roles = ServiceRole.Lobby,
         RequiredState = SessionState.Authenticated)]
public async ValueTask<KvMessage[]> GetSomething(CommandContext ctx)
{
    var rows = await _repository.GetRowsAsync(ctx.CancellationToken);
    return [ctx.Ok().SetList("count", "list", rows.Select(Present).ToArray())];
}
```

Return several messages for a START/DATA/END sequence — they are one logical response, so
they are one handler. Always write lists with `SetList`, which emits the count field and
the list together: a count that disagrees with its list is a documented way to crash the
client, and this is the only supported way to write one.

## Commands with no handler yet

Anything unrecognised gets a bare acknowledgement — `result="NOERR",msg=<same>,rqid=<same>`
— which is correct for commands that only need confirmation and is enough to keep the
client moving past most gaps. Each occurrence is logged, and a ranked summary is printed at
shutdown:

```
3 command(s) had no handler this session:
  CMD_GET_PLAYERNUMBERS   Menu   x14   first seen in InBlock   rqid=9,svrtype=MENU,...
  CMD_GET_FRIENDLIST      Menu   x6    first seen in InBlock   rqid=6,target_pid=1,...
```

A high count means the client keeps re-asking, which usually means the ack was not what it
wanted — list queries in particular need a `count` field, and a reply without one leaves the
client reading an uninitialised value. To find the right shape, locate the command's string
in `pes2010.exe` `.rdata`, follow the xref to its parser, and read which keys it looks up.
Then add a `[Command]` method for it.

## Account registration

The game has no registration flow, so accounts are created out of band.

**In a browser:** open `http://<server>/register` — a Razor page
(`src/OpenEleven.Server/Pages/Register.cshtml`, styled by `wwwroot/css/register.css`). It asks
for the Game ID, a password twice, and the serial, then posts a plain form and hashes the
password server-side. No JavaScript at all, and nothing loaded off this machine — the game
box has no internet route through this server.

Passwords are restricted to **printable ASCII, at most 32 characters**. The digest has to be
byte-identical to what the game computes from the same typed password, and we do not know
which encoding it uses; inside 0x20–0x7E every candidate encoding agrees, so the question
never arises. Accepting anything wider would create accounts that register cleanly and can
never log in.

**From a script:**

```bash
curl -X POST http://localhost/api/register \
  -H 'Content-Type: application/json' \
  -d '{"gameId":"marcos","passwordHash":"86d84f97...","regCode":"5HRVLVRUF75RMV2LRK45"}'
```

```
201  {"id":2,"gameId":"marcos","regCode":"5HRVLVRUF75RMV2LRK45"}
409  {"error":"That gameId is already registered."}
400  {"error":"passwordHash must contain exactly 32 hexadecimal characters."}
```

The API takes a digest, not a password: `passwordHash` is stored exactly as supplied and
compared, never reversed. Hash with the same convention the client uses —
`AuthProof.HashPassword` is that convention, and the form goes through it.

The form and the API are separate routes because one path cannot serve two POST handlers;
both converge on `RegistrationService`, so validation and error wording cannot drift.
Neither request body is ever written to the log, even with `Debug.HexDump` on: both
endpoints carry `[SensitiveBody]`. That covers the API too — the digest it carries is a
password-equivalent bearer token, so logging it is no better than logging the plaintext.

`regCode` is the product serial, unique per account. It is set here and **only** here: at
`MSG_REQAUTH` the client presents its own copy as `tmpRegcode`, and the two must match or
authentication is refused with `reason="REGCODE"`. Nothing the client sends is ever written
to the field.

How the account is identified matters, because the game socket never carries the username:
the client posts it to `/gameid_auth` over HTTP and afterwards presents only the credential
it got back, as `uname`. That credential is the stored password hash, so **register with the
same hash the client will present** or the account will not be found.

`Protocol.RequireRegisteredAccount` decides what happens to a client matching no account.
Off by default — an unrecognised client is admitted with nothing bound to its session, so the
protocol can be exercised before any account exists. Turn it on once registration is in use.
A client that *does* match an account always has its serial checked either way.

**Registration is unauthenticated, and the form sends a plaintext password.** Configure
`Https` in `conf/server.yaml`, or register only over a trusted LAN. Note this is a real
regression for password *reuse* but close to none for this system: the digest the old
JavaScript form sent was itself password-equivalent, so intercepting it already granted full
account access.

Anyone who can reach port 80 can create accounts.
Firewall it, or put it behind whatever front end owns registration, before exposing the
server publicly.

## Database

SQLite by default (`data/OpenEleven.db`, created and seeded on first run), MySQL in Docker.
Tables: `Accounts`, `Players`, `PlayerStats`, `Matches`, `Information`.

Blocks are **not** a table — they come from the `Lobbies` config section, because the list
is operator policy that changes as a unit and the client addresses entries by position.

Seeding reproduces what the reference server had hardcoded — one information item and the
"Local Player" profile — so a fresh database behaves the way the client is already known to
accept. Timestamps are stored as UTC `DateTime`, not `DateTimeOffset`: SQLite cannot order
or index the latter.

## Docker

```bash
docker compose -f docker/docker-compose.yml up --build
```

The client reaches the server through a hosts-file redirect of the Konami hostnames. That
redirect must point at the **Docker host's** address, and `AdvertiseIp` must be that same
address — otherwise the server list hands the client an address it cannot reach.

## Testing

- **Golden vectors** — `python tools/generate_goldens.py` imports `main.py` and dumps
  inner bodies, Blowfish blocks, complete wire frames and response strings to
  `tests/OpenEleven.Protocol.Tests/goldens.json`. The C# tests assert byte equality against
  them, including `CMD_GET_PLAYERINFO`, which is the largest response the server sends.
- **Behaviour tests** run the real DI stack against a throwaway SQLite file, covering
  dispatch, state gating, identity sharing across ports, and room membership.
- **Live probe** — `tools/probe_client.py` builds packets with the reference
  implementation's own codecs and prints the decrypted replies.

## Status

Working: gate discovery, EULA/URL list, HTTP asset and game-id auth endpoints, challenge
and authentication, profile list and selection, player info, profile editing, block list
and join, block player list, room create/join and member list, quick-match start.

The grammar handles three value shapes: quoted strings, record lists (`key=[{..},{..}]`),
plain scalar lists (`key=["","",""]`) and bare records used directly as a value
(`profile={date=0,country=50}`). The last one only turned up when a live client sent
CMD_SET_PLAYERPROFILE — worth remembering that the client's grammar is wider than any
single capture shows.

Stubbed: the challenge code is fixed and any credential is accepted; match results are not
yet recorded.

`MSG_ROOMINNOTICE` and `MSG_PxROOMOUTNOTICE` — the notices telling a room's occupants that
someone joined or left — are gated behind `Protocol.EmitUnconfirmedMessages` and therefore
**off by default**. `MSG_ROOMINNOTICE` reproducibly closes the recipient's Lobby connection,
and the cause is not yet understood: the payload matches the field table `FUN_00BB4D70`
declares, with the type codes and sizes that table expects.

The room roster does not depend on them. A join or leave pushes a fresh
`MSG_RMEMBERLISTSTART` / `MSG_RMEMBERLIST` / `MSG_RMEMBEREND` stream (the same sequence
`MSG_REQRMEMBERLIST` returns) to every other occupant, so the room screen still gains and
loses players with the notices disabled. Turn the flag on to resume tracing those messages.

When the server advertises a private address (`AdvertiseIp` resolves to RFC1918/loopback),
`CMD_GET_IPANDPORT` and `CMD_WATCH_IPANDPORT` hand out the peer's internal endpoint. Two
clients on that LAN cannot reach each other through the router's public address, which is
what the client reported at `CMD_JOIN_BLOCK` as its external endpoint. A publicly advertised
server keeps the external endpoint.

## License

OpenEleven is licensed under the **GNU Affero General Public License v3.0 or later**
(`AGPL-3.0-or-later`). The full text is in [LICENSE](LICENSE); the short version:

- You may run, study, modify and redistribute this software, including commercially.
- If you **distribute** a modified version, you must pass on the same license and the
  complete corresponding source.
- If you **run a modified version as a network service** — which is exactly what this
  project is for — section 13 requires you to offer everyone interacting with it (the
  players) the corresponding source of the version you are actually running. Pointing
  them at your fork's public repository satisfies this; you are not required to send
  changes upstream.

The AGPL covers this code only. It grants no rights to Pro Evolution Soccer, its assets,
its trademarks or its network protocol, which remain the property of their owners.

Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Contributions are accepted under the
same license with a DCO sign-off — see [CONTRIBUTING.md](CONTRIBUTING.md).

Questions about the license go to the maintainers at <pes5indie@gmail.com>. Note that
contributions are accepted under the AGPL with a DCO sign-off only, so the project as a
whole cannot be relicensed under different terms.
