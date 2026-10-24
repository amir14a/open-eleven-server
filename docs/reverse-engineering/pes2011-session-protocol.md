# PES2011 Online Session Protocol — Reverse-Engineered Flow

Source: static analysis of `pes2011.exe` (PC build) via Ghidra MCP. Goal: enough detail to stand up a
server emulator that speaks this protocol. Confidence is marked per section/command — treat
`VERIFIED` as decompiled directly from the binary, `INFERRED` as derived from address-layout
correlation (see methodology) or naming convention, and `PROPOSED` as an emulator-author's best
guess for fields that could not be recovered statically (test empirically against a packet capture
before trusting).

> This is a living reference. Sections not yet reached (deep COMMU/league system, Master League
> transfer market, full framing/crypto on the wire) are flagged `TODO` rather than guessed at.

---

## 0. Methodology notes (read this first)

- The client protocol is **not binary** — it's a text key/value format, roughly:
  `CMD_NAME` sent as the "command", plus a set of `key=value` pairs, `key="string value"` pairs, and
  `key=[{...},{...}]` arrays. Responses come back the same way, always including a `result` field:
  `result="NOERR"` on success, `result="ERR_XXXXX"` (or a small set of non-`ERR_`-prefixed codes on
  the pre-login "gate" layer) on failure. Example strings pulled directly from the binary:
  ```
  server_list=[{svrtype="GATE",svrport=4010,...,userCount={now=0,min=0,max=10}}]
  result="%s",server_num=0,server_list=[]
  result="NOERR", read_buffer="...
  result="NOERR",msg="CMD_SEND_HEARTBEAT",rqid=%d
  result="NOERR", ranking_data="%s"
  ```
  Every response appears to carry an `rqid` (request id) for correlating request/response — treat
  this as required in your emulator's response envelope. **TODO**: exact byte-level framing (is the
  KV text length-prefixed? null-terminated? wrapped in the header fiveserver-go uses for PES5/6? Not
  yet confirmed — see §1.3.)

- **Command → error-list mapping methodology (high confidence):** every `CMD_XXX` name string and
  every `ERR_XXX` string in the binary's `.rodata` is stored as a *pair*: the command's literal
  string is immediately followed in memory by exactly one error-string literal, repeated once per
  possible error for that command (confirmed via a global 12-byte-row lookup table at
  `0x012ebf28+` that pairs `{cmd_name_ptr, err_name_ptr, code}` — table rows point at exactly this
  interleaved string layout). Concretely: if `CMD_LOGIN` appears 12 times in the binary, it's because
  `CMD_LOGIN` has exactly 12 distinct error outcomes, and they appear in memory as
  `CMD_LOGIN, ERR_A, CMD_LOGIN, ERR_B, CMD_LOGIN, ERR_C, ...` in address order. This was verified by
  hand for `CMD_JOIN_ROOM`, `CMD_CREATE_PLAYER`, `CMD_LOGIN`, `CMD_START_QUICKMATCH` and holds
  consistently. All the "Errors (verified)" lists below were built this way — **not** guessed.

- Everywhere `svrtype="XXXX"` appears in a request, that's the client telling the gate/session layer
  which backend server type should handle the command (see §2). Observed values so far: `GATE`,
  `ACCOUNT`. There are almost certainly more (`LOBBY`/`ROOM`/`MATCH`-ish) not yet pinned down.

- **Booleans are the strings `"YES"`/`"NO"`, not `0`/`1`** (fourth-pass correction — applies
  retroactively to every `<0|1>` field documented anywhere in this file). Found by reading the
  binary's master enum-lookup table directly: the whole protocol funnels enum-to-string conversion
  through one function (`FUN_0100b870(type_id, index)` → looks up `PTR_PTR_01583e78[type_id][index]`,
  a table of ~77 typed string arrays covering every enum field in the protocol, not just booleans).
  Type id `0x4c` — the single most-used type id in the entire binary — resolves to exactly 2 values:
  `"NO"` (index 0), `"YES"` (index 1). So `is_invited=YES`, `enable_guest=NO`, etc. — **not**
  `is_invited=1`. Wherever this doc still shows `<0|1>` for a field, mentally substitute
  `"NO"`/`"YES"`.

- This same master enum table is where the room-status and match-phase enums in §6 came from —
  worth knowing it exists if you need to resolve other enum fields not yet covered here (e.g.
  `qmatchsetting`'s `opp_lang`, or `card_kind` in §9's `CMD_ADD_FOUL`): find the `FUN_0100b870(type_id,
  ...)` call site for the field you care about, then read `PTR_PTR_01583e78[type_id]` (an array of
  `count` string pointers, count stored at the parallel array based `013bef08[type_id]`) directly out
  of process memory.

---

## 1. CONNECT phase

### 1.1 Endpoints

| Purpose | Host | Port | Protocol | Confidence |
|---|---|---|---|---|
| Gate / master server | `pes11pcgate-e.winning-eleven.net` | **4010** (`0xfaa`) | TCP | VERIFIED |
| STUN (NAT traversal) | `pes7stun-e.winning-eleven.net` (EU), `pes7stun-u` (US), `pes7stun-j` (JP), `pes7stun` (fallback) | not statically pinned — the STUN server port is set at runtime via the `mrdUPnP` middleware's own `mrdUPnP_CMD_SET_STUN_SERV_HOST`/`mrdUPnP_CMD_SET_STUN_SERV_PORT` internal API rather than a hardcoded constant (config-driven, likely pushed down from the gate/account server rather than baked into the exe) | UDP, RFC3489-style | VERIFIED hostnames / port genuinely not a static constant |
| Konami ID web portal | `https://id.konami.net/` | 443 | HTTPS (WinINet) | VERIFIED, out of scope (unrelated to the game socket protocol) |

**Local STUN/P2P socket binding** (`FUN_0100a470`, deep dive): the client tries to bind its own local
UDP socket starting at port **0x1662 (5730)**, incrementing up to **0x166c (5740)** if the first
choice is taken (`htons()` + a bind-probe loop). This is the *client's own* ephemeral port range for
STUN/P2P traffic, not the STUN server's listening port. NAT type detection results are classified
into `mrdUPnP`'s own enum, confirmed via literal strings: `NAT_TYPE_OPEN_INTERNET`,
`NAT_TYPE_FULL_CONE_NAT`, `NAT_TYPE_SYMMETRIC_UDP_FIREWALL`, `NAT_TYPE_UNKNOWN_TYPE`,
`NAT_TYPE_SYMMETRIC_NAT`, `NAT_TYPE_RESTRICTED_CONE_NAT`, `NAT_TYPE_RESTRICTED_PORT_CONE_NAT` — this
is what feeds `ERR_CHECK_NATTYPE` in quickmatch (§7).

Region selection for STUN host is driven by a global region flag (0=EU/`-e`, 1=US/`-u`, 2=JP/`-j`,
else bare fallback) — `FUN_00ffb2e0`. Same flag likely selects the gate hostname variant too (all 4
observed gate string copies currently resolve to the same literal `pes11pcgate-e...`, i.e. only the
EU variant ships in this particular exe, or region selection for gate wasn't fully realized in this
build).

### 1.2 Socket setup

Standard blocking Winsock (`ws2_32`): `WSAStartup` → `gethostbyname`/`WSAAsyncGetHostByName` →
`socket` → `connect` → `send`/`recv`/`select`. No TLS.

> **Correction (sixth pass).** The first pass claimed "no XOR/Blowfish stream cipher on this socket —
> the wire payload appears to be plaintext KV text". That is **wrong**, and it was wrong because the
> claim was made from the payload *builder* (`FUN_00541e80`/`FUN_0056c5d0`, which does write plain
> ASCII key=value into an in-memory buffer) without ever reaching the socket writer. There is a
> cipher layer, and it is byte-for-byte the PES 2010 one. See §1.4 for the evidence and the layout.

### 1.3 First command on the wire

`FUN_005228a0` — runs immediately after `connect()` succeeds, before any gate business commands:

```
state 0 → (init)
state 1 → send "NETCLIENT"          # VERIFIED literal, no observed KV params
state 2 → poll for response
  on success → done, hand off to gate state machine (§2)
  on failure/timeout → state 0x1869e (abort/reconnect)
```

**Emulator behavior**: on new TCP connection, expect the first command name to be literally
`NETCLIENT`. Respond `result="NOERR"` (`rqid` echoed) to let the client proceed to gate commands.
Field content of the `NETCLIENT` request itself wasn't decompiled — capture a real client to confirm
whether it carries any params (build/version tag is plausible, `PROPOSED` only).

**Sixth-pass correction**: `NETCLIENT` is not a bare literal on the wire, it is an ordinary KV
command name. `FUN_0050d880` registers it with `FUN_0051fc00("NETCLIENT")`, and the same helper that
sends every other command (`FUN_00541e80` → `FUN_0100cd40`) sends it, so the first bytes on the
socket are `msg="NETCLIENT",rqid=<n>` inside the frame of §1.4 — not the 9 ASCII bytes `NETCLIENT`.

### 1.4 Wire framing (VERIFIED — corrects the "plaintext" claim)

This is the finding that makes the transport reusable from the existing PES 2010 implementation.
Every item below was read out of the binary, not inferred from another title.

**Recovered constants**

| What | Address | Value |
|---|---|---|
| 4-byte XOR key | `0x01569084` | `5B 9F 2E 64` |
| 56-byte Blowfish key | `0x0160c6b0` | `D8890AF0…C5E9DAAE` (identical to `GameCrypto.BlowfishKey`) |

**Reader — `FUN_01006660`** (called from `FUN_01011540`; the Blowfish object it uses is built by
`FUN_01006b40`, whose constructor runs the key schedule `FUN_00feba00` over the 56-byte key at
`0x0160c6b0` with `iVar4 % 0x38`, i.e. a 56-byte key). State lives at `esi+0x438`; 5 = read header,
6 = read body, 7 = done, 0xff = dead.

