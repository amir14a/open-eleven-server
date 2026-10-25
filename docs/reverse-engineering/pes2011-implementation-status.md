# PES 2011 (PC) — implementation status

Companion to [`pes2011-session-protocol.md`](pes2011-session-protocol.md), which holds the protocol
research. This file holds the state of the *implementation*: what runs, what is proven, and what is
still a guess. It is deliberately blunt — a build passing is not evidence of compatibility.

## Executable under analysis

| | |
|---|---|
| Path | `F:\Juegos\KONAMI\Pro Evolution Soccer 2011\pes2011.exe` |
| Size | 40,973,312 bytes |
| File version | 1.0.0.0 |
| Format | PE32, x86, little-endian, image base `0x00400000` |
| Ghidra language | `x86:LE:32:default`, 56,350 functions |
| Tooling | Ghidra 12.0.4 + GhidraMCP 5.7.1 (`127.0.0.1:8089`) |

Addresses below are absolute as Ghidra displays them for this image base. Anything re-derived on a
different build must be located by the referenced string or constant, not by address.

### Function and data addresses used

| Address | Role | Evidence |
|---|---|---|
| `0x01569084` | 4-byte XOR key `5B 9F 2E 64` | byte-pattern search; used by the frame reader |
| `0x0160c6b0` | 56-byte Blowfish key | byte-pattern search; consumed by `FUN_00feba00` |
| `FUN_00feba00` | Blowfish key schedule | `iVar4 % 0x38` over the key at `0x0160c6b0` |
| `FUN_01006b40` | Blowfish cipher object ctor | calls `FUN_00feba00` |
| `FUN_01006660` | **frame reader** — 24-byte header, MD5, XOR, 0x400 cap | decompiled in full |
| `FUN_01011540` | connection object that drives `FUN_01006660` | caller of the reader |
| `FUN_0050d880` | registers `NETCLIENT` as the first command | `FUN_0051fc00("NETCLIENT")` |
| `FUN_005228a0` | NETCLIENT handshake state machine | state 0/1/2 |
| `FUN_00541e80` / `FUN_0056c5d0` | name → message-class dispatch; writes `msg="<name>",` | `"msg"` at `0x012f363c` |
| `FUN_0100cd40` | command-name → message class table (~23 classes) | decompiled |
| `FUN_00512320` | builds `CMD_REG_SVRLIST` self-registration payload | gate host + immediate `0xfaa` |
| `FUN_00ffd160` | `CMD_GET_SVRLIST` **response parser** | `server_list[%d].%s` at `0x013ba1a4` |
| `0x013ba23c` | `svrtype` comparison table: `MENU`, `AUTOLOGIN`, `ACCOUNT`, `GATE`, `VDPCHAT` | string dump of `0x013ba1a0`–`0x013ba420` |
| `0x013ba3a0` | canned failure `result="%s",server_num=0,server_list=[]` | string dump |
| `FUN_0061c360` | login state machine (AUTH_GAMEID → LOGIN) | decompiled in earlier pass |
| `FUN_006425c0` | `CMD_LOGIN` sender | decompiled in earlier pass |
| `0x0130c9b8` | `account_first_login` response field | string search |
| `0x0130cc90`–`0x0130cdc0` | gate state machine's **literal table**: `timeout`, `svrtype="GATE"`, `lang`, `uri`, `read_buffer`, `logoutstat="FIN"`, `region` | memory dump |
| `0x013bee08`…`0x013bee90` | `ulist` entry `type` vocabulary (`PC_SPEC`, `KONAMIID`, `GAMEID_AUTH`, `EULA_PES`, `PATCH_DL`, …) | memory dump |
| `0x012e4a20` | `CMD_GET_URLLIST` response keys `url_num`, `ulist[`, `type`, `url`, `version`, `file_size`, `md5` | memory dump |

## PES 2010 vs PES 2011 compatibility matrix

