# OpenEleven

Reverse-engineered online server for **Pro Evolution Soccer 2010, 2011, 2012 and 2013 (PC)**,
written in C# / .NET 9. Each title runs as its own process with its own database. Each one
also serves a small website (rankings, players, match history, registration) and an
optional admin portal.

- [Titles](#titles) · [Layout](#layout) · [Run locally](#run-locally) · [Deploy with Docker](#deploy-with-docker)
- [Connecting players](#connecting-players) · [Website and admin portal](#website-and-admin-portal) · [Configuration](#configuration)
- [Security](#security) · [Architecture](#architecture) · [Account registration](#account-registration) · [Testing](#testing) · [Status](#status) · [License](#license)

## Titles

| Title | Project | Config | HTTPS | Gate | Other services |
|---|---|---|---|---|---|
| PES 2010 | `src/OpenEleven.Pes2010Pc` | `conf/server.pes2010pc.yaml` | 443 | 28010 | 28011–28015 |
| PES 2011 | `src/OpenEleven.Pes2011Pc` | `conf/server.pes2011pc.yaml` | 8444 | 4010 | 28021–28025 |
| PES 2012 | `src/OpenEleven.Pes2012Pc` | `conf/server.pes2012pc.yaml` | 8445 | 28016 | 28031–28035 |
| PES 2013 | `src/OpenEleven.Pes2013Pc` | `conf/server.pes2013pc.yaml` | 8446 | 15010 | 28041–28045 |

The gate port is hardcoded in each game. Every other port is announced by the gate, so
they can be moved freely, as long as each title's ports don't overlap another's.

## Layout

```
src/
  OpenEleven.Protocol/       XOR cipher, Blowfish-ECB, packet framing, key=value grammar codec
  OpenEleven.Data/           EF Core entities, DbContext, repositories, seeding
  OpenEleven.Server/         Host, listeners, dispatch, handlers, website and admin portal
  OpenEleven.Profiles/       One marker assembly per title (which commands it registers)
  OpenEleven.Pes20xxPc/      One executable per title; links its conf/server.pes20xxpc.yaml
tests/
  OpenEleven.Protocol.Tests/ Codec round-trips + byte-for-byte goldens from main.py
  OpenEleven.Server.Tests/   Handler, web and admin behaviour through the real DI stack
conf/                        Per-title configuration (tracked) and server.sample.yaml
docker/                      Dockerfile, docker-compose.yml, .env.example, certbot hook
docs/                        Reverse-engineering notes and the security audit
```

Kept locally but **not tracked** (see `.gitignore`), so a fresh clone won't have them:
`main.py` (the original single-file Python server, kept read-only as the protocol reference)
and `tools/` (`generate_goldens.py`, `probe_client.py`, both import `main.py`). The golden
vectors they produce *are* tracked, at `tests/OpenEleven.Protocol.Tests/goldens.json`, so
the test suite runs without them.

## Run locally

```bash
dotnet run --project src/OpenEleven.Pes2011Pc     # or Pes2010Pc / Pes2012Pc / Pes2013Pc
dotnet test                                       # full suite
```

In Visual Studio, set the title's project (for example **OpenEleven.Pes2011Pc**) as the
startup project and press F5.

Each project copies its `conf/server.pes20xxpc.yaml` next to the binary as
`conf/server.yaml`; `--config <path>` loads a different file. The SQLite database
(`data/<title>.db`) is also anchored to the binary's folder, so F5, `dotnet run` and the
container never end up with one database per working directory.

**Local overrides** go in `conf/server.local.yaml` next to the binary (for example
`src/OpenEleven.Pes2011Pc/bin/Debug/net9.0/conf/server.local.yaml`). It's loaded on top of
the main file. Environment variables come last, prefixed `OPENELEVEN_` with `__` for
nesting:

```bash
OPENELEVEN_Server__AdvertiseIp=192.168.1.20
OPENELEVEN_Server__Database__Provider=MySql
```

**The website is HTTPS.** `CMD_GET_URLLIST` advertises `https://<AdvertiseIp>[:<Listen.Https>]/...`,
and the server won't start without `Https.CertificatePath` / `Https.KeyPath` (PEM, for
example Let's Encrypt's `fullchain.pem` and `privkey.pem`). The client turns TLS on for
every port except 80 (pes2011.exe `0x00c4965b`). It validates the certificate through
WinInet and can't ignore errors, so `AdvertiseIp` must be a host name the certificate
covers, never an IP.

For local work without a certificate, set `Https.Enabled: false` in the local override. The
server then serves plain HTTP on port 80 (the only plain-HTTP port the client accepts), and
`AdvertiseIp` may be an IP. Only one title can own port 80 at a time.

**The client won't get past discovery unless those URLs load.** It closes the gate
connection after `CMD_GET_URLLIST`, fetches the EULA and posts to `/gameid_auth`, and only
then continues, with no error on the wire if that fails.

Probe a running server with packets built by the reference implementation:

```bash
python tools/probe_client.py --port 28010 CMD_GET_SVRLIST
python tools/probe_client.py --port 28014 --expect 2 MSG_REQCCODE MSG_REQAUTH
```

## Deploy with Docker

One container per title, plus one MySQL container holding a separate schema per title.
MySQL isn't published; only the titles can reach it. From the repository root:

1. **Secrets.** `cp docker/.env.example docker/.env`, then set `PES_DOMAIN` (the host name
   players connect to), the two MySQL passwords and, for each title whose admin portal you
   want, `ADMIN_PASSWORD_PES20xx`. See [Website and admin portal](#website-and-admin-portal).
2. **Certificate** for `PES_DOMAIN`. Issue it with certbot and use the deploy hook, which
   copies the files into `certs/` (the private key at mode 600) and restarts the titles:
   ```bash
   certbot certonly --standalone -d pes.example.com \
     --deploy-hook "$PWD/docker/certbot-deploy-hook.sh"
   ```
3. **Host folders.** Each title keeps its logs in `logs/<title>` and its cookie signing keys
   in `data/<title>/keys`. **Those keys are stored unencrypted**, and anyone who can read
   them can forge admin sessions. Lock the folder down before the first start:
   ```bash
   mkdir -p data logs && chmod 700 data
   ```
   The containers run as root, so they still have access; no `chown` is needed.
4. **Firewall.** Open the HTTPS, gate and service ports of each title you run (see
   [Titles](#titles)). Port 80 is only needed while certbot renews in standalone mode.
5. **Start.**
   ```bash
   docker compose -f docker/docker-compose.yml up -d --build              # every title
   docker compose -f docker/docker-compose.yml up -d --build pes2011pc    # just one (MySQL comes along)
   ```

Each title reads its config from `conf/` on the host. Edit it and restart the container; no
rebuild is needed. The ports published in `docker-compose.yml` must equal the ports in the
title's config, because those are the ports the server tells the client to use.

## Connecting players

The game reaches the server through a **hosts-file redirect** of the title's Konami gate
host name, for example, for PES 2011:

```
<server IP>  pes11pcgate-e.winning-eleven.net
```

It must point at the address of the machine running the server, the Docker host when you
use Docker. `AdvertiseIp` (in Docker, `PES_DOMAIN`) must resolve to that same address, or
the server list hands the client an address it can't reach.

**Windows 7.** The game uses WinInet, the same component Internet Explorer uses, and needs two
things the PC may be missing:

- **TLS 1.2.** It's off unless IE11 is installed or *Internet Options → Advanced → Use TLS
  1.2* is ticked.
- **The certificate's root CA.** Let's Encrypt's current chain ends at ISRG Root X2, which
  older Windows doesn't always have. To install it, download `http://x2.i.lencr.org/`, open
  it and choose *Install Certificate → Local Machine → Trusted Root Certification
  Authorities*.

To check quickly, open `https://<PES_DOMAIN>:<https port>/` **in Internet Explorer**. If it
loads, the game can connect. "Page can't be displayed" means TLS 1.2 is off; a certificate
warning means the root is missing.

## Website and admin portal

Every title serves its own site at `https://<PES_DOMAIN>[:<https port>]/`:

| Page | What it shows |
|---|---|
| `/` | Totals, who's online, live matches with score, top 5, latest matches, in-game news, announcement |
| `/rankings` | The in-game tables: rating, wins, matches, win %, streak; all-time or this week; by division |
| `/players`, `/players/{pid}` | Every player (online first, searchable), and one player's stats and recent matches |
| `/matches` | Every finished match |
| `/register` | Account creation (see [Account registration](#account-registration)) |
| `/admin` | The admin portal, when enabled |

Rating and divisions only appear for titles that have them (not PES 2010). Public pages are
cached for 15–30 seconds. Nothing is loaded from other sites (no CDNs, no web fonts); a test
enforces that.

### Admin portal

Off by default. Turn it on per title:

- **Docker:** `ADMIN_USER` and `ADMIN_PASSWORD_PES20xx` in `docker/.env`.
- **Local runs:** `Admin.User` / `Admin.Password` in `conf/server.local.yaml` next to the
  binary.

Never put the password in `conf/server.pes20xxpc.yaml`: those files are tracked by git.

- **One password per title, at least 12 characters.** A shorter one stops the server at
  startup. Each title has its own database, so bans, news and settings are per title anyway.
  A leaked password should only open one portal.
- **While it's off,** every `/admin` URL answers 404.
- **Changing the password** signs out every open admin session of that title.

What it does:

- **Online:** who is connected, from where, open rooms; disconnect anyone.
- **Accounts:** search by game id or player name. Ban with a reason (which also disconnects
  them), unban, disconnect, or set a new password (which also ends their sessions).
- **News:** the information items the game shows after login.
- **Lobbies:** add, remove, rename, resize and reorder, up to 10. A lobby's id is its
  position, so the list is saved whole and replaces `server.yaml`'s **at the next restart**;
  the page says when a saved list is waiting for one. While a saved list exists,
  `server.yaml`'s `Lobbies` are ignored; *Use server.yaml's lobbies* brings them back.
- **Blocked words:** one word or phrase per line, up to 200. They replace
  `Protocol.BlockedTerms` from `server.yaml` **at once** and survive restarts; *Use
  server.yaml's list* goes back.
- **Settings:** a curated set that applies immediately, without a restart: the home
  announcement, the rank system and a few game rules. Changes are validated before they go
  live and stored in the database, so they survive restarts; *Use server.yaml* undoes one.
  Addresses, ports, HTTPS, the database and the admin settings themselves stay YAML-only.

Every admin action is logged at Warning level with the admin's name.

## Configuration

Keys worth knowing (all under `Server:`):

| Key | Why it matters |
|---|---|
| `AdvertiseIp` | Written into every `svraddr` and URL. Must be a host name the certificate covers; `auto` or an IP only with HTTPS off. A wrong value sends the client somewhere unreachable; this is the most common misconfiguration. |
| `Https` | `Enabled`, `CertificatePath`, `KeyPath`. See [Run locally](#run-locally). |
| `Admin` | `User`, `Password`. Empty password = portal off. Set them in `.env` or `server.local.yaml`, not in a tracked file. |
| `Captcha` | Registration captcha: `None` (default), `Turnstile`, `HCaptcha`, `ReCaptcha` or `Custom`. Docker: `CAPTCHA_*` in `.env`. |
| `Lobbies` | The blocks offered by `CMD_GET_BLOCKLIST`, **maximum 10**. The client joins one by its **position**, so reordering changes what a running client selects. |
| `Protocol.BlockedTerms` | Banned words, matched as whole words in any case. Room names and names the game checks (`CMD_CHECK_STRING`) are refused if they contain one; in text chat they become `***`. Empty by default. Editable from the admin portal (*Words*), which then overrides this list. |
| `Rank` | Rating, points, promotion and relegation for the titles that have divisions. |
| `Protocol.EnforceSessionState` | Rejects commands that arrive before their stage instead of running them against half-initialised state. |
| `Protocol.ShareIdentityByRemoteAddress` | Off. Copies identity between sockets by remote IP; unsafe behind NAT. Captures show real clients authenticate every socket. |
| `Protocol.EmitUnconfirmedMessages` | Messages whose names are inferred rather than captured. Off by default: an unexpected message can crash the client. |
| `Database.Provider` | `Sqlite` for a zero-infrastructure run, `MySql` for deployment. |
| `Debug.HexDump` | Hex + ASCII of every packet, both directions. On for reverse-engineering work; secrets are redacted either way. `Debug.HexDumpMaxBytes` caps it per packet (0 = uncapped). |
| `Debug.PrettyPrintPayloads` | Renders key=value payloads as an indented tree instead of one long line. |
| `Debug.Colors` | `Auto` (default), `Always` or `Never`. Visual Studio's Output window shows no colour; run from a terminal to see it. |

## Security

- **Logins are throttled.** Password checks are limited to 20 per minute per address,
  shared by `/gameid_auth` and `CMD_AUTH_GAMEID`. Registration allows 10 posts per hour per
  address, the ranking endpoint 60 requests per minute, and admin sign-in 10 attempts per 5
  minutes.
- **Secrets stay out of the log.** Registration, admin and `/gameid_auth` bodies are never
  logged (`[SensitiveBody]`). Password and serial fields are redacted from packet logs,
  even with `Debug.HexDump` on.
- **Admin sessions:**
  - the cookie is scoped to `/admin`, HttpOnly, SameSite=Strict, and Secure over HTTPS;
  - it lasts 8 hours, renewed while in use;
  - every form carries an antiforgery token.
- **Key files:**
  - `data/<title>/keys` holds the cookie signing keys unencrypted: `chmod 700 data`;
  - `certs/privkey.pem` is written at mode 600 by the certbot hook;
  - `docker/.env` holds every password: keep it readable by root only.
- **Database:** MySQL is reachable only from the compose network.

The full review is in [docs/security-audit.md](docs/security-audit.md).

## Architecture

```
TCP ──► GameListener (one per service, BackgroundService)
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
any handler on any connection push a packet to any other session. That is the prerequisite
for rooms, chat and match offers. Kestrel serves the website, admin portal and the client's
HTTP endpoints in the same process.

### Services

Six listeners per title: Gate (gid 1), FdLobby (2), Lobby (3), Menu (4), Account (5) and
VdpChat (6). The gate handles discovery (server list, URL list, time) on the port the
client hardcodes; it announces the others. `svrgid` keys the client's internal service table and must stay stable even when ports
move. Each command declares which services it belongs to, but a command arriving elsewhere
is logged rather than refused, because the reference server answered everything on one
port. Set `Protocol.EnforceServiceRoles: true` once captures confirm the mapping.

### DI lifetimes

| Type | Lifetime | Why |
|---|---|---|
| `Hub` | Singleton | The one global state store: all sessions and rooms. |
| `CommandRegistry` | Singleton | Built once by assembly scan, immutable after; dispatch uses compiled delegates. |
| `XorCipher`, `BlowfishEcb`, `PacketCodec`, `KvReader`, `KvWriter`, `ProtocolCodecs` | Singleton | Stateless; injected rather than static so tests can swap keys. |
| `ServerCatalog`, `WebAssets` | Singleton | Derived from config at startup. |
| `IOptionsMonitor<ServerOptions>` | Singleton | Config with live reload on YAML change and on admin-portal saves (`LiveSettings`). |
| `GameListener` (one per service) | Singleton `BackgroundService` | Owns a port. |
| `Session` | Per connection, **not** in DI | Owned by the connection so nothing can capture it in a singleton. |
| `GameDbContext`, repositories, handlers | Scoped | One DI scope per dispatched command. A connection can live for hours; a `DbContext` must not. |

A singleton never depends on a scoped service: `Hub` holds `Session` POCOs, and handlers
get their repositories per dispatch.

### Adding a command

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

Return several messages for a START/DATA/END sequence. They are one logical response, so
they are one handler. Always write lists with `SetList`, which emits the count field and
the list together. A count that disagrees with its list is a documented way to crash the
client, and `SetList` is the only supported way to write one.

### Commands with no handler yet

Anything unrecognised gets a bare acknowledgement, `result="NOERR",msg=<same>,rqid=<same>`.
That is correct for commands that only need confirmation and is enough to keep the client
moving past most gaps. Each occurrence is logged, and a ranked summary is printed at
shutdown:

```
3 command(s) had no handler this session:
  CMD_GET_PLAYERNUMBERS   Menu   x14   first seen in InBlock   rqid=9,svrtype=MENU,...
  CMD_GET_FRIENDLIST      Menu   x6    first seen in InBlock   rqid=6,target_pid=1,...
```

A high count means the client keeps re-asking, which usually means the ack wasn't what it
wanted. List queries in particular need a `count` field; a reply without one leaves the
client reading an uninitialised value. To find the right shape, locate the command's
string in the title's executable `.rdata`, follow the xref to its parser, and read which
keys it looks up. Then add a `[Command]` method for it.

## Account registration

The game has no registration flow, so accounts are created out of band, and **a client
that matches no account is refused** (`NOACCOUNT`).

**In a browser:** `https://<PES_DOMAIN>[:<https port>]/register`. It asks for the Game ID, a
password twice and the serial, plus the captcha if one is configured. The password is hashed
server-side.

Passwords are restricted to **printable ASCII, at most 32 characters**. The digest has to be
byte-identical to what the game computes from the same typed password, and we don't know
which encoding it uses. Inside 0x20–0x7E every candidate encoding agrees, so the question
never arises. Accepting anything wider would create accounts that register cleanly and can
never log in.

**From a script:**

```bash
curl -X POST https://pes.example.com:8444/api/register \
  -H 'Content-Type: application/json' \
  -d '{"gameId":"marcos","passwordHash":"86d84f97...","regCode":"5HRVLVRUF75RMV2LRK45"}'
```

```
201  {"id":2,"gameId":"marcos","regCode":"5HRVLVRUF75RMV2LRK45"}
409  {"error":"That gameId is already registered."}
400  {"error":"passwordHash must contain exactly 32 hexadecimal characters."}
```

The API takes a digest, not a password: `passwordHash` is stored exactly as supplied and
compared, never reversed. Hash with the client's convention; `AuthProof.HashPassword` is
that convention, and the form goes through it. Both routes converge on
`RegistrationService`, so validation and error wording can't drift.

`regCode` is the product serial, unique per account. It is set here and **only** here: at
`MSG_REQAUTH` the client presents its own copy as `tmpRegcode`, and the two must match or
authentication is refused with `reason="REGCODE"`. Nothing the client sends is ever written
to the field.

How the account is identified matters, because the game socket never carries the username.
The client posts it to `/gameid_auth` over HTTPS and afterwards presents only the
credential it got back, as `uname`. That credential is the stored password hash, so
**register with the same hash the client will present**, or the account won't be found.

## Database

SQLite by default (`data/<title>.db`, created and seeded on first run), MySQL in Docker.
Tables: `Accounts`, `Players`, `PlayerStats`, `Matches`, `Information`, `Friendships`,
`Blacklist`, `Notices`, `ShortMails`, `WeeklyRankings`, `Settings` (which also holds the
admin portal's saved settings).

Blocks are **not** a table. They come from the `Lobbies` config section, because the list is
operator policy that changes as a unit and the client addresses entries by position.

Timestamps are stored as UTC `DateTime`, not `DateTimeOffset`, because SQLite can't order
or index the latter.

## Testing

- **Golden vectors:** `python tools/generate_goldens.py` imports `main.py` and dumps inner
  bodies, Blowfish blocks, complete wire frames and response strings to
  `tests/OpenEleven.Protocol.Tests/goldens.json`. The C# tests assert byte equality against
  them.
- **Behaviour tests** run the real DI stack against a throwaway SQLite file. They cover:
  - dispatch and state gating;
  - rooms, quick match and match results;
  - chat, friends and competitions;
  - registration and the captcha;
  - the admin portal;
  - the markup rule that pages load nothing from other sites.
- **Live probe:** `tools/probe_client.py` builds packets with the reference implementation's
  own codecs and prints the decrypted replies.

## Status

Detailed, per-command status lives in
[docs/reverse-engineering/pes2011-implementation-status.md](docs/reverse-engineering/pes2011-implementation-status.md)
and the other notes under `docs/reverse-engineering/`.

`MSG_ROOMINNOTICE` and `MSG_PxROOMOUTNOTICE`, the notices telling a room's occupants that
someone joined or left, are behind `Protocol.EmitUnconfirmedMessages` and **off by
default**. In PES 2010, `MSG_ROOMINNOTICE` reproducibly closes the recipient's Lobby
connection, and the cause isn't yet understood. The room roster doesn't depend on them: a
join or leave pushes a fresh `MSG_RMEMBERLISTSTART` / `MSG_RMEMBERLIST` /
`MSG_RMEMBEREND` stream to every other occupant.

When the server advertises a private address (`AdvertiseIp` resolves to RFC1918/loopback),
`CMD_GET_IPANDPORT` and `CMD_WATCH_IPANDPORT` hand out the peer's internal endpoint. Two
clients on that LAN can't reach each other through the router's public address, which is
what the client reported at `CMD_JOIN_BLOCK`. A publicly advertised server keeps the
external endpoint.

## License

OpenEleven is licensed under the **GNU Affero General Public License v3.0 or later**
(`AGPL-3.0-or-later`). The full text is in [LICENSE](LICENSE); the short version:

- You may run, study, modify and redistribute this software, including commercially.
- If you **distribute** a modified version, you must pass on the same license and the
  complete corresponding source.
- If you **run a modified version as a network service**, which is exactly what this
  project is for, section 13 requires you to offer everyone interacting with it (the
  players) the corresponding source of the version you are actually running. Pointing
  them at your fork's public repository satisfies this; you aren't required to send
  changes upstream.

The AGPL covers this code only. It grants no rights to Pro Evolution Soccer, its assets,
its trademarks or its network protocol, which remain the property of their owners.

Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Contributions are accepted under the
same license with a DCO sign-off; see [CONTRIBUTING.md](CONTRIBUTING.md).

Questions about the license go to the maintainers at <pes5indie@gmail.com>. Contributions
are accepted under the AGPL with a DCO sign-off only, so the project as a whole can't be
relicensed under different terms.