```
[2]  packet id       big-endian uint16      (reassembled as header[0]<<8 | header[1])
[2]  length          big-endian uint16      (0 is legal: the frame then carries no body)
[4]  packet counter  big-endian uint32      (matched against an expected counter by the reader)
[16] MD5             MD5(header[0..7] || body)
[N]  body            N == length
```

The whole frame — header first, then body — is XORed with the 4-byte key, and the keystream is
**aligned to the frame start**: the header is transformed at offset 0 and the body at offset 24
(the loop index is `(pbVar3 - (esi+8)) & 3` starting at `esi+0x20`, which is `i & 3` because
`0x18 % 4 == 0`). The reader then rebuilds the MD5 input as `header[0..7] || body` and compares it
against the digest it decoded from `header[8..23]`, which is exactly `PacketCodec`'s `PrefixSize`
hash.

**Rejected frames**: `if (0x400 < uVar1) → state 0xff`, i.e. **any frame whose length field exceeds
0x400 (1024) kills the connection before the body is read**. `PacketCodec` accepts up to 65535, so
this is a real limit the server has to respect on the way out; `GameConnection` logs an error
for any larger outbound frame when running as PES 2011.

**Text-command body**: packet id `0x0060`. The body is Blowfish-ECB ciphertext, so the KV text is
not visible in a raw capture — which is the other reason the first pass's "plaintext" reading was
mistaken. The decrypted inner body is the PES 2010 layout:

```
[4] payload length (big-endian)
[4] payload length (repeated)
[1] flag
[N] ASCII key=value payload, NUL-terminated
[P] NUL padding so that 9 + N + P is a multiple of 8
```

`INFERRED`, and the one link in this chain that was *not* re-derived for PES 2011: the inner-body
header was taken from the project's validated PES 2010 implementation and reused unchanged. What
makes the reuse safe rather than a guess is that the outer frame, both cipher keys, the MD5
construction, the counter check and the 1024-byte cap all match exactly — the same netcode library
version — and `InnerBody.Wrap`/`Unwrap` are pure functions of that library's own record format.
**Evidence still missing**: the byte-level construction of the 0x0060 body inside pes2011.exe (the
`[len][len][flag]` writes). Confirming it means finding the encrypt-side call site, which is behind
the same per-svrtype virtual dispatch that hid `send()` in earlier passes. A capture from a real
client that accepts a server-sent frame closes this immediately; until then treat the innermost
9 bytes as the highest-confidence inference in this document, not as verified.

**Send path**: `FUN_00541df0(name, payload)` → `FUN_0100cd40(name, …)`, which is a dispatcher that
maps a command name to one of ~23 message classes (`FUN_011becc0`, `FUN_011cdd70`, `FUN_011bdc80`,
`FUN_00ffc890`, … each in its own `__aligned_malloc` size) and then invokes the class's send method.
The envelope is built one field at a time: `FUN_0056c5d0` writes the command name under the key
`"msg"` (string at `0x012f363c`) and then appends `","` (`0x012f3640`). So requests look like
`msg="CMD_GET_SVRLIST",rqid=<n>,…`, which is the same envelope the C# `KvReader` already parses.

---

### 1.5 Large messages: chunking and compression (VERIFIED, `FUN_01013130` -> `FUN_01012e50`)

A single frame body is capped at 0x400 bytes (`FUN_01006660`), but a *message* is not. After the XOR/MD5 stage, `FUN_01012e50` dispatches on the packet id. The encrypted text-command family is:

| Packet id | Handler | Meaning |
|---|---|---|
| `0x60` | `FUN_01011160` | complete message in one frame |
| `0x70` | `FUN_01010da0` | first chunk of a multi-frame message |
| `0x71` | `FUN_01010380` | middle chunk |
| `0x72` | `FUN_01010fe0` | last chunk; completes the message |

(`0x10/0x20/0x21/0x22` are the same four shapes without Blowfish; `0x40/0x50/0x51/0x52` are other channels.) The body is Blowfish-ECB decrypted in place (`FUN_01006300`), then, all integers big-endian:

```
0x60: [X u32][Y u32][flag u8][X bytes]
0x70: [X u32][C u32][Y u32][flag u8][C bytes]      C <= 0x3f8
0x71: [C u32][C bytes]                              C <= 0x3fc, running total <= 0x19000
0x72: [C u32][C bytes]                              then the message is complete
```

* `X` = total stored payload length (bytes carried across all chunks; compressed length when `flag != 0`), `Y` = final uncompressed length, `C` = bytes in this frame. For an uncompressed single frame `X == Y == payload length`, which is what OpenEleven writes today.
* The receiver allocates `X+1` and `Y+1` byte buffers on `0x70`, appends each chunk at a running offset, and refuses the message if the offset would exceed **0x19000 (102,400) bytes**; the client's per-message state is `memset(…, 0x19000)`.
* `flag != 0` means the assembled bytes are a **zlib stream** (RFC1950, bundled zlib 1.2.3, `windowBits` 15, `inflate(Z_FINISH)`; `FUN_011d87c0`) that must inflate to exactly `Y` bytes; on any zlib error the message is dropped with error `-0x47`. `flag == 0` means the bytes are the text itself.
* The text is NUL padded to the 8-byte Blowfish block as before; the lengths exclude the padding.
* Chunk state is shared per connection: a new `0x70` while a message is still open is treated as a fatal error, and `0x71/0x72` without an open `0x70` return `-0x18` (ignored). Frames must therefore be sent in order; each chunk is still an ordinary 24-byte-header frame with its own counter and MD5.

Practical rules for the server (not yet implemented in `GameConnection`):
1. Plain text <= 1015 bytes (`1024 - 9`): one `0x60` frame, as now.
2. Larger: either send `0x70`, `0x71`..., `0x72` with <= ~1000 bytes of text each, or zlib-compress the whole text, set `flag = 1`, `X` = compressed size, `Y` = original size, and chunk the compressed bytes (a 10 KB KV list typically compresses below one frame).
3. Inbound: the server currently drops every frame that is not `0x60`, so any client request above ~1015 bytes (e.g. `SEND_SHORTMAIL` bodies up to 1536 bytes, `SET_GAMEPLAN` data) would be lost. The client's own sender is expected to use the same chunk ids; this was not decompiled.
4. Hard ceiling per message: 102,400 bytes, so large lists (room list <= 100, block players <= 100) are fine once chunked.

---

## 2. GATE phase

Driven by `FUN_0061e620`, a linear state machine over TCP connection to the gate host:port from §1.1.
This is the most solid part of this document — fully decompiled request-builder, in order.

### Sequence

| # | State | Command | Purpose |
|---|---|---|---|
| 1 | connect | *(socket connect, then §1.3 NETCLIENT)* | — |
| 2 | 1 | `CMD_REG_SVRLIST` | Register/announce self to gate |
| 3 | 3 | `CMD_GET_SVRVERSION` | Version check |
| 4 | 5 | `CMD_GET_SVRLIST` | **The important one** — returns real backend server IP:port list |
| 5 | 7 | `CMD_GET_HTTP` | Fetch a small blob over the game socket (web-view style content?) |
| 6 | 9 | `CMD_GET_SVRTIME` | Server clock sync |
| 7 | 0xd | `CMD_GET_URLLIST` | Region-specific URL list (probably web links shown in-game) |
| 8 | 0xf / 0x11 | `CMD_DISCONNECT` / re-`CMD_REG_SVRLIST` | Teardown, or re-register after list refresh |

### Commands (VERIFIED — decompiled from `FUN_0061e620`, `FUN_00512320`)