| Component | PES 2010 | PES 2011 | Reuse |
|---|---|---|---|
| Gateway connection | TCP, `CMD_GET_SVRLIST` on a fixed gate port | TCP, same command, port **4010** (`0xfaa` in `FUN_00512320`) | **Shared transport, different port** (config) |
| Transport / ports | 24-byte frame, gate + 5 service ports | **identical 24-byte frame**; gate 4010, others from `server_list` | **Directly reusable** (`PacketCodec`) |
| Packet framing | `[u16 id][u16 len][u32 ctr][16B MD5]`, XOR `5B9F2E64` | **identical** (`FUN_01006660`) | **Directly reusable** (`XorCipher`, `PacketCodec`) |
| Packet body | `0x0060` → Blowfish-ECB, 56-byte key | **same key, same ids** | **Directly reusable** (`BlowfishEcb`) |
| Inner body | `[len][len][flag][ASCII KV]`, 8-byte padded | *assumed identical* — not re-derived | **Reused, INFERRED** (`InnerBody`) |
| Max frame | not checked in this repo | **1024 bytes**, enforced by the client reader | **New** (constant in `GameConnection`) |
| Authentication | `MSG_REQCCODE` + `MSG_REQAUTH` challenge/response | **different**: `CMD_AUTH_GAMEID` ticket, then `CMD_LOGIN` | **Separate implementation** |
| Encryption | XOR + Blowfish only | same; `PASS_AES` exists on `gameid_password` but its key is unrecovered | **Separate** (AES refused) |
| Server list | `server_num` + `svrlist`, entries `svrtype/svrname/svrport/svraddr/max_player_num/player_num/svrgid` | `server_num` + `server_list`, entries `svrtype/svraddr/svrport/userCount` only | **Separate payload** (`ServerCatalog` branches) |
| `svrtype` values | `GATE/FDLOBBY/LOBBY/MENU/ACCOUNT/VDPCHAT` | only `MENU/AUTOLOGIN/ACCOUNT/GATE/VDPCHAT`; anything else ⇒ `ERR_CLIENT_OPTFORMAT` | **Separate mapping** |
| Lobby system | `CMD_JOIN_LOBBY` / `CMD_JOIN_BLOCK` / block list | same command names documented, not yet implemented | **Not implemented** |
| Room management | implemented (`RoomHandlers`) | command names/fields documented (§6), not implemented | **Reverse-engineered, not implemented** |
| Match negotiation | implemented for 2010 | documented (§6/§8), not implemented | **Reverse-engineered, not implemented** |
| STUN / NAT | shared `pes7stun-*` service | **same hostnames**, port not a static constant | **Reusable in principle; unverified** |

The two rows to read carefully are *inner body* and *STUN*: they are the ones where "looks the same"
is doing work that evidence has not done.

## First real client session (2026-10-23)

A retail `pes2011.exe` was pointed at this server via `hosts`. It connected and produced this
exchange, which is the first evidence that does not come from a hand-written probe:

```
Gate  opened 127.0.0.1:52937
IN  CMD_GET_SVRLIST   rqid=1     OUT NOERR
IN  CMD_GET_SVRVERSION rqid=2    OUT NOERR
IN  CMD_GET_SVRTIME    rqid=3    OUT NOERR
IN  CMD_GET_URLLIST    rqid=4    OUT NOERR
Gate  closed 127.0.0.1:52937        ← then the whole sequence repeated twice more
```

What this establishes:

- The frame, the XOR/Blowfish layer and the KV envelope are **accepted by the real client** — it
  decoded five replies in a row and advanced to the next command each time. Before this run the
  reuse of the PES 2010 transport was an inference; it is now client-confirmed.
- `CMD_GET_SVRLIST`, `CMD_GET_SVRVERSION`, `CMD_GET_SVRTIME` and `CMD_GET_URLLIST` payloads are
  accepted (no `ERR_CLIENT_OPTFORMAT` abort, no disconnect at the reply).
- The client does **not** send `NETCLIENT` first on this path: it opens with `CMD_GET_SVRLIST` and
  uses `rqid=1..4` from a fresh counter. `NETCLIENT` remains implemented because `FUN_0050d880`
  registers it, but it was not observed here — worth re-checking on the ACCOUNT socket.
- The first real blocker is immediately after `CMD_GET_URLLIST`: the client closed the gate
  connection and retried the identical sequence three times, never fetching a single URL
  (no `HTTP GET` line in the trace) and never opening the ACCOUNT connection.

Diagnosis at the time: the `ulist` URLs carried an explicit `:8081`, and the client issues no
request at all. Changed to the shape the working PES 2010 setup uses — `AdvertiseIp: 127.0.0.1`
and HTTP on port 80, so the URLs become `http://127.0.0.1/pcspec.bin` with no port component.
This is a hypothesis under test, not a confirmed cause: the alternative is that the client rejects
another field of the `ulist` entry (`md5=0`, or `version=1`), and the retry loop alone cannot
distinguish them. The next client run decides it.

`pes2011pc.log` (see `Server:LogFile`) is required for this loop: the console output of a failing
attempt is gone before it can be read.

## Status

### Implemented and client-tested

Nothing has completed the *whole* online flow. What has been confirmed against a retail client is
the gate half: the client accepted the framing, decrypted five replies and advanced through
`CMD_GET_SVRLIST` → `CMD_GET_SVRVERSION` → `CMD_GET_SVRTIME` → `CMD_GET_URLLIST`, then stalled on
the step after `CMD_GET_URLLIST` (see "First real client session" above). Everything past that
point is still unproven with a real client.