#### `CMD_REG_SVRLIST` (state 1)
- **Request**: no extra fields observed beyond the envelope; built via `FUN_00512320` immediately
  before, which independently constructs (but doesn't appear to attach to *this* call — see below) a
  `server_list` descriptor block:
  ```
  server_list=[{svrtype="GATE", server_num=<n>, svrport=4010, userCount={now=<n>,min=0,max=10}}]
  ```
  (This block's exact call relationship to `CMD_REG_SVRLIST` vs. a separate registration payload
  wasn't fully pinned down — `FUN_00512320` is called right before the state-1 branch executes, so
  treat this as the request body. `INFERRED` wiring, `VERIFIED` field content.)
- **Proposed response (success)**: `result="NOERR",rqid=<n>`
- **Proposed response (failure)**: `result="SvrTypeErr",rqid=<n>` (only gate-specific error string
  found near this zone; gate layer does not use the `ERR_XXX` scheme that the main session uses —
  `PROPOSED` that this is the only failure mode, could be incomplete)

#### `CMD_GET_SVRVERSION` (state 3)
- **Request**: `svrtype="GATE"` — RESOLVED (sixth pass): `DAT_0130cd00` points at the literal `GATE`
  in the gate literal table and `DAT_0130cd08` at the key `svrtype`.
- **Response — VERIFIED (sixth pass)**: the four field names sit in a contiguous literal run at
  `0x012e49e8`–`0x012e4a0c`:
  ```
  version, patch_version, dlc_version, eula_version
  ```
  `result="NOERR",rqid=<n>,version="…",patch_version="…",dlc_version="…",eula_version="…"`
  **Emulator implication**: the shared `CMD_GET_SVRVERSION` handler already emits exactly these four
  keys, so it is correct for PES 2011 with no per-title variant. Mismatch handling still belongs to
  `CMD_LOGIN` (`ERR_DIFFERENT_PATCHVERSION` / `ERR_DEFCLIENTVER`, §4); this call looks informational.

#### `CMD_GET_SVRLIST` (state 5) — **critical for bootstrap**

**Deep-dive update, in response to a direct question about this one**: you were right to be
suspicious — the `svrtype` value here is **not a fixed compile-time constant**. `DAT_0130ccb8` (the
value passed for `svrtype`) and `DAT_0130ccc8` (the key name of the third field) are both runtime
globals, not string literals baked into `.rodata` (confirmed: `list_strings` finds *zero* defined
strings anywhere near those two addresses — they live in writable memory, populated at runtime, not
in the read-only string pool everything else in this doc comes from). Tracing the third field's
value-builder (`FUN_005104a0`) shows it pulls from a **client-side config/options list**
(`FUN_0109e910`→`FUN_0109e900(1)`, reading element `[1]` of a global settings array) and only falls
back to a hardcoded 2-byte default if that config entry is unset. That's a strong signal this field
is something like a user-selectable region/locale code, not a single hardcoded filter — i.e. this
request's shape can legitimately vary per client/session, exactly as you suspected. I could not
statically pin every value it can take (would need a live capture or the config-file schema), but an
emulator should **not** assume a single fixed `svrtype` — accept whatever the client sends and filter/
return matching `server_list` entries rather than hard-coding a match on `"GATE"` or similar.

**Update (third pass) — the full answer**: found the actual response parser for this exact command
(`FUN_00ffd160`) and read its comparison table directly out of binary memory. `CMD_GET_SVRLIST`'s
`server_list` response recognizes **exactly 5** `svrtype` values: `MENU`, `AUTOLOGIN`, `ACCOUNT`,
`GATE`, `VDPCHAT`. Full breakdown, storage semantics, and field names moved to the response subsection
below (this is now `VERIFIED`, not proposed) — short version: `MENU`/`AUTOLOGIN`/`ACCOUNT` are pooled
(multiple entries allowed, e.g. load-balanced server farms), `GATE` is a singleton, and `VDPCHAT` is
recognized but not stored anywhere by this build. `COMMU` and `MASTERLEAGUE` (seen as literals
elsewhere in the binary, e.g. §6/§9) are **not** in this specific parser's accepted set — they're
valid `svrtype` values used in *other* requests (community/league-scoped calls that stay on the
already-established `ACCOUNT` connection) but not things `CMD_GET_SVRLIST` itself hands back a
dedicated server for.

- **Request** (VERIFIED field names, from `FUN_0061e620` case 5):
  ```
  timeout=<int>            # param_1+0x65c
  svrtype=<runtime value>  # DAT_0130ccb8
  lang=[...]               # DAT_0130ccc8 — RESOLVED (sixth pass): the field is "lang", not an
                           #   opaque region blob. See the literal table note below.
  ```
  > **Sixth-pass resolution of the "runtime globals" caveat above.** Reading the gate state machine's
  > own literal table (0x0130cc90–0x0130cdc0) shows the keys in the order the branches use them, so
  > the three runtime globals are just pointers into that table:
  >
  > | Global | Address | Literal |
  > |---|---|---|
  > | `DAT_0130ccb0` | 0x0130ccb0 | `timeout` |
  > | `DAT_0130ccb8` | 0x0130ccb8 | `GATE` (the `svrtype` **value**, not a key) |
  > | `DAT_0130ccc0` | 0x0130ccc0 | `svrtype` (the key) |
  > | `DAT_0130ccc8` | 0x0130ccc8 | **`lang`** (the third field's key) |
  >
  > So the request is `timeout=<n>,svrtype="GATE",lang=[…]`: a **language filter**, not a
  > region/locale code. That also explains why `FUN_005104a0` reads a client settings entry —
  > the player's language selection — and why the array can legitimately be empty. An emulator can
  > ignore it, but it should not mistake it for a server-type filter.
#### Response — fully VERIFIED, third pass (`FUN_00ffd160`, the actual response parser)

This resolves the "only GATE?" question directly: **no, there are 5 recognized `svrtype` values**,
each individually compared against by name in the parser, in this exact order, with memory read
directly from the binary at `0x013ba23c` onward:

```
MENU        →  stored in a growable list (counter slot, supports multiple entries — a pool)
AUTOLOGIN   →  stored in a growable list (counter slot, supports multiple entries — a pool)
ACCOUNT     →  stored in a growable list (counter slot, supports multiple entries — a pool)
GATE        →  stored in a SINGLE fixed slot (no counter — at most one GATE entry is meaningful)
VDPCHAT     →  recognized (matched, no error raised) but no storage code follows — likely
                ignored/reserved rather than actively used by this build
<anything else>  →  `ERR_CLIENT_OPTFORMAT` is raised (malformed/unrecognized server_list entry)
```

So per your instinct: this build expects a response that can carry up to **three pooled server
types** (`MENU`, `AUTOLOGIN`, `ACCOUNT` — each can appear multiple times, e.g. a load-balanced pool of
account servers) plus **one singleton `GATE`** entry (presumably a self-reference / fallback gate) and
an acknowledged-but-unused `VDPCHAT` type (voice/chat data server — never wired up to storage in this
parser, possibly vestigial from an earlier title or reserved for a feature this build doesn't use).
`MENU` and `AUTOLOGIN` hadn't been directly observed as literals anywhere else in this document
before this — `AUTOLOGIN` in particular strongly implies there's a fast-path reconnect flow (skip full
`CMD_AUTH_GAMEID`+`CMD_LOGIN`, reuse a session against a dedicated "autologin" server type) that
wasn't traced in the login state machine in §4 — worth another pass if you want that flow too.

- **Response fields per entry, VERIFIED exact field names** (correcting the first pass's `svrip`
  guess — the real key is `svraddr`):
  ```
  result="NOERR"
  server_num=<n>                       # total entry count, parser caps read at 200
  server_list=[
    {svrtype="ACCOUNT"|"MENU"|"AUTOLOGIN"|"GATE"|"VDPCHAT",
     svraddr="x.x.x.x",                # up to 0x85 (133) bytes — hostname or dotted-quad, either fits
     svrport=<n>,                      # stored as a 16-bit value client-side
     userCount=<n>},                   # a single count value here (not the {now,min,max} object —
                                        #   that richer shape was only confirmed on the *request*/
                                        #   self-registration side in §2's CMD_REG_SVRLIST, not here)
    ...
  ]
  ```
- **Client identifier default**: if the client's own ID string can't be read/parsed, it falls back to
  the literal `"no_client"` (VERIFIED string) — worth handling as a valid-but-anonymous case rather
  than rejecting it.
- **Emulator implication**: return at least one `ACCOUNT` entry (required to reach §4's login flow),
  and consider also returning a `GATE` self-entry and a `MENU` entry if you implement the post-login
  menu/lobby hand-off as a separate server type rather than reusing the `ACCOUNT` connection. Any
  `svrtype` outside the 5 recognized values will hit `ERR_CLIENT_OPTFORMAT` client-side, so don't
  invent new ones.
- **Proposed response (empty/failure)**: `result="SvrTypeErr",server_num=0,server_list=[]` — this
  exact empty-list literal was found verbatim in the binary at `013ba3a0`.

#### `CMD_GET_HTTP` (state 7)
- **Request**: `uri="<string>"` — RESOLVED (sixth pass): `DAT_0130cce0` points at the literal `uri`
  in the gate literal table (0x0130cce0). The client is asking for a named resource over the game
  socket, so an emulator can log it and learn exactly what the original served.
- **Response**: `result="NOERR", read_buffer="<blob>"` — the envelope literal
  `result="NOERR", read_buffer="` is in the binary verbatim (`013bb5b4`), confirming the field name
  `read_buffer`. The **content** of the blob is still unknown; `uri` plus a live client is what will
  settle it.

#### `CMD_GET_SVRTIME` (state 9)
- **Request**: `svrtype="GATE"` (`DAT_0130cd24` → `GATE`, key `DAT_0130cd2c` → `svrtype`)
- **Response — still unknown (sixth pass)**: none of `svrtime`, `time` or `date` exists as a defined
  string literal anywhere in the binary, so the field the client reads (if any) is not guessable from
  the string pool. The server currently answers `date=<unix seconds>` because that is what the
  validated PES 2010 path does; for PES 2011 that key is **unverified and may simply be ignored**.
  The lowest-risk reading is that the clock sync is informational — the login state machine never
  branches on its body — so an extra unused field is harmless. Confirm by watching whether the client
  stalls or logs an error at this step on a real run.

#### `CMD_GET_URLLIST` (state 0xd)
- **Request** (VERIFIED field names, from `FUN_0061e620` case 0xd, keys resolved from the literal table):
  ```
  svrtype="GATE"        # value at 0x0130cd44, key at 0x0130cd4c
  lang=[...]            # key 0x0130cd54 — same language-filter array as CMD_GET_SVRLIST
  region="<string>"     # key 0x0130cd5c, VERIFIED literal
  ```
- **Response — VERIFIED (sixth pass)**: it is **not** `url_list`. The shape is the same one the PES
  2010 handler already emits, and its literals sit together at `0x012e4a20`:
  ```
  result="NOERR",rqid=<n>,url_num=<n>,ulist=[
    {type="PC_SPEC"|"KONAMIID"|"GAMEID_AUTH"|"EULA_PES"|…, url="<http url>",
     version=<n>, file_size=<n>, md5=<32 hex>},
    …
  ]
  ```
  The `type` vocabulary is a literal cluster at `0x013bedf0`–`0x013beea0`:
  `RANK`, `RANK_WEEK`, `PC_SPEC`, `DOWNLOAD`, `DOWNLOAD_DESC`, `KONAMIID`, `KONAMIID_ALTER`,
  `GAMEID_AUTH`, `VIDEO`, `EULA_PES`, `COMMU_MEMBERS_VS`, `COMMU_TEAM_VS`, `HTTP_TEST`, `ADFILE`,
  `PATCH_DL`, `BANNER…`.
  **Emulator implication**: the shared `CMD_GET_URLLIST` handler is already correct for PES 2011 — it
  emits `url_num`/`ulist` with `type`/`url`/`version`/`file_size`/`md5`, and the four types it uses
  (`PC_SPEC`, `KONAMIID`, `GAMEID_AUTH`, `EULA_PES`) are all in PES 2011's accepted vocabulary. No
  per-title variant is needed.

#### `CMD_DISCONNECT` (state 0xf)
- **Request** (VERIFIED field names, from `FUN_0061e620` case 0xf):
  ```
  svrtype=<const>          # DAT_0130cd74
  logoutstat=<const>       # DAT_0130cd84
  ```
- **Proposed response**: `result="NOERR",rqid=<n>` — client doesn't appear to check the body deeply,
  just transitions state and closes.

### Gate errors

Only one gate-specific literal error string was located: **`SvrTypeErr`** (found at `013ba000` /
`013bb680`, and used in the canned empty-list response above). The main-session `ERR_XXX` enum
(§8) does **not** appear to be used at the gate layer — gate looks like an older/simpler subsystem
(hand-rolled inline `FUN_0061e620` state machine with literal strings) vs. the main session's
generic table-driven command dispatch (§0 methodology). Treat `SvrTypeErr` as the generic
"something's wrong with this request" catch-all for the gate layer until proven otherwise.

---

## 3. Handoff: gate → account server

Client takes an entry from `CMD_GET_SVRLIST`'s `server_list` (svrtype presumably `"ACCOUNT"`),
opens a **new TCP connection** to that `svrip:svrport`, and repeats the §1.3 `NETCLIENT` handshake
on the new socket before sending any account/session commands. (`INFERRED` — not directly observed,
but this mirrors the gate pattern exactly and matches how `svrtype="ACCOUNT"` requests are seen
being sent later, e.g. `CMD_GET_PLAYERLIST` in §5 explicitly carries `svrtype="ACCOUNT"`.)

**Emulator implication**: you need (at minimum) two listening endpoints — a gate listener on 4010,
and an account/session listener on whatever port your `CMD_GET_SVRLIST` response advertises. They
can be the same process/port in your emulator even if the real Konami infra split them.

---

## 4. ACCOUNT / LOGIN phase

**Deep-dive update**: the real request-builder chain was found. `CMD_LOGIN` itself is issued from
`FUN_006425c0`, called by the login state machine `FUN_0061c360`. This section is now `VERIFIED`
end-to-end, not proposed — full request field list decompiled directly from the binary.

### 4.0 Actual login sequence (VERIFIED, `FUN_0061c360`)

The client does **not** send `CMD_LOGIN` as the first authenticated call. The real flow is a
two-stage ticket exchange:

```
state 0 → wait
state 1 → check a locally-cached "have game-id ticket?" flag (param+0x51c)
            byte==0  → skip straight to state 4 (use previously-cached loginid/loginid_password)
            byte!=0  → go to state 2 (need a fresh ticket)
state 2 → send CMD_AUTH_GAMEID (see §4.1)               [only when a fresh ticket is needed]
state 3 → on success, extract "loginid" and "loginid_password" from the AUTH_GAMEID response
            and cache them locally
state 4 → depending on a mode flag (param+0x3b8 == 1 or 2): reconnect the active connection to
            the svrtype="ACCOUNT" server (FUN_0050cfd0("ACCOUNT")) — i.e. this issues a fresh
            CMD_DISCONNECT/CMD_GET_SVRLIST-style handoff cycle before continuing (state 5/6)
state 5/6 → CMD_DISCONNECT (svrtype=<current>, logoutstat=<const>) then reconnect
state 7 → send CMD_LOGIN (see §4.2) using the cached loginid/loginid_password
state 8 → on success, read "account_first_login" (bool) from the response; on
            ERR_DEFCLIENTVER / ERR_DIFFERENT_PATCHVERSION specifically, may trigger a patch-download
            flow (FUN_0100b8a0 cmd 0x4c)
state 9 → CMD_GET_PLAYERLIST (svrtype="ACCOUNT")           — same as §4's post-login bootstrap
state 0xb → CMD_SET_LANGUAGE (svrtype="ACCOUNT", one field at DAT_0130ca04 — PROPOSED name "language")
state 0xc → done
```

**Emulator implication**: `CMD_AUTH_GAMEID` and `CMD_LOGIN` are two distinct, separately-addressable
calls, and the second one's credentials (`loginid`/`loginid_password`) are literally the first one's
response payload echoed back — your emulator's `CMD_AUTH_GAMEID` handler must mint whatever
`loginid`/`loginid_password` pair it wants the client to present to `CMD_LOGIN` next (a session
ticket, effectively — doesn't need to be anything more than an opaque token your `CMD_LOGIN` handler
recognizes). On repeat logins the client may skip `CMD_AUTH_GAMEID` entirely and replay a
**locally-cached** `loginid`/`loginid_password` from a previous session, so your `CMD_LOGIN` handler
needs to accept tickets it minted an arbitrary amount of time earlier, or force-fail them to make the
client re-run `CMD_AUTH_GAMEID`.

### 4.1 `CMD_AUTH_GAMEID` (VERIFIED request, `FUN_0061c360` case 2)

```
gameid="<string>"                  # local game-id (Windows-side identifier — CD-key/Konami ID?)
gameid_password="<string>"         # password for the above
password_type="PASS_PLAIN"|"PASS_AES"   # PASS_AES if a local encryption flag is set, else PASS_PLAIN
                                    #   — VERIFIED both literal strings exist in the binary
gameid_url_num=<n>
gameid_url=[<string>,...]          # array of URLs (likely login/reg-related redirect targets)
```
- **Errors (VERIFIED, 4, §0 methodology)**: `ERR_AUTHGID_URL`, `ERR_AUTHGID_SSLEXPIRE`,
  `ERR_AUTHGID_AUTH`, `ERR_AUTHGID_SSLETC`
- **Proposed response (success)**: `result="NOERR",rqid=<n>,loginid="<token>",loginid_password="<token>"`
  — this is the critical pair the client will cache and replay into `CMD_LOGIN` (§4.2). Treat both as
  opaque bearer tokens your server mints and later validates.

### 4.2 `CMD_LOGIN` (VERIFIED request, `FUN_006425c0`)

```
svrtype="ACCOUNT"
platform=<value>                # via FUN_0050ffb0 — PROPOSED "PC", not literally dumped
style="LOGINID"                 # constant — implies the same endpoint may accept other auth
                                 #   "styles" too (GAMEID/NPTICKET-direct?), only LOGINID observed in use
loginid="<string>"              # from CMD_AUTH_GAMEID's response, or replayed from local cache
loginid_password="<string>"     # paired with loginid, same source
use_regcode=<0|1>               # whether a PC registration code should be validated this login
regcode="<string>"              # the PC serial/registration code itself
patchver=<value>                # client game-data patch version -> ERR_DIFFERENT_PATCHVERSION
dlcver=<value>                  # installed DLC version
parahash=<value>                # hash of local "para" game-data files (integrity check)
entryhash=<value>               # hash of the launcher/entry executable (integrity check)
dont_check_heartbeat=<0|1>      # always observed as 0 (false) in this build
```
- **Security note**: no Blowfish/AES wrapping observed on `loginid_password` itself in this call —
  it goes out as a plain KV string value. The AES option (`PASS_AES`) applies to `gameid_password` in
  the *earlier* `CMD_AUTH_GAMEID` call, not here. Windows CryptoAPI imports (`CryptCreateHash`,
  `CryptHashData`, `CryptAcquireContextA`, `CryptGenRandom`) are present in the binary and almost
  certainly back the `parahash`/`entryhash` integrity hashes (MD5 most likely, matching this
  project's PES5/6 protocol convention per `CLAUDE.md`), not password confidentiality.
- **Proposed response (success)**:
  ```
  result="NOERR",rqid=<n>,account_first_login=<0|1>,accountid=<n>,...
  ```
  (`account_first_login` field name is VERIFIED — extracted explicitly by the client in state 8 above)
- **Errors (VERIFIED, 12 total, in binary order)**:

  | Error | Likely meaning |
  |---|---|
  | `ERR_NOTGETCCODE` | Couldn't obtain client/challenge code (pre-auth handshake step failed) |
  | `ERR_PARAMCHECK_FAILED` | Malformed request |
  | `ERR_INVALID_ACCOUNT` | Bad username/account |
  | `ERR_DIFFERENT_PATCHVERSION` | Client game-data version mismatch |
  | `ERR_DEFCLIENTVER` | Client build/version mismatch |
  | `ERR_DATABASE` | Backend DB error |
  | `ERR_LOGIN_FAILED` | Bad password / generic auth failure |
  | `ERR_ALREADYLOGGEDIN` | Account already has an active session |
  | `ERR_REGCODENOTISSUED` | No PC registration code on file |
  | `ERR_REGCODE_FAILED` | Registration code check failed |
  | `ERR_SERVERNOTREADY` | Server-side not ready (maintenance?) |
  | `ERR_REGCODEDIFLASTTIME` | Registration code differs from last recorded one |

> **Correction from the first pass**: `CMD_AUTH_GAMEID` is *not* dead PC code — it's the real
> first-stage auth call used on PC too (see §4.1 above). The name is misleading (sounds
> console/platform-specific) but it's actually PES2011's generic "get me a login ticket" endpoint.

#### `CMD_AUTH_NPTICKET` (PSN ticket auth — likely still dead on PC, unlike AUTH_GAMEID)
- **Errors (VERIFIED, 5)**: `ERR_GET_TICKETINFO`, `ERR_READ_PASSPHRASE`, `ERR_READ_CIPHERINFO`,
  `ERR_PREPARE_CIPHERINFO`, `ERR_INVALID_TICKET`. Same as above — likely shared cross-platform
  netcode leftover, safe to leave unimplemented for a PC-only emulator.

#### `CMD_SET_KONAMIID` / `CMD_GET_KONAMIID`
- Errors (VERIFIED): `SET`: `ERR_LOGIN_FAILED`, `ERR_ALREADYREGISTERED`. `GET`: `ERR_NOTREGISTERED`.
- **Request/response**: `PROPOSED` — `konami_id="<string>"` field, links the game account to a
  Konami ID (the `id.konami.net` web portal from §1.1).

#### `CMD_GET_PLAYERLIST` (VERIFIED request, from `FUN_0061b940` case 1 — this one **was** fully
decompiled, it's part of the post-login "profile bootstrap" flow)
```
svrtype="ACCOUNT"
```
- Response consumed via `FUN_0050f550`/`FUN_00511880` into a player/profile-slot structure —
  **PROPOSED** shape: `result="NOERR",rqid=<n>,player_num=<n>,player_list=[{playerid=<n>,name="<string>",...}]`
  (mirrors PES5/6's up-to-3-profiles-per-account model per this repo's own `sql/schema.sql`)

#### `CMD_GET_PRIVATEINFO` (VERIFIED request, `FUN_0061b940` case 3, runs right after `GET_PLAYERLIST`)
```
svrtype="ACCOUNT"
mypid=<player id from GET_PLAYERLIST>       # field name at DAT_0130c7f4, PROPOSED as "mypid" by
                                              #   analogy with the confirmed "mypid" field used in the
                                              #   very next command
```

#### `CMD_GET_DIVISIONUPDATE` (VERIFIED request, `FUN_0061b940` case 5)
```
svrtype="ACCOUNT"
mypid=<player id>
```
Ranked-division standing update, runs automatically after private info.

#### `CMD_CREATE_PLAYER`
- **Request (PROPOSED)**: `name="<string>"`, plus whatever else profile creation needs (nationality,
  favorite team, etc. — not recoverable statically)
- **Errors (VERIFIED, 4)**: `ERR_DATABASE`, `ERR_ALREADYEXISTS`, `ERR_INVALIDLETTER`,
  `ERR_NGWORDINPNAME` (NG = "no good" — profanity/reserved-word filter on player name)

#### `CMD_DEL_PLAYER`
- **Errors (VERIFIED, 3)**: `ERR_DATABASE`, `ERR_NOPLAYER`, `ERR_DELINDAYS` (probably "must wait N
  days before you can delete again" cooldown)

#### `CMD_SET_CURRENTPLAYER`
- **Errors (VERIFIED, 3)**: `ERR_REFUSED`, `ERR_DATABASE`, `ERR_NOPLAYER`

#### `CMD_SET_PLAYERPROFILE`
- **Errors (VERIFIED, 3)**: `ERR_INVALIDDATE`, `ERR_NGWORDININTRO` (profanity filter on profile
  "intro"/bio text), `ERR_DATABASE`

#### `CMD_GET_PLAYERINFO` / `CMD_GET_MATCHID`
- Both trivial — **Errors (VERIFIED, 1 each)**: `ERR_DATABASE`

#### `CMD_DISCONNECT` (main-session variant — distinct from gate's!)
- **Errors (VERIFIED, 1)**: `ERR_DATABASE` (a DB write happens on logout — session/audit close)

#### `CMD_SET_LANGUAGE` (VERIFIED request, `FUN_0061c360` case 0xb — new find, runs right after
`CMD_GET_PLAYERLIST` as the last step of login bootstrap)
```
svrtype="ACCOUNT"
<field>=<value>      # key name at DAT_0130ca04, PROPOSED "language", value sourced from param+0x3d8
```
- **Proposed response**: `result="NOERR",rqid=<n>`

---

## 5. LOBBY / MENU phase

### Background "watch" subscriptions (VERIFIED, `FUN_005213d0`)

Runs once, right after entering the main menu (post-login, post-profile-select). Registers four
push-notification channels the server can later use to proactively message the client:

```
CMD_WATCH_MAINTETIME     # maintenance-window broadcast
CMD_WATCH_EMERGENCY      # emergency/urgent message broadcast
CMD_WATCH_SHORTMAIL      # in-game mail notification
CMD_WATCH_IPANDPORT      # push updates to this client's own observed ip:port (NAT/STUN result?)
```
No parameters observed on any of the four (all sent via `FUN_00541eb0()` with an empty body before
`FUN_00541e80(name)`). **Proposed response**: `result="NOERR",rqid=<n>` for each — these are
subscribe-and-forget; the server later pushes unsolicited messages using these same command names
(this matches the `CMD_SEND_HEARTBEAT` heartbeat/push pattern seen in the binary's response-format
strings — treat all `CMD_WATCH_*`/`CMD_SEND_*` names as bidirectional: client subscribes once, server
pushes async messages with matching command name+payload afterward).

Same function also independently issues (`INFERRED` grouping, same source block):
```
CMD_GET_JOINEDCOMMULIST    # community/clan list the player belongs to (out of scope, see note below)
```

> **Scope note**: PES2011 has a large "COMMU" (community/clan) subsystem — leagues, rankings, member
> lists, ~150 of the 723 total `CMD_*` commands. It's out of scope for the flow you asked for
> (connect/gate/account/lobby/room/quickmatch/match) and isn't documented here beyond this note.
> Revisit if you want clan/league support later.

### Core lobby commands

#### `CMD_JOIN_LOBBY`
- **Request (PROPOSED)**: `lobbytype="<string>"` or similar — which lobby/division to enter
- **Errors (VERIFIED, 1)**: `ERR_LOBBYUSERMAX` (lobby is full)

#### `CMD_JOIN_BLOCK`
- **Errors (VERIFIED, 3)**: `ERR_BLOCKNOTEXIST`, `ERR_BLOCKISFULL`, `ERR_DIVISIONREFUSED`
- ("Block" = a sub-partition of a lobby, standard PES matchmaking-lobby structure — same concept as
  fiveserver-go's `Lobby`/`Room` split.)

#### `CMD_GET_BLOCKLIST`
- **Errors (VERIFIED, 1)**: `ERR_DATABASE`

#### `CMD_REQWATCHL_BLOCKPLAYERLIST`
- **Errors (VERIFIED, 1)**: `ERR_NOBLOCKBELONG` (not currently in a block)
- Subscribes to live updates of who's in your current block — same subscribe/push pattern as §5's
  watch commands.

#### `CMD_SEARCH_PLAYER`
- **Errors (VERIFIED, 1)**: `ERR_TOOMANYRECORDS`

#### `CMD_SEND_TEXTCHAT`
- **Errors (VERIFIED, 6)**: `ERR_CHATENABLE` (chat disabled for this player), `ERR_ISNOTGM`,
  `ERR_TARGETINGAME`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`, `ERR_PLAYERISNOTGAMER`

#### `CMD_SEND_FRIENDREQ`
- **Errors (VERIFIED, 5)**: `ERR_DATABASE`, `ERR_INFRIENDLIST` (already friends), `ERR_FRIENDCOUNTFULL`,
  `ERR_INBLACKLIST`, `ERR_FRIEND_PERMISSION_WAITING_STATE`

#### `CMD_ADD_BLACKLIST`
- **Errors (VERIFIED, 4)**: `ERR_INFRIENDLIST`, `ERR_PLAYERISONESELF`, `ERR_INBLACKLIST`,
  `ERR_FRIENDCOUNTFULL`

#### `CMD_SEND_SHORTMAIL`
- **Errors (VERIFIED, 1)**: `ERR_NOTFOUNDCLIENT` (recipient offline/not found)

#### `CMD_GET_WEEKLYRANKINGDATELIST`
- **Errors (VERIFIED, 1)**: `ERR_WEEKLYRANKINGNOTFOUND`

#### `CMD_CHECK_STRING`
- Profanity/NG-word check used before room names, player names, chat, etc.
- **Errors (VERIFIED, 1)**: `ERR_INVALIDLETTER`

---

## 6. ROOM phase

**Fourth-pass update — this section was rewritten from scratch.** Found the real
`CMD_CREATEJOIN_ROOM` builder (`FUN_005e5c20`), the real join/P2P-connect state machine
(`FUN_005e4d60`), two more commands (`CMD_LEAVE_ROOM`, `CMD_WATCH_ROOMSTATE`,
`CMD_REQWATCHL_ROOMLIST`, `CMD_WATCH_FORCELEAVEROOM`), and — by reading the master enum table
(§0) — the **exact room-status and match-phase enum strings**. This is now the most solidly verified
phase in the whole document after §9 (in-match).

### 6.0 Full verified sequence

```
HOST                                          SERVER                         GUEST(S)
────                                          ──────                         ────────
CMD_CREATEJOIN_ROOM ─────────────────────────▶
  (name, match_type, team_flag[], lang,
   max_players, is_invite_limit, password,
   enable_guest, legends_playstyle,
   is_legends_vs_player, team_name,
   short_teamname)
                                    room created, host auto-joined,
                                    status=WAITING, room_id assigned
                              ◀──────────────── result="NOERR", room_id=<n>, ...

CMD_REQWATCHL_ROOMPLAYERLIST(room_id) ───────▶     subscribe to roster push
CMD_WATCH_ROOMSTATE(?) ──────────────────────▶     subscribe to status push
                                                                          CMD_JOIN_ROOM ─────▶
                                                                            (room_id, is_invited,
                                                                             password)
                                              ◀──────────────────────────────────────────────
                                    roster push: roomplayer_list updated (see §6.2)
                              ◀────────────────────────────────────────────▶ (both get the push)

  (any member) CMD_SET_GAMEMEMBERENV, CMD_SET_GAMETEAM, CMD_SET_GUEST, CMD_SET_GAMEPLAN
  as players configure teams/settings — server should push updated roomplayer_list/roomstate
  after each

                                    when the host/server decides to start:
                                    push CMD_UPDATE_ROOMSTATE status=SETENV
                              ◀────────────────────────────────────────────▶

  each client polls its LOCAL cache of the last-pushed room state (fed by the WATCH_ROOMSTATE
  push above — no explicit poll command found; it's push-driven) until it flips "ready"

CMD_GET_IPANDPORT(pid=<host's player id>) ───▶     resolve host's connect target
                              ◀──────────────── result="NOERR", ipaddr="x.x.x.x", port=<n>

  client attempts a direct P2P connect (UDP, STUN-assisted per §1/§8) to that ip:port
  on success  → status effectively becomes GAME, hand off into the §9 in-match driver
  on failure  → retries the GET_IPANDPORT/connect cycle, or gives up → CMD_LEAVE_ROOM

  ... match plays out (§9) ...
                                    push CMD_UPDATE_ROOMSTATE status=RESULT / RESULTWAIT

CMD_LEAVE_ROOM ───────────────────────────────▶    (on exit, cancel, or failure — any member)
                              ◀──────────────── result="NOERR"
```

**Emulator implication — this is the load-bearing part**: the room-ready signal that triggers the
client's P2P connect attempt is **entirely server-push-driven**. There is no explicit "start match"
command the client sends — it's watching its locally-cached copy of the last `CMD_UPDATE_ROOMSTATE`/
`CMD_WATCH_ROOMSTATE` push for a specific status transition. Your emulator needs to *decide* when a
room is ready (all required slots filled + ready-flagged, presumably) and then **push** the state
change proactively, the same way `CMD_WATCH_MAINTETIME` etc. are push channels in §5. Don't wait for
a request that will never come.

### 6.1 Room status enum (VERIFIED, read directly from the master enum table, type id `0x44`)

Used in `CMD_UPDATE_ROOMSTATE`'s `status` field (§9 event types 6/7) and presumably in
`CMD_WATCH_ROOMSTATE` pushes:

| Value | Meaning |
|---|---|
| `WAITING` | Lobby — players joining/leaving, room open |
| `SETENV` | Configuring — teams/settings being finalized, about to start |
| `GAME` | Match in progress |
| `RESULT` | Showing match result |
| `DISCONWAIT` | Waiting on a disconnect to resolve (a player dropped mid-flow) |
| `RESULTWAIT` | Waiting for result confirmation/acknowledgment from participants |

### 6.2 Room roster shape (VERIFIED, `FUN_006407f0` — a response parser, gives real field names for
both directions):

```
room_id=<n>
room_name="<string>"
roomplayer_list_num=<n>
roomplayer_list=[
  {
    room_player_id=<n>,
    room_entry_no=<n>,
    game_entry_no=<n>,
    desiredPosition=[<n>,<n>,...],    # up to 13 entries (0..0xc) — per-position preference/rating
    gameplayer_name="<string>"        # up to 0x30 bytes, strncpy'd
  },
  ...
]
```

#### `CMD_REQWATCHL_ROOMPLAYERLIST` (VERIFIED request, new find — subscribe to live room roster
updates, same subscribe/push pattern as §5)
```
room_id=<n>
```
- **Proposed response**: `result="NOERR",rqid=<n>` to the subscribe call itself; the server then
  pushes `roomplayer_list` updates (shape above) under the same command name as unsolicited messages.

#### `CMD_CREATEJOIN_ROOM` — VERIFIED request (`FUN_005e5c20`, exact field bytes read from `.rodata`)

```
name="<string>"                     # room display name — note: "name", NOT "room_name" (request
                                     #   and roster-push use different keys for the same concept)
match_type="OC_FREE"|"OC_RANKING"|"LEGENDS"|"COMMU"|"COMMU_LEAGUE"
                                     #   selector: 0→OC_RANKING, 2→LEGENDS, 3→COMMU, 4→COMMU_LEAGUE,
                                     #   anything else (incl. 1) → OC_FREE (the default case)
team_flag=[...]                     # array, built via a generic [item,item,...] serializer —
                                     #   exact element shape not resolved, likely per-slot team
                                     #   availability
lang="EN"|"FR"|"GR"|"IT"|"SP"|"JP"|"KR"|"US"|"PR"|"MX"|"RU"|"HE"|"SV"|"NL"|"BR"|"TR"|"CH"
                                     # VERIFIED enum (§7 has the full derivation) — same table as
                                     #   qmatchsetting's opp_lang, minus the "ANY" wildcard
max_players=<n>                     # internally defaults to 2 for OC_RANKING, 4 for every other
                                     #   match_type — client-suggested, not necessarily authoritative
is_invite_limit="YES"|"NO"
password="<string>"
enable_guest="YES"|"NO"
legends_playstyle="EVERYONE"        # constant in this build — LEGENDS-mode specific, likely has
                                     #   other values (vs-CPU-only? vs-legends-only?) not observed
is_legends_vs_player="YES"|"NO"
team_name="<string>"
short_teamname="<string>"
```
- **Proposed response (success)**: `result="NOERR",rqid=<n>,room_id=<n>` — the room is created *and*
  the host is auto-joined in one call (hence "CREATEJOIN"), status starts at `WAITING` (§6.1).
- **Errors (VERIFIED, 4)**: `ERR_INVALIDARG`, `ERR_ALREADYINROOM`, `ERR_NGWORDINRNAME`,
  `ERR_ROOMTOOMANY` (server-side room count limit reached)

#### `CMD_JOIN_ROOM` — VERIFIED request (`FUN_005e4d60` case 1)

```
room_id=<n>
is_invited="YES"|"NO"
password="<string>"
```
- **Proposed response (success)**: `result="NOERR",rqid=<n>` — client then relies on the roster/state
  push channels (§6.2, §6.1) rather than reading a room snapshot back from this response directly.
- **Errors (VERIFIED, 9 — the richest room error set)**: `ERR_DATABASE`, `ERR_ROOMNOTFOUND`,
  `ERR_INVALIDARG`, `ERR_ALREADYINROOM`, `ERR_TOOMANYMEMBERS`, `ERR_PASSWDINCORRECT`,
  `ERR_DIFFDCVERSION` (game-data/DLC version mismatch between host and joiner),
  `ERR_ROOMISFULL`, `ERR_ROOMSTATNOTMATCH` (room already mid-match / not in "open" state)
- **Client-side retry behavior** (VERIFIED, `FUN_005e4d60` case 2): if `ERR_ROOMNOTFOUND` is returned
  in a specific internal retry-mode, the client just aborts back to the room browser rather than
  retrying — no auto-retry-on-not-found loop.

#### `CMD_LEAVE_ROOM` — VERIFIED (`FUN_005e4d60` case 0xc, new find, no request fields)
Sent with an empty body on: explicit user-initiated leave, P2P connect failure/timeout after
exhausting retries, or any unrecoverable error in the join/connect flow.
- **Proposed response**: `result="NOERR",rqid=<n>`

#### `CMD_WATCH_ROOMSTATE` — VERIFIED request (`FUN_0053cee0`, fifth pass): **empty body**, no fields
at all. Sent once, globally, when the client's local context enters `"MENU"` (an internal state
check against the literal `"MENU"`, tying back to the `MENU` svrtype bucket from §2 — this really is
the "I'm in the menu/lobby area" signal). This is a blanket subscription, not scoped to a specific
`room_id` — the server presumably scopes pushes to whatever room the subscribing session is actually
in at the time (or is about to join), rather than the client declaring a room upfront.
- On teardown, the client resets by sending a `"NONE"` value through the same channel machinery
  (state 1's failure path) — likely an implicit "unsubscribe"/reset rather than a distinct command.
- **Proposed response/push shape**: `result="NOERR",rqid=<n>,status="WAITING"|"SETENV"|"GAME"|
  "RESULT"|"DISCONWAIT"|"RESULTWAIT"` (§6.1 enum) — this is almost certainly the actual push that
  drives §6.0's "room ready" client-side check.

#### `CMD_WATCH_FORCELEAVEROOM` — VERIFIED command name (new find)
Server-initiated push telling a client it's been force-removed from its room (disband, kick,
server-side cleanup). Treat as a push-only channel — no client request fields expected beyond the
initial subscribe.

#### `CMD_REQWATCHL_ROOMLIST` — VERIFIED request (`FUN_005f6f80`, fifth pass): **empty body**, no
fields. Always sent as a **pair** together with `CMD_REQWATCHL_BLOCKPLAYERLIST` in the same breath —
both fire together on entering a block (§5) and again together on a reconnect/refresh flag. This
confirms the room list is scoped to **whichever block the player most recently joined** (`CMD_JOIN_BLOCK`,
§5) rather than taking an explicit filter — the client doesn't declare "give me rooms for block X",
it declares "I'm now watching block X" (via `CMD_JOIN_BLOCK`) and this pair of subscriptions rides on
top of that context. **Proposed** push shape mirrors `CMD_GET_SVRLIST`'s array pattern:
`result="NOERR",rqid=<n>,room_list=[{room_id=<n>,room_name="<s>",...}]`.

#### `CMD_GET_IPANDPORT` (room-scoped usage — VERIFIED request, `FUN_005e4d60` case 5)
```
pid=<player id>              # resolved from the room roster — observed use is fetching the HOST's
                              #   connect target so a joining client can P2P-connect to them
```
Called repeatedly in a poll loop (see §6.0 sequence) until the room signals ready via the state-push
channel, not just once. **Proposed response**: `result="NOERR",rqid=<n>,ipaddr="x.x.x.x",port=<n>`.

#### `CMD_CHANGE_ROOMNAME`
- **Errors (VERIFIED, 6)**: `ERR_NOTINROOM`, `ERR_NOTROOMOWNER`, `ERR_INVALIDARG`,
  `ERR_NGWORDINRNAME`, `ERR_ROOMNOTFOUND`, `ERR_DATABASE`

#### `CMD_KICK_ROOMMEMBER`
- **Errors (VERIFIED, 5)**: `ERR_NOTINROOM`, `ERR_NOTROOMOWNER`, `ERR_ROOMSTATNOTMATCH`,
  `ERR_TARGETPLAYERNOTEXIST`, `ERR_PLAYERISONESELF` (can't kick yourself)

#### `CMD_SET_GUEST` (adds a local/offline "guest" slot to your room entry — couch co-op guest)
- **Errors (VERIFIED, 7)**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`,
  `ERR_ROOMISFULL`, `ERR_TOOMANYGUEST`, `ERR_NOGUEST`, `ERR_CANTADDGUEST`

#### `CMD_SET_XSESSION`
- **Errors (VERIFIED, 3)**: `ERR_NOTINROOM`, `ERR_NOTROOMOWNER`, `ERR_ROOMNOTFOUND`
- (Xbox Live session interop — likely dead on PC, implement only if needed.)

#### `CMD_SET_GAMEMEMBERENV` (VERIFIED request wiring from in-match driver, §7 event type 0xb)
```
<serialized env blob>            # FUN_00541230(...) then FUN_00541fd0(...) — opaque structure, not
                                  #   individual KV fields; likely a packed settings blob (controller
                                  #   config / player environment), PROPOSED to pass through opaquely
```
- **Errors (VERIFIED, 5)**: `ERR_ROOMSTATNOTMATCH`, `ERR_NOTGAMEHOST`, `ERR_MSGFORMAT`,
  `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`

#### `CMD_SET_GAMEMEMBER`
- Sent with an empty body per §7 event type 0xc (`FUN_00541eb0()` then straight to send) — probably a
  simple "ready"/"member confirmed" ping. **Errors**: not isolated (shares the room-state error family
  above; no dedicated error block found nearby)

#### `CMD_UPDATE_ROOMSTATE` (VERIFIED request, §7 event types 6/7)
```
status=<value>                   # room lifecycle status enum, PROPOSED values: "OPEN"/"FULL"/
                                  #   "INGAME"/"CLOSED" by analogy with fiveserver-go's Room states
```

#### `CMD_SET_GAMETEAM` (VERIFIED request, §7 event type 5)
```
home_team_id=<n>
away_team_id=<n>
```
- **Errors (VERIFIED, 1)**: `ERR_NOTINROOM`

#### `CMD_SET_GAMEPLAN`
- **Errors (VERIFIED, 1)**: `ERR_DATABASE`

---

## 7. QUICK MATCH phase

#### `CMD_CANCEL_DECIDE_QUICKMATCH`
- **Errors (VERIFIED, 1)**: `ERR_ROOMNOTFOUND`

#### `CMD_START_QUICKMATCH` — the big one, 12+ distinct outcomes, forming a clear validation pipeline

**Deep-dive update**: the exact sender still wasn't isolated, but its settings-payload builder
(`FUN_005ea980`) was — it constructs a nested `qmatchsetting` object that's almost certainly attached
directly to this request. Field names below are `VERIFIED` from that function; how they nest under
`CMD_START_QUICKMATCH` specifically is `INFERRED` (very high confidence — `qmatchsetting` as a key
name was also found as a literal alongside `CMD_START_QUICKMATCH` region errors in §0's methodology
zone).

```
qmatchsetting={
  player_num=<n>,
  game_time="5MINUTES"|"10MINUTES"|"15MINUTES"|"20MINUTES"|"25MINUTES"|"30MINUTES",
                                 # VERIFIED enum (fifth pass, master table read directly from memory)
  opp_lang="EN"|"FR"|"GR"|"IT"|"SP"|"JP"|"KR"|"US"|"PR"|"MX"|"RU"|"HE"|"SV"|"NL"|"BR"|"TR"|"CH"|"ANY",
                                 # VERIFIED enum, 17 language/region codes + "ANY" wildcard (no
                                 #   filter) -> feeds ERR_CHECK_LANG. Same enum is almost certainly
                                 #   what `CMD_CREATEJOIN_ROOM`'s `lang` field (§6) takes too (minus
                                 #   "ANY", since a room has one language, not a filter)
  is_chat=<0|1>,
  has_guestplayer=<0|1>,        # -> feeds ERR_CHECK_GUEST
  frrate=<value>,               # "free-run rate"? gameplay difficulty/assist-adjacent setting
  # --- mode-dependent block, mode selected by an internal match-type code (0-8 observed) ---
  # when mode == 2 ("fd" — Free Direct / ranked-adjacent? ):
  fd_is_level=<0|1>,
  fd_position=<value>,
  fd_posflag=<bitmask>,         # 13-bit mask, one bit per position slot (0..0xc)
  # otherwise ("oc" — Online Club?):
  oc_is_rating=<0|1>,
  oc_is_pcspec=<0|1>,           # -> feeds ERR_CHECK_CPU
  # --- always present ---
  is_coach_mode=<0|1>,
  ml_is_division=<0|1>,         # Master League division-linked match
}
```
The match-type code (0–8) selects between at least: ranked 1v1, ranked variants, "fd" (2 modes), an
"oc" (Online Club?) variant with/without chat (5/6), and two more (7/8) — exact names not recovered,
but the settings differences above are real. `NAT_TYPE_*` classification (§1.1) and blacklist checks
happen server-side against the requester and the candidate, not client-supplied fields.

- **Errors (VERIFIED, expanded — 15 distinct across the two error zones found)**:

  | Error | Likely meaning |
  |---|---|
  | `ERR_GETPLAYERFAIL` | Couldn't resolve the requesting player |
  | `ERR_QMATCH_STARTMATCHING` | (Informational state, not necessarily failure — "matching has started") |
  | `ERR_GETPLAYERFAIL` | (repeats — appears twice in the table, once per candidate lookup pass) |
  | `ERR_PING_FAILED` | Latency probe to candidate opponent failed |
  | `ERR_PLAYERNOTEXIST` | Matched candidate no longer exists/online |
  | `ERR_REFUSED` | Candidate declined |
  | `ERR_QMATCH_CONNECTFAILED` | P2P connect-back failed |
  | `ERR_CHECK_LANG` | Language/region mismatch |
  | `ERR_CHECK_GUEST` | Guest-related eligibility failure |
  | `ERR_CHECK_CPU` | CPU/performance capability mismatch |
  | `ERR_CHECK_NATTYPE` | NAT type incompatible for P2P (symmetric NAT on both sides, etc.) |
  | `ERR_CHECK_BLACKLIST` | One side has blacklisted the other |
  | `ERR_QMATCH_NOTICE` | (informational push, not necessarily a hard failure) |
  | `ERR_QMATCH_CANCELED` | Matchmaking was canceled (by either side, or `CMD_CANCEL_DECIDE_QUICKMATCH`) |
  | `ERR_QMATCH_PRECONNECT` | Pre-connection handshake step failed, ahead of the full P2P connect |

  (The last 3 were found in a second, separate `ERR_QMATCH_*` cluster — `01308314`-`01308404` and its
  duplicate at `013ba724`-`013bb070` — distinct from the main 12-error validation-pipeline block,
  likely async/push-style notifications rather than direct request-response errors.)

- **Proposed response (success)**: `result="NOERR",rqid=<n>,opponent_pid=<n>,ipaddr="x.x.x.x",port=<n>`
  — quickmatch's whole point is handing back a P2P peer to connect to (see §8 `CMD_GET_IPANDPORT` /
  `CMD_SEND_P2PFAILED`, which are clearly the STUN-assisted P2P connect step that follows).

#### `CMD_UPDATE_COMBINATION`
- **Errors (VERIFIED, 1)**: `ERR_NOTINROOM`
- (VERIFIED request from §9's in-match driver too — see there, it's actually used both during
  matchmaking setup and mid-match, field names `total_comb`/`max_comb`.)

---

## 8. P2P handoff (STUN-adjacent, sits between quickmatch/room and actual match)

**Deep-dive update**: found the field names for the P2P status-push channel — `p2p_player_num`,
`p2p_status`, `p2p_event` (VERIFIED literals, all three clustered together in `.rodata`, consumed by
`FUN_005e1d10`/`FUN_005e22e0`). `p2p_status` almost certainly carries one of the `NAT_TYPE_*` values
from §1.1's `mrdUPnP` enum once STUN detection completes for a peer.

```
result="NOERR",rqid=<n>,p2p_player_num=<n>,p2p_status=<value>,p2p_event=<value>
```

#### `CMD_GET_IPANDPORT` / `CMD_WATCH_IPANDPORT`
- No dedicated `ERR_` block located near either — likely reuse the room/generic error family.
- **Proposed response**: `result="NOERR",rqid=<n>,ipaddr="x.x.x.x",port=<n>` — this is almost
  certainly how the client learns its own STUN-observed public endpoint to hand to the opponent (or
  vice versa) for the P2P match connection, consistent with the `mrdUPnP_STUN` NAT-type-detection
  code found separately in §1.

#### `CMD_SEND_P2PFAILED`
- Client-initiated: reports that the direct P2P connect attempt failed, presumably triggering a
  server-mediated fallback or match cancellation. No dedicated error block found; **proposed**
  response is a simple ack: `result="NOERR",rqid=<n>`.

---

## 9. IN-MATCH phase

This is the most solidly reverse-engineered phase — `FUN_005fa030` is a complete, fully-decompiled
event-driven state machine that fires once the match actually starts. Structure: state 0 subscribes
to four push channels, state 1 polls a local event queue (populated by the game engine simulating the
match) and translates each event into one outbound command.

### Subscriptions (state 0, VERIFIED, no params)
```
CMD_WATCH_ABNORMALEND
CMD_WATCH_FORFEITEDGAMEEND
CMD_WATCH_INJURYGAMEEND
CMD_WATCH_OWNGOALGAMEEND
```
Same subscribe/push pattern as §5 — the server can push these commands unsolicited to notify of a
forfeit/injury/etc. reported by the *other* peer in a P2P match.

### Event → command dispatch (VERIFIED, from the local event-queue switch)

| Event type | Command | Fields (VERIFIED names) |
|---|---|---|
| 0 | `CMD_CHANGE_GAMEPHASE` | `phase=<value>` (VERIFIED enum, type id `0x1a` in the master table, §0 — see below) |
| 1 | `CMD_SET_INJURYGAMEEND` | *(opaque, one field via `DAT_0130968c`)* |
| 2 | `CMD_UPDATE_COMBINATION` | `total_comb=<n>`, `max_comb=<n>` |
| 3 | `CMD_ADD_SCORE` | `goal_side=<value>`, `goal_gameplayer_id=<n>`, `goal_player_side=<value>`, `assist_side=<value>`, `assist_gameplayer_id=<n>`, `assist_player_side=<value>`, `goal_player_pid=<n>` |
| 4 | `CMD_ADD_FOUL` | `card_pid=<n>`, `card_side=<value>`, `card_gameplayer_id=<n>`, `card_kind=<value>`, `injury_side=<value>`, `injury_gameplayer_id=<n>`, `injury_all=<n>`, `injury_one=<n>`, plus a nested struct with `symptom`, `level`, `level_no_self_conscious` |
| 5 | `CMD_SET_GAMETEAM` | `home_team_id=<n>`, `away_team_id=<n>` |
| 6 / 7 | `CMD_UPDATE_ROOMSTATE` | `status=<value>` |
| 8 / 10 | `CMD_SEND_ABNORMALEND` | `is_sync_failed=<bool>` |
| 9 | `CMD_SET_FORFEITEDGAMEEND` | *(no extra fields observed)* |
| 0xb | `CMD_SET_GAMEMEMBERENV` | opaque env blob (see §6) |
| 0xc | `CMD_SET_GAMEMEMBER` | *(empty body)* |
| 0xd | `CMD_SET_COMMU_MATCHINFO` | `possession_average_h=<n>`, `shot_h=<n>`, `suffer_shot_h=<n>`, `possession_average_a=<n>`, `shot_a=<n>`, `suffer_shot_a=<n>` (community/league match stats — only fires if the match belongs to a COMMU league) |
| 0xe | `CMD_SEND_ML_GAMEPLAYERS` | `gameplayer_id_list_num=<n>`, `gameplayer_id_list=[<n>,<n>,...]` (Master League squad sync) |

State 2 (after sending) just waits for `result="NOERR"` and loops back to state 1 to poll the next
queued event; on failure it retries or aborts depending on context (`FUN_00521850`/state jumps).

### `phase` enum (VERIFIED, read directly from the master enum table, type id `0x1a`)

11 values, in order — this is a standard football match structure and doubles as a good state model
for the emulator's authoritative match clock if you want one:

```
ENTRY → 1ST → BREAK1 → 2ND → BREAK2 → EX1ST → BREAK3 → EX2ND → BREAK4 → PK → END
```
(`ENTRY`=pre-kickoff, `1ST`/`2ND`=regulation halves, `BREAK1`=halftime, `BREAK2`=end of regulation
before extra time, `EX1ST`/`EX2ND`=extra-time halves, `BREAK3`/`BREAK4`=extra-time breaks,
`PK`=penalty shootout, `END`=match over.)

Also present but not part of the per-event dispatch table above:
```
CMD_REQ_FORFEITEDGAME_REPLY   # sent when the local player is asked to confirm/deny a forfeit claim
```

### Errors seen in this zone (VERIFIED, address-correlated)
- `CMD_SET_FORFEITEDGAMEEND`: `ERR_NOTINROOM`
- `CMD_SET_INJURYGAMEEND`: `ERR_NOTINROOM`
- `CMD_SET_GAMEPLAN`: `ERR_DATABASE`
- `CMD_SET_GAMETEAM`: `ERR_NOTINROOM`

### Proposed responses
All in-match commands are fire-and-forget from the client's perspective (state machine doesn't
branch heavily on response content beyond NOERR/not) — a safe minimal emulator response is:
```
result="NOERR",rqid=<n>
```
for every command in this phase, echoing back any fields the client would reasonably want confirmed
(e.g. echo `phase` on `CMD_CHANGE_GAMEPHASE`, echo score totals on `CMD_ADD_SCORE` if you want to
support authoritative server-side score tracking rather than trusting the P2P peers).

---

## 10. Result/error code reference (consolidated)

Full `ERR_XXX` enum dump from the binary (1366 total occurrences found; the ~200 most relevant to
this flow are attributed to specific commands above). Errors not attributed above belong to the
out-of-scope COMMU/league/Master-League-market subsystems (`ERR_COMPE_*`, `ERR_TEAM_NOTFOUND`,
`ERR_INDEX_OUTOFRANGE`, `ERR_PLAYERNOTINCOMPE`, etc.) and aren't needed for the requested flow.

**Universal success marker**: `result="NOERR"` — every single response envelope, gate or
main-session, uses this exact literal on success.

**Gate-layer failure marker**: `SvrTypeErr` (only one found; gate doesn't use the `ERR_XXX` scheme).

**Main-session failure marker**: `result="ERR_XXXXX"` from the table in §0, per-command lists in §4–§9.

---

## 11. Open questions / next steps for the emulator

Resolved in the second pass (kept here for traceability): `CMD_LOGIN`'s exact fields (§4.2, fully
verified — it goes through a hand-written sender, `FUN_006425c0`, not the generic dispatcher as
originally guessed), password handling (plaintext KV value, no Blowfish wrapping observed — §4.2
security note), STUN local port range (§1.1), `svrtype` enum values (§2 update — but see caveat
below, it turned out to be a runtime-variable field rather than a fixed enum for `GET_SVRLIST`
specifically), and `qmatchsetting`'s field shape (§7).

Still open:

1. **Wire framing — RESOLVED (sixth pass).** See §1.4. The frame is 24 bytes of
   `[u16 id][u16 len][u32 counter][16B MD5(header[0..7] || body)]` plus the body, XORed with
   `5B9F2E64` aligned to the frame start, with a 0x400-byte cap on the length field; body
   id `0x0060` is Blowfish-ECB over `[len][len][flag][ASCII KV]`. The reason three passes of
   cross-reference chasing missed it: `ws2_32.dll` really is imported by ordinal, so following
   `send()` backwards does not work. The way in was the *reader* (`FUN_01006660`), reached from
   `FUN_01011540`, plus the two key blobs found by byte-pattern search (`0x01569084`,
   `0x0160c6b0`). Working from the crypto keys rather than from the socket API is the reusable
   lesson here.
   One sub-item remains open: the *encrypt* side of the 0x0060 inner body was not re-derived
   for this binary (§1.4, "Text-command body").
2. Whether the account/session server is a genuinely separate TCP endpoint from gate, or the same
   process on a second port — `FUN_0050cfd0("ACCOUNT")` (called during login, §4.0 state 4) reads as
   an explicit "reconnect to the ACCOUNT-typed server" call, which supports "separate connection" over
   "same socket," but the actual new endpoint's IP:port source (presumably cached from an earlier
   `CMD_GET_SVRLIST` response) wasn't traced.
3. STUN **server** port (as opposed to the client's own local bind range, now confirmed at 5730-5740)
   is set via `mrdUPnP_CMD_SET_STUN_SERV_PORT` at runtime, not a compile-time constant — can't be
   statically pinned; likely arrives via a gate/account server response field not yet identified.
4. The exact match-type codes (0–8) for `CMD_START_QUICKMATCH`'s `qmatchsetting` mode selector, and
   the precise wiring of `qmatchsetting` as a sub-object of `CMD_START_QUICKMATCH` vs. a sibling field
   — `INFERRED`, not 100% confirmed by a single decompiled call site.
5. `CMD_CREATEJOIN_ROOM`/`CMD_JOIN_ROOM`'s own request-builder functions — still not isolated (same
   "goes through the generic dispatcher" situation `CMD_LOGIN` was originally in); `room_id`/
   `room_name` field names are now verified via a response-parser instead (§6).
6. **New lead from the third pass**: `CMD_GET_SVRLIST` can return an `AUTOLOGIN`-typed server (§2),
   which strongly implies a fast-path reconnect flow that skips the full `CMD_AUTH_GAMEID` →
   `CMD_LOGIN` sequence in §4 for returning sessions. Not traced yet — worth a dedicated pass if
   "remember me"/quick-reconnect behavior matters for the emulator (find what connects to an
   `AUTOLOGIN`-typed server and what it sends there instead of `CMD_LOGIN`).
7. `VDPCHAT` (§2) — recognized by the client but not stored by the parser inspected; unclear whether
   it's genuinely unused in this build or handled by code outside `FUN_00ffd160`. Low priority unless
   voice chat matters for the emulator.

Resolved in the fourth pass (room deep-dive): `CMD_CREATEJOIN_ROOM`/`CMD_JOIN_ROOM`/`CMD_LEAVE_ROOM`
request fields (§6, all now VERIFIED), room-status enum (§6.1), match-phase enum (§9), and the
critical discovery that **all boolean fields protocol-wide are `"YES"`/`"NO"` strings**, not numeric
(§0) — retroactively fixes every `<0|1>` placeholder elsewhere in this document.

New from this pass:

8. `team_flag`'s array element shape (`CMD_CREATEJOIN_ROOM`) — confirmed to be an array via the
   generic `[x,y,...]` serializer, but individual element contents weren't traced.
9. `CMD_REQWATCHL_ROOMLIST`'s and `CMD_WATCH_ROOMSTATE`'s exact request/response field names —
   command names are VERIFIED, payloads are `PROPOSED` by symmetry only.
10. The room-ready trigger condition server-side is now clearly "push-driven" (§6.0), but the exact
    *client-side* trigger — what specific state-push field flip makes `FUN_005e4860`/`FUN_005e4920`
    return "go" — wasn't fully traced into the push-handler that populates their backing structures.
    An emulator can reasonably guess "push `status=SETENV` once all required players are ready" and
    iterate from there.

**Fifth pass results:**

- `CMD_WATCH_ROOMSTATE` and `CMD_REQWATCHL_ROOMLIST` — both now VERIFIED as **empty-body**
  subscriptions (§6), resolving item 9 above: no hidden request fields, they're scoped implicitly by
  current menu/block context rather than taking explicit filters.
- `opp_lang`/`lang` and `game_time` enums — both fully VERIFIED (§6, §7) via the same master-table
  technique that cracked room status and match phase.
- **`AUTOLOGIN` (item 6, previous pass) — dead end, reported honestly**: searched for every
  occurrence of the literal `"AUTOLOGIN"` in the binary; the only one beyond the `CMD_GET_SVRLIST`
  response-parser bucket (§2) is a second copy in the same parser region with no distinct owning
  function — i.e. no separate "connect to an AUTOLOGIN server and do X" code path was found
  referencing it by name. Either such a flow exists but addresses the bucket by numeric index rather
  than string (untraceable this way), or `AUTOLOGIN` is accepted-but-unused in this build, same
  situation as `VDPCHAT`. Downgrading this from "promising lead" to "probably not worth chasing
  further" unless you find contrary evidence.
- **Wire framing (item 1) — still open, and now understood *why* it's resisted three separate
  attempts**: `ws2_32.dll` is imported **by ordinal**, not by name (confirmed in the raw import
  table — `send`/`recv`/`socket`/etc. all show `original_imported_name: "Ordinal_N"`). Ghidra's
  cross-reference graph doesn't resolve call sites for ordinal-only imports the way it does for named
  ones, which is why every attempt to walk backward from `send()` to its caller came up empty through
  this toolset (direct xref lookup, bulk xref lookup, and an API-call-chain analyzer all returned
  nothing). This isn't a dead end from lack of trying — it's a tooling limitation for this specific
  binary. Fastest remaining path is almost certainly a live packet capture (Wireshark against a real
  client hitting anything that completes a TCP handshake, even your own placeholder listener) rather
  than more static analysis.