What *has* been proven end to end is that the server speaks the recovered wire format: a separate
Python implementation of the frame (`tools/probe_pes2011.py`, using pycryptodome and its own
MD5/XOR/Blowfish code, sharing nothing with the C# server) connected to the running server and
completed the full sequence below. That rules out "the server only agrees with itself", and it is why
the framing is listed as verified rather than assumed.

### Implemented and tested (unit + live socket, no game client)

| Feature | Where | Evidence |
|---|---|---|
| Frame reuse for PES 2011 | `Protocol/Framing`, `Protocol/Crypto` | `FUN_01006660`; keys at `0x01569084` / `0x0160c6b0` |
| First command `NETCLIENT` | `Pes2011Handlers.NetClient` | `FUN_0050d880`, `FUN_005228a0` |
| Gate self-registration `CMD_REG_SVRLIST` | `Pes2011Handlers.RegisterServerList` | `FUN_00512320` |
| Server list in the PES 2011 shape | `Pes2011Handlers.GetServerList`, `ServerCatalog` | `FUN_00ffd160`, `0x013ba1a4`, `0x013ba23c` |
| `svrtype` restricted to the five parsed values | `ServiceRoleExtensions.ToPes2011SvrType` | `0x013ba23c` |
| Login stage 1 `CMD_AUTH_GAMEID` (mints a ticket) | `Pes2011Handlers.AuthGameId`, `GameIdTicketStore` | `FUN_0061c360` state 2 |
| Login stage 2 `CMD_LOGIN` (redeems the ticket) | `Pes2011Handlers.Login` | `FUN_0061c360` state 7, `account_first_login` |
| Cached-ticket replay across connections | `GameIdTicketStore` (not single-use) | `FUN_0061c360` state 1 |
| Credential redaction in packet logs | `PacketLogFormatter.Redact` | local policy; verified by test + log inspection |
| `CMD_GET_URLLIST` needs **no** PES 2011 variant — the shared handler's `url_num`/`ulist` shape and its four `type` tokens are all in this binary's vocabulary | shared `GateHandlers` | `0x012e4a20`, `0x013bee08`–`0x013bee90` |
| `CMD_GET_SVRVERSION` needs **no** PES 2011 variant — `version`/`patch_version`/`dlc_version`/`eula_version` is exactly the literal run the client reads | shared `GateHandlers` | `0x012e49e8`–`0x012e4a0c` |
| 1024-byte outbound frame diagnostic | `GameConnection` | `FUN_01006660` |

Observed on the live socket (`tools/probe_pes2011.py`):

```
--> msg="NETCLIENT",rqid=1
<-- result="NOERR",msg="NETCLIENT",rqid=1
--> msg="CMD_GET_SVRLIST",rqid=2
<-- result="NOERR",msg="CMD_GET_SVRLIST",rqid=2,server_num=4,
    server_list=[{svrtype="GATE",svraddr="192.168.100.7",svrport=4010,userCount=0},
                 {svrtype="MENU",...},{svrtype="ACCOUNT",...},{svrtype="VDPCHAT",...}]
--> msg="CMD_AUTH_GAMEID",rqid=2,gameid="pes11probe",gameid_password="...",password_type="PASS_PLAIN"
<-- result="NOERR",msg="CMD_AUTH_GAMEID",rqid=2,loginid="…",loginid_password="…"
--> msg="CMD_LOGIN",rqid=3,svrtype="ACCOUNT",style="LOGINID",loginid="…",loginid_password="…"
<-- result="NOERR",msg="CMD_LOGIN",rqid=3,account_first_login="YES"
```

Failure paths observed on the same socket: wrong password → `ERR_AUTHGID_AUTH`; forged or expired
ticket → `ERR_LOGIN_FAILED`; unknown `style` → `ERR_LOGIN_FAILED`; mismatched serial with
`use_regcode="YES"` → `ERR_REGCODE_FAILED`.

### Implemented but unverified

- **`CMD_GET_HTTP`** returns `read_buffer=""`. The request field (`uri`, at `0x0130cce0`), the
  response field and the success envelope (`0x013bb5b4`) are verified; *what the original service
  returned for a given `uri`* is not. The handler logs the requested `uri` at Information so the first
  real client run names the resource to implement. An empty buffer is a placeholder and is labelled
  as such in the handler.
- **Inner body `[len][len][flag]`** for PES 2011 (see the matrix row). Reused from the validated PES
  2010 implementation on the strength of every surrounding byte matching.
- **`CMD_GET_PLAYERINFO` / `CMD_GET_PLAYERLIST` after login** are served by the shared PES 2010
  handlers. Both `player_count`/`player_data` and `player_num`/`player_list` literals exist in
  pes2011.exe, so the shared keys are plausible but not confirmed for this title.
- **Registration-code gate on `CMD_LOGIN`** uses verified field names (`use_regcode`, `regcode`) and
  verified error names, but the exact semantics of `use_regcode` are inferred.
- **`CMD_GET_SVRTIME`** answers with the shared handler's `date=<unix seconds>`. No `svrtime`, `time`
  or `date` literal exists as a defined string in pes2011.exe, so the key is unverified; the login
  state machine does not branch on the body, so an unused extra field is the low-risk choice.

### Reverse-engineered but not implemented

- Lobby/block selection, room create/join/leave, `CMD_WATCH_ROOMSTATE` push-driven readiness, and
  match negotiation (`CMD_GET_IPANDPORT`, `CMD_SEND_P2PFAILED`). Documented with field names in
  `pes2011-session-protocol.md` §5–§9. None of it is wired into `Pes2011Handlers` yet, and the
  shared PES 2010 room handlers must **not** be assumed compatible with it.
- `qmatchsetting` sub-object of `CMD_START_QUICKMATCH` (§7).

### Unknown or blocked

- **`PASS_AES` key** — `gameid_password` can arrive AES-wrapped; the key is not recovered, so the
  handler refuses that mode rather than treating ciphertext as a password.
- **STUN server port** — set at runtime through `mrdUPnP`, not a constant. Until it is known, P2P
  match negotiation cannot be completed for either title.
- **`AUTOLOGIN` / `VDPCHAT`** — accepted by the server-list parser, no code path found that uses them
  (`AUTOLOGIN` was searched exhaustively; the lead is dead).
- **Gate → ACCOUNT endpoint split** — confirmed to be a separate TCP connection by
  `FUN_0050cfd0("ACCOUNT")`, but whether the original ran it on a different host is not recoverable
  from the client.

## Running it

```bash
dotnet run --project src/OpenEleven.Pes2011Pc -- --config conf/server.pes2011pc.yaml
```

Gate listens on **4010**; `MENU`, `ACCOUNT` and `VDPCHAT` are advertised on the ports in
`conf/server.pes2011pc.yaml`. `FdLobby`/`Lobby` are configured but intentionally **not** advertised
for this title, because PES 2011's parser rejects their `svrtype` tokens.

Verify with the independent probe:

```bash
python tools/probe_pes2011.py --port 4010 NETCLIENT CMD_GET_SVRLIST
python tools/probe_pes2011.py --port 28014 --game-id <account> --password <password>
```

The probe needs an account: `POST http://127.0.0.1:8081/api/register` with
`{"gameId": "...", "passwordHash": "<md5-hex of the ASCII password>", "regCode": "..."}`.

## Tests

`tests/OpenEleven.Server.Tests/Pes2011Tests.cs` (20 tests) covers the server-list shape and its exact
wire text, the `svrtype` restriction, the empty-list failure envelope, `NETCLIENT`, `CMD_REG_SVRLIST`,
`CMD_GET_HTTP`, the full ticket login, cached-ticket replay, forged tickets, wrong passwords,
`PASS_AES` refusal, serial gating, unknown `style`, and the 1024-byte frame bound (round-tripped
through `PacketCodec`).

`tests/OpenEleven.Server.Tests/ProtocolSecretRedactionTests.cs` (13 tests) covers credential masking,
hex-dump withholding, nested records/lists, and false-positive checks.

`Pes2010_discovery_keeps_its_own_payload` guards the PES 2010 payload from the new branch.

## Next step

The shortest path to a *client-tested* result is the real client at the path above:

1. Map `pes11pcgate-e.winning-eleven.net` → `127.0.0.1` and `id.konami.net` → `127.0.0.1` in
   `hosts`, then start the server and register an account.
2. Launch the client, pick "Online", and watch the packet log. The gate sequence
   (`NETCLIENT`, `CMD_REG_SVRLIST`, `CMD_GET_SVRVERSION`, `CMD_GET_SVRLIST`, `CMD_GET_HTTP`,
   `CMD_GET_SVRTIME`, `CMD_GET_URLLIST`) is implemented; the first command that appears in the log
   without a matching `OUT` line is the next thing to research.
3. The likely first blocker is `CMD_GET_HTTP` (empty buffer) or the HTTP URLs `CMD_GET_URLLIST`
   advertises, since the PES 2010 flow depends on those being fetchable.
