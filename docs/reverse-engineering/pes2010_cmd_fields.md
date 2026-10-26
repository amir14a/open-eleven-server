# pes2010.exe — Session Flow & Verified Command Fields (Pass 2)

Companion to [`pes2010_cmd_commands.md`](./pes2010_cmd_commands.md) (pass 1: full command inventory +
input/output direction). This rewrite replaces an earlier version of this file that leaned on mining
adjacent strings in `.rdata` — that approach produced noisy, unordered, low-confidence output with no
sense of sequence. This version is grounded in **decompiled code from the actual call sites**, and it's
organized around the question that actually matters: *if I send command X, what happens next?*

## How this was derived

The whole client funnels every network send through one wrapper, `FUN_00768e50`. It has exactly **123
distinct caller functions** across the game's code — each one is the real game/UI logic that builds one
specific command's request and (usually, right there or in a paired poll loop) reads its response. All 123
were decompiled. Per call site this gives:

- The literal **request keys** — string literals passed to the option-setters (`FUN_00768ea0` = set string
  field, `FUN_00768ee0`/`f00` = set int/enum field, `FUN_00768f60` = set list field, `FUN_00768fa0` = merge
  a pre-built sub-object).
- The literal **response keys actually read** — via `FUN_00753250` (string), `FUN_00753450` (scalar),
  `FUN_00753550` (int), and `FUN_00753950` (compare a field, almost always `"result"`, against a literal
  like `NOERR`/`ERR_*`).
- What gets sent **next**, on success and on failure — this is what makes the flow section below possible.

Where a field's exact direction or provenance couldn't be pinned down from the decompile, that's called out
explicitly rather than guessed — see [Known gaps](#known-gaps) at the bottom.

---

## Session flow

The client moves through named server contexts (`svrtype`) as a session progresses. This is the actual
verbatim order, read out of the boot/login state machines:

```
 CONNECT
    │
    ▼
 ┌─────────────────────────── GATE ───────────────────────────┐
 │ CMD_REG_SVRLIST                                             │
 │   → CMD_GET_SVRLIST                                         │
 │        ok    → CMD_GET_SVRVERSION ─┐                        │
 │        error → CMD_GET_EULA ───────┤                        │
 │                                     ▼                        │
 │                              CMD_GET_SVRTIME                │
 │                                     │                        │
 │                                     ▼                        │
 │                          CMD_GET_INFORMATIONLIST             │
 │                                     │                        │
 │                                     ▼                        │
 │                             CMD_GET_URLLIST                 │
 │                                     │                        │
 │                                     ▼                        │
 │                             CMD_DISCONNECT                  │
 │                                     │                        │
 │                                     ▼                        │
 │                         CMD_REG_SVRLIST  (reconnect)         │
 └───────────────────────────────────┬──────────────────────────┘
                                      ▼
 ┌───────────────────────── ACCOUNT ──────────────────────────┐
 │ CMD_AUTH_GAMEID                                             │
 │   → CMD_DISCONNECT                                          │
 │   → CMD_LOGIN (svrtype "ACCOUNT")                           │
 │        [no existing account → CMD_CREATE_PLAYER first,      │
 │         then CMD_SET_LANGUAGE, then CMD_LOGIN retried]       │
 │   → CMD_GET_PLAYERLIST                                      │
 │   → CMD_SET_LANGUAGE                                        │
 │   → CMD_GET_PRIVATEINFO → CMD_GET_DIVISIONUPDATE             │
 └───────────────────────────────────┬──────────────────────────┘
                                      ▼
 ┌────────────────────────── LOBBY / MENU ────────────────────┐
 │ CMD_DISCONNECT                                               │
 │   → CMD_LOGIN (svrtype "LOBBY")                              │
 │   → CMD_SET_CURRENTPLAYER  (ping/mail/latency thresholds)    │
 │   → CMD_GET_BLOCKLIST      (pick least-full block)           │
 │   → CMD_JOIN_BLOCK         (NAT info, chosen block index)    │
 │        → registers CMD_WATCH_ROOMLIST + CMD_WATCH_BLOCK_PLAYERLIST│
 │   → CMD_GET_PLAYERINFO                                       │
 └───────┬───────────────────────────────────────┬─────────────┘
         ▼ (create/join a room)                  ▼ (quick match)
 ┌── ROOM ─────────────────────────┐   ┌── QUICK MATCH ──────────────────┐
 │ CMD_CREATEJOIN_ROOM             │   │ CMD_START_QUICKMATCH             │
 │  resp has room_id →             │   │  result=ERR_QMATCH_NOTICE →      │
 │    CMD_SET_GAMEENV              │   │    (searching, wait/retry)       │
 │  no room_id → CMD_LEAVE_ROOM    │   │  result=ERR_QMATCH_STARTMATCHING │
 │        — or —                   │   │    → (matched, proceeds to ROOM) │
 │ CMD_JOIN_ROOM                   │   │  result=ERR_QMATCH_PRECONNECT    │
 │  resp: room_owner.*, ex/in ip:port│  │    → 30s NAT/connect timer       │
 │  → CMD_GET_IPANDPORT(owner pid) │   │  result=NOERR → confirmed         │
 │  → real P2P UDP connect to owner│   │  ERR_REFUSED/ERR_GETPLAYERFAIL    │
 │    (~40s, see detail below)     │   │  → CMD_LEAVE_ROOM                 │
 │  → CMD_WATCH_ROOMPLAYERLIST(room_id)│ │                                  │
 │                                  │   │  other terminal error             │
 │  in-room config (host):         │   │    → CMD_CANCEL_DECIDE_QUICKMATCH │
 │   CMD_SET_GAMEENV, CMD_SET_GUEST│   │  user cancels search              │
 │   CMD_CHANGE_ROOMNAME,          │   │    → CMD_CANCEL_STARTQUICKMATCH   │
 │   CMD_KICK_ROOMMEMBER           │   └──────────────┬────────────────────┘
 │                                  │                  │
 │  entry-game screen registers:   │                  │
 │   CMD_WATCH_DECIDE_GAMEENV      │                  │
 │   CMD_WATCH_DECIDE_GAMEPLAYER   │◄─────────────────┘
 │   CMD_WATCH_DECIDE_GAMEPLAYERENV│
 │   CMD_WATCH_DISCON_PLAYERENV    │
 │   CMD_WATCH_DISCON_PLAYERMATCH  │
 │   CMD_WATCH_ENTRY_GAME          │
 │   CMD_WATCH_UPDATE_GAMERECORD   │
 │  → CMD_ENTRY_GAME (ready-up)    │
 └───────────────┬──────────────────┘
                 ▼
 ┌── IN-MATCH (event pump, host-authoritative) ───────────────┐
 │ registers: CMD_WATCH_ABNORMALEND, CMD_WATCH_FORFEITEDGAMEEND,│
 │            CMD_WATCH_INJURYGAMEEND, CMD_WATCH_OWNGOALGAMEEND │
 │ per local event, host sends:                                 │
 │   CMD_CHANGE_GAMEPHASE, CMD_ADD_SCORE, CMD_ADD_FOUL,          │
 │   CMD_UPDATE_COMBINATION, CMD_SET_GAMETEAM,                   │
 │   CMD_UPDATE_ROOMSTATE, CMD_SEND_ABNORMALEND,                 │
 │   CMD_SET_INJURYGAMEEND, CMD_SET_FORFEITEDGAMEEND (bare),     │
 │   CMD_SET_GAMEMEMBERENV, CMD_SET_GAMEMEMBER                   │
 │ opponent forfeit pushed → CMD_REQ_FORFEITEDGAME_REPLY         │
 │ every tick → CMD_GET_GAMEADDPOINT                             │
 └───────────────┬─────────────────────────────────────────────┘
                 ▼
        CMD_GET_GAMERESULTS  → CMD_LEAVE_ROOM
```

Running the whole time, independent of the stage above:

- **Chat**: on entering LOBBY, `CMD_WATCH_ROOMSTATE`, `CMD_WATCH_MAINTETIME`, `CMD_WATCH_EMERGENCY`,
  `CMD_WATCH_SHORTMAIL`, `CMD_WATCH_IPANDPORT` are all registered together in one burst. `CMD_SEND_TEXTCHAT`
  + `CMD_WATCH_TEXTCHAT` are gated on `scene == LOBBY`.
- **Social**: `CMD_GET_BLACKLIST` fetch is immediately followed by registering `CMD_WATCH_FRIENDLIST` +
  `CMD_WATCH_FRIENDREQ` (`FUN_0077dc90` state 3). Both are answered requests, and `FUN_0077db80`
  re-reads `CMD_GET_FRIENDLIST` once they answer, so the server must ACK them instead of parking
  them. PES2010 sends the social and mail commands on the **Lobby** socket.
  Friend requests, invitations and mail (`CMD_SEND_FRIENDREQ`, `CMD_SEND_SHORTMAIL`,
  `CMD_SEND_INVITATION`, `CMD_ADD_BLACKLIST`, etc.) are all fire-when-clicked, no fixed sequence.
  Accepting, declining, withdrawing, deleting or blocking pushes a fresh
  `CMD_WATCH_FRIENDLIST` (`friend_num`/`flist`, same shape as `CMD_GET_FRIENDLIST`) to every
  affected online player, followed by a bare `CMD_WATCH_FRIENDREQ` when subscribed. Unsolicited
  pushes use rqid 0. Without this, an already-open reply/mail screen keeps the pre-accept list
  (`RecievedReq` instead of `Friend`) until the friend screen is reopened.
- **Community (COMMU)** and **Competition League (COMPLG)**: separate optional sub-flows reachable from
  LOBBY, summarized in [Community & league flow](#community-commu--competition-league-complg-flow) below.

---

## Verified per-command detail

Request/response columns are only filled where the decompile actually showed a field being set (request)
or read by name (response). `—` means bare (genuinely no fields at that call site), not "unknown". Where a
response is handed to an un-decompiled parser function, that's noted instead of guessing field names.

### Gate / handshake

| Command | Request | Response | Notes |
|---|---|---|---|
| `CMD_REG_SVRLIST` | opaque pre-built struct (no individual literal keys at the call site) | error-check only | First command sent on connect; sent again after the full gate sequence completes |
| `CMD_GET_SVRLIST` | `timeout`=20, `svrtype`="GATE", `lang` | not resolved (opaque parser `FUN_007336b0`/`FUN_0076b2a0`) | ok → `CMD_GET_SVRVERSION`; error → `CMD_GET_EULA` |
| `CMD_GET_SVRVERSION` | `svrtype`="GATE" | not resolved (`FUN_007336b0`) | → `CMD_GET_SVRTIME` |
| `CMD_GET_EULA` | `uri` (version/locale token) | **`eula`** (confirmed, read by name) | only taken if `CMD_GET_SVRLIST` errored |
| `CMD_GET_SVRTIME` | `svrtype`="GATE" | not resolved (`FUN_0072a550`) | → `CMD_GET_INFORMATIONLIST` |
| `CMD_GET_INFORMATIONLIST` | `svrtype`="GATE", `lang` | not resolved (`FUN_0072c280`) | → `CMD_GET_URLLIST` |
| `CMD_GET_URLLIST` | `svrtype`="GATE", `lang` | not resolved (`FUN_0072c6e0`) | → `CMD_DISCONNECT` → `CMD_REG_SVRLIST` |

### Account / login

| Command | Request | Response | Notes |
|---|---|---|---|
| `CMD_AUTH_GAMEID` | `gameid`, `gameid_password` (if a saved credential exists), `gameid_url_num`, `gameid_url` (list) | not resolved (`FUN_0072c100`) | → `CMD_DISCONNECT` → `CMD_LOGIN` |
| `CMD_LOGIN` | `svrtype`, `platform`, `style`, `loginid`, `loginid_password`, `use_regcode`, `regcode` (conditional), `patchver`, `dlcver`, `parahash`, `entryhash` | **`account_first_login`** (confirmed, read by name) | fully verified separately, see pass-1 companion; re-confirmed here at every relogin call site via the shared builder `FUN_007c4f40` |
| `CMD_CREATE_PLAYER` | `svrtype`="ACCOUNT", **`name`** | error-check only | → `CMD_SET_LANGUAGE` |
| `CMD_CHECK_STRING` | **`str`** (the string being validated) | error-check only | precedes `CMD_SET_PLAYERPROFILE` at one call site; standalone validator at another |
| `CMD_GET_PLAYERLIST` | `svrtype`="ACCOUNT" | player-slot count (via `FUN_00732d20`, exact field names not resolved) | → `CMD_GET_PRIVATEINFO` |
| `CMD_GET_PRIVATEINFO` | `svrtype`="ACCOUNT", **`pid`** (current profile) | `count`, then `privInfo[i].reldate|iid|title|info|important|present` (`FUN_00731c60`) | → `CMD_GET_DIVISIONUPDATE` |
| `CMD_SET_PLAYERPROFILE` | one field keyed `"svrtype"` but holding a **profile-type enum value**, not a service tag — flagged as ambiguous, possibly decompiler mis-association | error-check only | |

### Lobby / menu

| Command | Request | Response | Notes |
|---|---|---|---|
| `CMD_SET_CURRENTPLAYER` | `svrtype`="MENU", **`lang`**, `frame_rate`, `pcspec` | `useable_team_size`, `useable_team_id[]`, `mailcount`, `newmail`, `chat_probi`, `camera_probi`, `voice_probi`, `band_probi`, `green_ping`, `green_band`, `yellow_ping`, `yellow_band`, `red_ping`, `red_band`, `green_delay`, `yellow_delay`, `red_delay`, `p2ptimeout`, `first_login`, `lossframe_match`, `avg_max_combination`, `avg_total_combination`, `invite_num`, `check_lobbygame_ping_diff`, `check_game_ping_max`, `mcount10flg` — all read by name via `FUN_0072bb20` | sent right after login; response sets the session's ping/latency thresholds |
| `CMD_GET_BLOCKLIST` | `svrtype`="MENU" (or `"LOBBY"` at an alternate call site) | `count`, `bklist[i].player_num`, `bklist[i].max_player_num`, plus a per-block subfield (name built dynamically, not statically resolved) | caller picks the least-full block |
| `CMD_JOIN_BLOCK` | `svrtype`="MENU", `index` (chosen block), `ex_ip`, `ex_port`, `in_ip`, `in_port`, **`nat`** (NAT-type code; the function that originally computes it wasn't traced) | none read (success/fail only) | → `CMD_GET_PLAYERINFO`; registers `CMD_WATCH_ROOMLIST` + `CMD_WATCH_BLOCK_PLAYERLIST` |
| `CMD_GET_RANKING` | **`data`** (opaque pre-serialized filter blob), + one more optional key only sent when a UI filter is active (address not individually resolved) | not resolved (handled in a separate screen-refresh callback) | only sent if the local player has a ranking entry |

### Room lifecycle

#### Getting in: create/join, then a real peer-to-peer connect

`CMD_CREATEJOIN_ROOM` and `CMD_JOIN_ROOM` only get you *server-side* membership in the room. Actually
reaching the room owner is a separate, fully traced client-side state machine (`FUN_007b8ad0`) that runs
immediately after `CMD_JOIN_ROOM` succeeds:

```
CMD_JOIN_ROOM (room_id, is_invited, password)
  → response: room_owner.name, room_owner.pid, room_owner.xuid,
              ex_ip, ex_port, in_ip, in_port, x36_* (unused Xbox-360-parity fields)
  → wait for local "punch ready" flag
  → CMD_GET_IPANDPORT(pid = room_owner.pid)
  → response: ex_ip, ex_port (of the owner)
  → poll local P2P peer list for up to ~40s, looking for the owner's pid among
    currently-connected peers (FUN_007c8200 / FUN_00728d00)
  → real UDP hole-punch / connect attempt using the owner's ex/in ip:port
    (FUN_00f8f3c0 → FUN_00f94170 → FUN_00f937b0), ~40s timeout, polling a
    connection-state field for values 4 (connected) or 8 (failed)
  success → continues into the room                     failure/timeout at any step
                                                            → CMD_LEAVE_ROOM → poll ack → reset
```

This confirms match traffic (and the room-owner handshake itself) is **direct peer-to-peer**, not
server-relayed — the server's only role is introducing the two IP:port pairs via `CMD_JOIN_ROOM` +
`CMD_GET_IPANDPORT`.

#### Room settings (`game_env`)

`CMD_SET_GAMEENV` (host sends) and `CMD_WATCH_DECIDE_GAMEENV` (every member receives — same field shape) both
carry one `game_env` sub-object, cached in the room-context struct at offsets `+0x3ca8`–`+0x3cc8`. Every
field's valid values were read directly out of the client's enum-string tables (see
[Verified enum tables](#verified-enum-tables) below) rather than guessed:

| Field | Type | Valid values |
|---|---|---|
| `cpuLevel` | enum | `VERYEASY`, `EASY`, `NORMAL`, `HARD`, `VERYHARD`, `HARDEST` |
| `gametime` | enum | `NOSET`, `5MINUTES`, `10MINUTES`, `15MINUTES`, `20MINUTES`, `25MINUTES`, `30MINUTES` |
| `injury` | bool enum | `NO`, `YES` |
| `condition` | enum | `NORMAL`, `RANDOM` |
| `ball_type` | raw int | not enum-backed — direct index into the ball asset list |
| `exGame` | bool enum | `NO`, `YES` (extra-time toggle) |
| `pkOnOff` | bool enum | `NO`, `YES` (penalty shootout toggle) |
| `substitution` | raw int | substitution count, not enum-backed |
| `limitTime` | enum | `NOSET`, `SHORT`, `MIDDLE`, `LONG` |

#### Room / match state machine

`CMD_UPDATE_ROOMSTATE`'s `status` field and `CMD_WATCH_DECIDE_GAMEPLAYER`'s `status` field share one enum
(6 values, read from the client's own string table):

```
WAITING ──► SETENV ──► GAME ──► RESULT
   ▲                                │
   └──────────── (room reset) ──────┘
        DISCONWAIT / RESULTWAIT — side states entered while a disconnect is being resolved
```

Confirmed from the in-match dispatcher: the client fires `CMD_UPDATE_ROOMSTATE{status=SETENV}` when a match
is about to start, and `CMD_UPDATE_ROOMSTATE{status=WAITING}` to reset the room back to its lobby state
afterward — both **host-initiated for `SETENV`, unconditional (any peer) for the reset to `WAITING`.**

#### Other room lifecycle commands

| Command | Request | Response | Notes |
|---|---|---|---|
| `CMD_CREATEJOIN_ROOM` | **`name`**, `match_type`, `team_category`, **`lang`**, `max_players`, `is_invite_limit`, `password`, `enable_guest` | **`room_id`** (confirmed, read by name — presence of this field = success) | has room_id → `CMD_SET_GAMEENV`; no room_id → `CMD_LEAVE_ROOM` |
| `CMD_LEAVE_ROOM` | — (bare at every call site) | — | universal abort/exit-room; used on every failure branch above |
| `CMD_SET_GUEST` | `has_guestplayer` | error-check only | re-sent whenever local guest-controller state changes |
| `CMD_CHANGE_ROOMNAME` | **`name`**, `passwd` | none | room-settings screen action |
| `CMD_KICK_ROOMMEMBER` | `target_pid` | none | |
| `CMD_ENTRY_GAME` | `entry`=1, **`side`** (`HOME`/`AWAY`, from controller-port bit) | none | guarded by local match phase == pre-match; "ready up" for the match |
| `CMD_GET_IPANDPORT` | `pid` (target — e.g. room owner) | `ex_ip`, `ex_port` (confirmed via helper) | called right after `CMD_JOIN_ROOM` succeeds |
| `CMD_WATCH_ROOMPLAYERLIST` | `room_id` | — | registered once `room_id` is known (after join/create), separate from the block-level watches |

#### Entry-game watch pushes (all 7 fully decompiled)

Registered together, bare, in one burst when the entry-game screen opens — this is how non-host room
members find out what the host is configuring:

| Watch | Fields pushed | What it's for |
|---|---|---|
| `CMD_WATCH_DECIDE_GAMEENV` | full `game_env` (same 9 fields as `CMD_SET_GAMEENV` above) | broadcasts the host's room-settings changes to everyone else |
| `CMD_WATCH_DECIDE_GAMEPLAYER` | `status` (room/match state enum, above), `player_count`, an 8-slot ID array, an 8-slot `video` array (`FRAME_50`/`FRAME_60` — target framerate per slot) | per-slot roster + framerate broadcast |
| `CMD_WATCH_DECIDE_GAMEPLAYERENV` | `count`, then per entry: id, `side` (`HOME`/`AWAY`), `sideLeader` (`NO`/`YES`) | team-side and "who's the side captain" assignment |
| `CMD_WATCH_DISCON_PLAYERENV` | one field (minimal — just enough to identify what changed) | lightweight disconnect notice |
| `CMD_WATCH_DISCON_PLAYERMATCH` | `count`, then per entry: id, `side`, `sideLeader` — identical shape to `_GAMEPLAYERENV` | re-broadcasts side/captain assignments after a disconnect reshuffles them |
| `CMD_WATCH_ENTRY_GAME` | `entry_count`, then per entry: `pid`, `entryNum`, `gameNum` | tracks who has pressed "ready" (`gameNum == -1` = not yet entered); local ready-flag flips once more than one entry is complete |
| `CMD_WATCH_UPDATE_GAMERECORD` | none — bare signal only | just a "your match record was updated, go re-fetch it" ping |

#### Room roster and join events (verified against this build)

- `MSG_REQRMEMBERLIST` is answered with ACK + `MSG_RMEMBERLISTSTART` +
  `MSG_RMEMBERLIST` + `MSG_RMEMBEREND`. `FUN_00BB5190` reads, per member,
  `ex_ip`, `ex_port` (2-byte), `in_ip`, `in_port` (2-byte), `pid`,
  `room_pid`, `room_entry_no`, `game_entry_no`, and eight indexed
  `desiredPosition[N]` strings. `pid` is the lookup key used by
  `FUN_00BB3D70`; `room_pid` is a separate room-local id and is what
  `FUN_00BB41E0` serializes back as `room_player_id` — it must be unique per
  member, not the host repeated on every entry.
- `CMD_WATCH_ROOMSTATE` (`FUN_0072F770`) reads `event` as enum type `0x2e`
  (`OwnerChanged`, `AppointOwner`, `Kicked`, `StateChanged`), `owner_pid`, and
  `state` as enum type `0x2d`. Both `event` and `state` are plain top-level
  keys; a response with an unknown enum string maps to `-1`.
- `CMD_WATCH_ENTRY_GAME` (`FUN_0072AEB0`) reads `entry_count` and
  `entrygame[i].pid`, `.entryNum`, `.gameNum`. It requires every unoccupied
  slot to be present with `gameNum=-1`, and only enables the local ready flag
  when more than one entry has `gameNum != -1`.
- `CMD_WATCH_DECIDE_GAMEPLAYER` (`FUN_0072ED20`) reads the unindexed key `pid`
  eight times and `video` eight times into fixed arrays. **These reads do not
  advance through repeated keys.** The 2026-10-26 crash dump contains the wire
  response `pid=1,pid=2,pid=-1,...`, but the parsed PID array at
  `(*0x016f0284)+0x3d50` is eight copies of `1`. Offline execution of the real
  tokenizer/reader confirms the same behavior for scalar and bracketed lists:
  querying `pid` always selects the first item. Only explicit `pid[i]` queries
  select later items; this callback never constructs those queries. The uint
  reader `FUN_00B6EB80` also rejects textual `-1` (fallback `0x7b`), whereas
  `4294967295` produces the all-ones sentinel. **A response-only format fix has
  not been established; do not confuse eight transmitted values with eight
  successfully parsed slots.**
- `CMD_GET_FRIENDLIST` / `CMD_WATCH_FRIENDLIST` entries are parsed by `FUN_00735DE0` as
  `state` (enum 0x18: Friend/SentReq/RecievedReq), `pid`, `npid`, `xuid`, `name`,
  `is_login`, `is_ingame`, `server`, `lobby_id`, `lobby_name`, `block_id`,
  `block_name`, `room_id`, `room_name`, `req_date`, `req_read`, `division`
  (enum 0x16: D3C/D3B/D3A/D2/D1/NONE) and `mode` (enum 0x25: NONE/NETWORK/COMPE/
  COMMUNITY/LEGENDS/...). `mode` is the friend's availability: if it is omitted the
  entry stays at `NONE`, and the mail screen treats that friend as unavailable even
  though the friend list still draws it. The server now sends `mode=NETWORK` for an
  online friend and `NONE` otherwise, plus `lobby_id`/`block_id`/`req_date`/`req_read`.
- `CMD_GET_PRIVATEINFO` (`FUN_00731C60`): see the dedicated section below; `present`
  is mandatory.
- `MSG_ROOMINNOTICE` and `MSG_PxROOMOUTNOTICE` match `FUN_00BB4D70`'s field
  table and are required. `CMD_WATCH_ROOMPLAYERLIST` (`FUN_00BB4060`) is not a
  subscription: it sends `MSG_REQRMEMBERLIST`, takes one START/LIST/END stream
  (`FUN_00BB5190` then marks the task done) and only changes afterwards through
  these two notices. Without `MSG_ROOMINNOTICE` the host never adds the joiner
  as a P2P peer (`FUN_007C7FC0`), drops the joiner's probes (`FUN_00F8BFF0`),
  and the joiner leaves after the 15 s handshake timeout (`FUN_007C0670`
  status 6, `FUN_007B8AD0`). Confirmed live in a LAN test: with the notice, the
  join completes and the Lobby connection stays up.

### Server-side match-setup corrections (2026-10-26)

- `CMD_SET_GAMEMEMBERENV`: validate the owner's complete selected-player decision
  before applying it, retain `sideLeader`, ACK first, then publish
  `CMD_WATCH_DECIDE_GAMEPLAYERENV`. Previously only the PES2011 roomplayer publisher
  ran, leaving PES2010 peers without the final decision.
- `CMD_SET_GAMEMEMBER`: after an accepted side decision, ACK first and publish the
  final `CMD_WATCH_DECIDE_GAMEPLAYER` roster. Previously this was a bare ACK.
- The PES2010 roster counts/includes entered participants, not all room occupants.
  Unused scalar PID slots now carry unsigned `4294967295`, which the native reader
  accepts. **This does not repair the repeated-first-PID callback described above.**
- Room advertisements use the reported phase and team IDs, including phase changes
  that leave status at GAME. Returning to WAITING/SETENV resets phase to ENTRY.
- Regression coverage: `tests/OpenEleven.Server.Tests/Pes2010MatchSetupTests.cs`.
  These are server protocol/state corrections, **not a verified kickoff-crash fix**.
  Live validation still requires restarting the server with the changes and starting
  a two-client match with unchanged executables. Check final watch delivery to both
  peers between the host's setup commands and phase 1ST; reaching kickoff without
  the recorded crash is the required client-side result.

### PES2010 division ladder and team history (2026-10-26)

- PES2010 ranks on **D1, D2, D3A, D3B, D3C** and starts in D3C. The reference
  capture proves `CMD_GET_PLAYERLIST`/`CMD_GET_PLAYERINFO` carry `division="D3C"` /
  `div="D3C"`.
- `RankSystem.Normalize` used to rewrite any non-D5..D1 value to `D5` after every
  match, and the one-time rank initialization did the same. That destroyed the
  PES2010 ladder, which is why `CMD_SEARCH_PLAYER` advertised `division="D5"`.
  Normalization is now gated by `RankSystem.Applies(profile)`, and a startup repair
  maps already-damaged rows back: D5→D3C, D4→D3B, D3→D3A (D1/D2 unchanged).
- The same repair applies to every PES2010 output that carries a division:
  search, player list, block list, friend list, game results and quick match.
- `CMD_GET_PLAYERINFO` must send `teamLog=[a,b,c,d,e]` and `desired_position=[...]`
  in list form. The parser (`FUN_00732230`) asks for `teamLog[0]`, but the lookup
  (`FUN_00b6e0f0`) splits that into key `teamLog` plus element 0. The reference's flat
  `teamLog[0]=v` pairs never match, so every slot stayed at the -1 its reset
  (`FUN_004a3640`) leaves (seen live with a CE write watch). Values land as words at
  player+0x1e0..0x1e8. `FD_teamLog` is read by the parser too but is absent from the
  reference capture, so it is deliberately **not** invented here.
- The same lookup reads every `x[i].y` key (`date_list[i].id`, `ulist[i]...`) as
  element i of list `x`.

### PES2010 `CMD_GET_PRIVATEINFO` notice fields

`FUN_00731C60` reads exactly six fields per notice and **stops the whole entry at the
first missing one**, after already accepting `count`:

- `privInfo[i].reldate` (int)
- `privInfo[i].iid` (int)
- `privInfo[i].title` (string)
- `privInfo[i].info` (string)
- `privInfo[i].important` (enum type 0: FALSE/TRUE)
- `privInfo[i].present` (enum type 0: FALSE/TRUE)

The server sent the first five only. PES2010 therefore showed `count=1` with no rows;
PES2011's parser does not require `present`, so it looked fine there. The response now
sends `present="TRUE"`.

### PES2010 quick match room entry

`MSG_QUICK_ACCEPT_MATCH_OFFER_RES` only confirms the formed group. PES2010 never opens the
lobby room screen for a quick match, so the server must also send the room tail a
`CMD_JOIN_ROOM` response carries. It previously created the room silently: both clients
stayed on the room screen with no roster/state and one left after a few seconds.

`Hub.PublishQuickMatchRoom` now, for PES2010:

- moves the room to `SETENV` and marks `MatchStarting`;
- assigns `GameEntryNo` (0..n-1), alternating `GameSide`, and one leader per side;
- pushes `CMD_WATCH_ROOMSTATE`, `MSG_RMEMBERLISTSTART/LIST/END`,
  `CMD_WATCH_ROOMPLAYERLIST`, `CMD_WATCH_IPANDPORT`, `CMD_WATCH_ENTRY_GAME`,
  `CMD_WATCH_DECIDE_GAMEENV`, `CMD_WATCH_DECIDE_GAMEPLAYER` and
  `CMD_WATCH_DECIDE_GAMEPLAYERENV`.

`MSG_QUICK_REQ_MATCHING_START` still pairs only equal `type|player_num|game_time|frrate`.
A "free 2vs2" (`player_num=4`) search with two clients cannot form a match by design.

### Online kickoff crash: 2026-10-26 dump

Confirmed for executable SHA-256
`90297023199b3e59a48ab936c2f67f71722bc27ae1cbdda20a45251060a2c680`:

- `pes2010.exe.52740.dmp`: read AV `0xc0000005` at `0x0097afa9`
  (`FLD [EAX]`), reading `0x01f00598`. This is an out-of-bounds **read**,
  not evidence of a stack-buffer overwrite or an oversized network packet.
- Event `0x03000051` at `0x01eff988` has a 2-by-11 coordinate table at `+0x1c`
  and eight controller records at `+0x124`, stride `0x1c`. Slots 0/1 contain
  `(side,player)=(0,10)/(1,10)`; slots 2–7 contain `(0,255)`.
- `FUN_0097AF20`, the **in-match chat overlay** event handler, reads
  `event + 0x1c + 12*(side*11+player)` before its
  visibility checks. Slot 2 therefore reads `0x01f00598`, exactly matching
  the exception. `FUN_009815E0` contains the same unguarded lookup at
  `0x009816b0`; it is another chat-related display component, not a proved
  second crash in this run (the dump's HUD pointer for that component is null).
- `FUN_00B17830` deliberately initializes empty controller records with
  `player=0xff`; this byte is **not proof of a failed network enum lookup**.
  `FUN_00B1D580` interpolates snapshots and dispatches this event at
  `0x00b1db74` (return `0x00b1db79`). `0x00985adb` is an intermediate
  forwarding return address, not the event constructor.
- Source chain recovered from the dump, not inferred from packet field names:
  scene `0x23ce8b70` -> HUD `0x23f3c330` -> chat object `0x23d24260`
  (vtable `0x01226510`). The HUD's `+0x88` pointer matches the faulting `ESI`.
  Game `0x1d2b1720` -> controller table `0x2411d650`: slot 0 has assigned slot 0,
  side 0, selected global footballer 11; slot 1 has assigned slot 1, side 1,
  selected global footballer 22. Slots 2–7 have unassigned slot/side/footballer
  `0xff`. Thus the actual match controller table is a two-player HOME/AWAY
  setup, **not eight duplicate active controllers**.
- `FUN_00B1BE90` reads each controller's selected global footballer at `+0x3d`.
  At `0x00b1bfab` it initializes the event player index to `0xff`; the
  `0x00b1c002`–`0x00b1c008` range permits only footballers 1–22 to replace that
  default. Inactive controllers therefore produce the observed empty records.
  The current snapshot at `0x23da5990`, embedded event at `+0x4eb0`
  (`0x23daa840`), contains the exact eight `(side,player,active,...)` prefixes
  dispatched on the stack. `FUN_00B1D580`'s saved frame at `0x01effd44`
  references that current snapshot and previous snapshot `0x23e39e50`.
  No network enum parse is needed to explain the `0xff` reaching the handler.
- Why offline can work: `FUN_00B0AD50` creates this chat widget when HUD mode
  (`+0xbc`) is 5 or game type (`+0xb8`) is 10. The dump has type 0/mode 5,
  so the widget is created and all eight records are forwarded to it. A
  **read-only** comparison with the same executable running offline (PID 66980,
  started `10:53:40`) has type 0/mode 4 and null chat widgets at HUD `+0x88`
  and `+0x8c`, while its unused controller slots also contain `0xff`.
  This establishes the extra online consumer, not a different empty-slot value.
- The TEB gives `StackBase=0x01f00000`: the recorded read is 0x598 above it.
  **Capture-time caveat:** the full dump contains a zero-filled, RW, MEM_MAPPED
  page at `0x01f00000`. Its mapping timing cannot be established from this dump.
  Do not claim this page is absent from the captured memory or that an emulator
  loaded with all captured pages reproduces the Windows AV. The exception
  record proves the fault; instruction tracing proves the out-of-table address.
- Investigated handler/parser bytes match the executable on disk. The module
  list contains no loaded kitserver DLL. This does not establish that the
  entire executable is pristine or rule out earlier modifications.
- The duplicated parsed PID roster is a separate confirmed defect. A causal
  link from it to this AV, a server-only repair, and an end-to-end match fix
  remain unproven. Do not fill empty slots with fake players to hide the AV.
  Tracing the unchanged handler with the captured cached PIDs and with cached
  PIDs `(1,2,-1,-1,-1,-1,-1,-1)` reaches the same out-of-table address, with
  no reads of the network state before that instruction. This counterfactual
  keeps the match event fixed; it does not prove upstream setup is unaffected.
- Session log correlation (`src/OpenEleven.Pes2010Pc/bin/Debug/net9.0/logs/pes2010pc.log`):
  at `10:27:06.933` the server sends that exact roster and correct HOME/AWAY
  sideinfo (lines 87273/87355). The host itself submits the correct two-player
  `envlist` at `10:27:08.304` (line 87974), so duplicated parsed PIDs alone
  do not prove that the actual match-side assignment was wrong. Team selection
  succeeds at `10:27:16.017`; `phase="1ST"` is ACKed at `10:27:19.233`.
  The host disconnect and recorded loss occur at `10:27:28.059`, after the
  `10:27:27` dump, not before it. The log does not show a server error or
  timeout immediately preceding the crash.

Read-only reproduction: `tools/analyze_pes2010_crash.py DUMP --exe EXE
--replay-parser --replay-handler --output .artifacts/pes2010-crash-20261026/evidence.json`.
Dependencies: `minidump`, plus `unicorn` for offline instruction tracing. Retained
response strings in the dump are not an ordered capture of the final packets.
The handler trace runs unchanged game instructions, stubs UI hide calls, and
stops before the first out-of-table lookup. It is neither an OS-fault replay
nor a full-match validation. **Investigation constraint: no executable or
live-memory patches; no client/server behavior changes were made in this pass.**

### Quick match

| Command | Request | Response | Notes |
|---|---|---|---|
| `CMD_START_QUICKMATCH` | opaque struct + `my_pid` | **`result`** — confirmed closed enum: `ERR_QMATCH_NOTICE` (still searching), `ERR_QMATCH_STARTMATCHING` (matched), `ERR_QMATCH_CANCELED`, `ERR_QMATCH_PRECONNECT` (starts a 30s connect timer), `NOERR` (confirmed), `ERR_REFUSED`/`ERR_GETPLAYERFAIL` (→ `CMD_LEAVE_ROOM`) | each result value drives a distinct state transition — this is the clearest example of "what do I get back" in the whole protocol |
| `CMD_CANCEL_STARTQUICKMATCH` | — | — | sent only while still searching |
| `CMD_CANCEL_DECIDE_QUICKMATCH` | — | — | aborts a matched-but-unconfirmed quickmatch |

### In-match event pump

All of these are dispatched from one function (`FUN_007c1f80`) reacting to locally-queued local match
events (event `type` tag in parentheses); the table below is the event→command→fields mapping, fully
decompiled this pass — including the enum value tables, resolved directly from the client's own string
tables rather than inferred.

**Authority split, confirmed from the dispatcher's host-gate checks:** match-progression commands
(`CMD_CHANGE_GAMEPHASE`, `CMD_ADD_SCORE`, `CMD_ADD_FOUL`, `CMD_UPDATE_COMBINATION`, `CMD_SET_GAMETEAM`,
`CMD_SET_GAMEMEMBERENV`, `CMD_SET_GAMEMEMBER`, `CMD_SET_INJURYGAMEEND`, `CMD_UPDATE_ROOMSTATE{SETENV}`) are
**host-only** (gated on a local "am I host" flag). Disconnect/sync-failure reporting
(`CMD_SEND_ABNORMALEND`, `CMD_SET_FORFEITEDGAMEEND`, `CMD_REQ_FORFEITEDGAME_REPLY`,
`CMD_UPDATE_ROOMSTATE{WAITING}`) is **unconditional — any peer can send these**, since any peer can be the
one who disconnects or detects desync.

| Event | Command | Request | Notes |
|---|---|---|---|
| 0 | `CMD_CHANGE_GAMEPHASE` | `phase` = one of `ENTRY`, `1ST`, `BREAK1`, `2ND`, `BREAK2`, `EX1ST`, `BREAK3`, `EX2ND`, `BREAK4`, `PK`, `END` (11-value enum, confirmed) | host-only |
| 1 | `CMD_SET_INJURYGAMEEND` | `side` = `HOME`/`AWAY` | host-only |
| 2 | `CMD_UPDATE_COMBINATION` | `total_comb`, `max_comb` (raw ints) | host-only |
| 3 | `CMD_ADD_SCORE` | `goal_side`, `goal_player_side`, `assist_side`, `assist_player_side` — each `HOME`/`AWAY`; `goal_gameplayer_id`, `assist_gameplayer_id`, `goal_player_pid` (raw ints) | host-only. Field **names** are certain; the mapping of which of the 4 raw event values (`event[9]`, `event[0xb]`, `event[5]`, `event[7]`) fills which "side" field is inferred from call order (very likely correct — computed and emitted in matching sequence) but not proven at the disassembly/register level — see [Known gaps](#known-gaps) |
| 4 | `CMD_ADD_FOUL` | `card_pid`, `card_side` (`HOME`/`AWAY`), `card_gameplayer_id`, `card_kind` (`YELLOW`/`RED`, confirmed 2-value enum), `injury_side` (`HOME`/`AWAY`), `injury_gameplayer_id`, nested `injury_data` (`symptom`, `level`, `level_no_self_conscious`), `injury_all`, `injury_one` | host-only. Same `injury_data` shape independently confirmed in the COMMU league injury-report call sites — canonical injury shape used everywhere |
| 5 | `CMD_SET_GAMETEAM` | `home_team_id`, `away_team_id` (raw ints) | host-only |
| 6 | `CMD_UPDATE_ROOMSTATE` | `status` = `SETENV` (fixed) | host-only; fired entering the match-setup phase |
| 7 | `CMD_UPDATE_ROOMSTATE` | `status` = `WAITING` (fixed) | **unconditional**; resets the room after a match |
| 8 | `CMD_SEND_ABNORMALEND` | `is_sync_failed` = `NO` (fixed) | unconditional |
| 9 | `CMD_SET_FORFEITEDGAMEEND` | **bare — zero fields** | unconditional; forfeit is keyed server-side off the existing room/game session |
| 10 | `CMD_SEND_ABNORMALEND` | `is_sync_failed` = `YES` if the event's resync flag is 1, else the raw flag value | unconditional; also flips a local "resync in progress" flag |
| 0xb | `CMD_SET_GAMEMEMBERENV` | whole opaque merged struct | host-only |
| 0xc | `CMD_SET_GAMEMEMBER` | bare | host-only |
| (push) | `CMD_REQ_FORFEITEDGAME_REPLY` | reply to an opponent's pushed forfeit request, consumed via `FUN_0072f5d0`/`FUN_007296c0` | unconditional, not tied to a local event — fires off an incoming push instead |
| (every tick) | `CMD_GET_GAMEADDPOINT` | — (bare) | called every tick from the pump, and again from a post-match community-stats screen before `CMD_SET_COMMU_MATCHINFO` |

Registered once at pump start (state 0), regardless of host status: `CMD_WATCH_ABNORMALEND`,
`CMD_WATCH_FORFEITEDGAMEEND`, `CMD_WATCH_INJURYGAMEEND`, `CMD_WATCH_OWNGOALGAMEEND` — every peer watches for
these regardless of who's hosting.

`CMD_GET_GAMERESULTS` (`pid` request, response → `FUN_007379a0`, not resolved) is a standalone results-screen
call, not part of the event pump itself.

### Verified enum tables

Pulled directly from the client's own string-lookup tables (`FUN_00bb0110`/`FUN_00bb0050`, indexed by a
small integer "enum type ID" — 55 types total, `0x00`–`0x36`), not inferred from proximity or guessed. Only
the types actually referenced by the commands documented above are listed; the mechanism generalizes to all
55 if more are needed later.

| Type ID | Used by | Values |
|---|---|---|
| `0x01` | `CMD_ADD_FOUL`'s `card_kind` | `YELLOW`, `RED` |
| `0x14` | `game_env.condition` | `NORMAL`, `RANDOM` |
| `0x15` | `game_env.cpuLevel` | `VERYEASY`, `EASY`, `NORMAL`, `HARD`, `VERYHARD`, `HARDEST` |
| `0x17` | `CMD_WATCH_DECIDE_GAMEPLAYER`'s per-slot `video` | `FRAME_50`, `FRAME_60` |
| `0x19` | every `side`/`sideLeader`-shaped field (`side`, `goal_side`, `card_side`, `injury_side`, etc.) | `HOME`, `AWAY` |
| `0x1a` | `CMD_CHANGE_GAMEPHASE`'s `phase` | `ENTRY`, `1ST`, `BREAK1`, `2ND`, `BREAK2`, `EX1ST`, `BREAK3`, `EX2ND`, `BREAK4`, `PK`, `END` |
| `0x1b` | `game_env.gametime` | `NOSET`, `5MINUTES`, `10MINUTES`, `15MINUTES`, `20MINUTES`, `25MINUTES`, `30MINUTES` |
| `0x1d` | `game_env.limitTime` | `NOSET`, `SHORT`, `MIDDLE`, `LONG` |
| `0x2d` | room/match `status` (`CMD_UPDATE_ROOMSTATE`, `CMD_WATCH_DECIDE_GAMEPLAYER`) | `WAITING`, `SETENV`, `GAME`, `RESULT`, `DISCONWAIT`, `RESULTWAIT` |
| `0x36` | generic boolean-style fields (`injury`, `exGame`, `pkOnOff`, `sideLeader`, `is_sync_failed`, `is_select_team`, …) | `NO`, `YES` |

---

## Community (COMMU) & Competition League (COMPLG) flow

These are optional sub-systems reachable from the lobby, not part of the critical path above. Coverage here
is request-side only (from the caller table) — response fields weren't deep-dived for these.

**Community** — browse/join, then compete:
```
CMD_SEARCH_COMMU (keyword, target, has_friends, target_pid)  or  CMD_GET_JOINEDCOMMULIST
  → CMD_JOIN_COMMU (commu_id, password)   or   CMD_CREATE_COMMU (commu_name, commu_description, password, invite_only)
  → CMD_SET_CURRENT_COMMU (commu_id)
  → registers CMD_WATCH_FRIENDLIST / CMD_WATCH_FRIENDREQ (right after a CMD_GET_BLACKLIST fetch)
  → CMD_GET_COMMU_LEAGUELIST → CMD_JOIN_COMMU_LEAGUE (compe_id)
  → CMD_APPLY_COMMU_MATCH (match_id, room_id)  ⇄  CMD_REPLY_COMMU_MATCH (match_id, reply)
  → match proceeds through the normal ROOM flow above
  → CMD_GET_COMMU_*RANKING / *RESULTS (post-match stats)
  → CMD_LEAVE_COMMU_LEAGUE / CMD_WITHDRAW_COMMU (commu_id)
```

**Competition League** — very similar shape:
```
CMD_GET_COMPLG_LIST
  → CMD_JOIN_COMPLG / CMD_CREATE_COMPLG
  → CMD_SET_COMPLG_TEAM (per-player injury_data sub-struct)
  → CMD_ENTRY_COMMU_LEAGUECELL (cell_id)
  → matches proceed
  → CMD_GET_COMPLG_MYRESULTS / rankings
  → CMD_LEAVE_COMPLG / CMD_QUIT_COMPLG / CMD_END_COMMU_LEAGUE
```

---

## Enum catalog

Confirmed by direct code reading (string-literal comparisons against a `"result"`-style field, e.g. the
`CMD_START_QUICKMATCH` dispatcher above) rather than proximity-mined.

### Result / error codes
`NOERR` = success. 116 named failure codes exist, following `ERR_<AREA>_<REASON>` or `ERR_<REASON>`:

<details>
<summary>Full result/error code list (116)</summary>

```
NOERR
ERR_CLIENT_CANTCONNECT, ERR_CLIENT_TIMEOUT, ERR_CLIENT_SVRADDR, ERR_CLIENT_SVRDISCONNECT,
ERR_CLIENT_SENDFAIL, ERR_CLIENT_NONSUPPORT, ERR_CLIENT_OPTFORMAT, ERR_CLIENT_UNKNOWN,
ERR_OPTION_OUTOFRANGE, ERR_RESULT_OUTOFRANGE, ERR_DATABASE, ERR_MSGFORMAT,
ERR_SERVERNOTREADY, ERR_BUSY,

ERR_LOGIN_FAILED, ERR_ALREADYLOGGEDIN, ERR_DEFCLIENTVER, ERR_DIFFERENT_PATCHVERSION,
ERR_DIFFDCVERSION, ERR_INVALID_ACCOUNT, ERR_PARAMCHECK_FAILED, ERR_NOTGETCCODE,
ERR_REGCODE_FAILED, ERR_REGCODENOTISSUED, ERR_REGCODEDIFLASTTIME, ERR_INVALID_TICKET,
ERR_AUTHGID_AUTH, ERR_AUTHGID_SSLETC, ERR_AUTHGID_SSLEXPIRE, ERR_AUTHGID_URL,
ERR_ALREADYEXISTS, ERR_DELINDAYS, ERR_INVALIDLETTER, ERR_NGWORDININTRO, ERR_INVALIDDATE,
ERR_NOPLAYER,

ERR_STUN_UPNP_BIND, ERR_STUN_UPNP_PORTOPEN, ERR_STUN_ALREADYUSEDPORT,
ERR_STUN_SYMMETRICNAT, ERR_STUN_UDPBLOCKED, ERR_STUN_NETWORKSETTING, ERR_STUN_PORTRANGE,

ERR_ROOMNOTFOUND, ERR_ROOMISFULL, ERR_ROOMTOOMANY, ERR_ROOMSTATNOTMATCH, ERR_NOTINROOM,
ERR_NOTROOMOWNER, ERR_ALREADYINROOM, ERR_TOOMANYMEMBERS, ERR_NGWORDINRNAME, ERR_INVALIDARG,
ERR_NOTGAMEHOST, ERR_NOTFOUNDCLIENT, ERR_NORFOUNDCLIENT, ERR_CANTADDGUEST, ERR_NOGUEST,
ERR_TOOMANYGUEST, ERR_DIVISIONREFUSED, ERR_TARGETPLAYERNOTEXIST, ERR_TARGETISNOTLOGIN,
ERR_TARGETINGAME, ERR_ISNOTGM, ERR_CHATENABLE, ERR_NOBLOCKBELONG, ERR_REFUSED, ERR_INDEX_OUTOFRANGE,

ERR_BLOCKISFULL, ERR_BLOCKNOTEXIST, ERR_WEEKLYRANKINGNOTFOUND, ERR_TOOMANYRECORDS,

ERR_PLAYERNOTEXIST, ERR_PLAYERISONESELF, ERR_PLAYERISNOTGAMER, ERR_FRIENDCOUNTFULL,
ERR_INFRIENDLIST, ERR_INBLACKLIST, ERR_NGWORDINPNAME,

ERR_TEAM_NOTFOUND,

ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_COMMUCOMPEISFULL, ERR_COMMUMATCHCANCELED,
ERR_COMMUMATCHREFUSED, ERR_COMMUNITYISFULL, ERR_COMMUNOTENTRY, ERR_NOTJOINEDCOMMU,
ERR_ALREADYINCOMMU, ERR_NOTCOMMUOWNER, ERR_NOTENTRY, ERR_PLAYERNOTINCOMMU, ERR_PLAYERNOTINCOMPE,
ERR_TOOMANYCOMMU, ERR_TOOMANYLIST, ERR_NGWORD, ERR_PASSWDINCORRECT, ERR_ENTRYISFULL,
ERR_MATCHNOTEXIST,

ERR_COMPE_NOTFOUND, ERR_COMPE_STATUS, ERR_COMPE_FULL, ERR_COMPE_NOTENTRY,
ERR_COMPE_BEFORE_ENTRYTIME, ERR_COMPE_AFTER_ENTRYTIME, ERR_COMPE_JOIN_OTHERCOMPE,
ERR_COMPE_NOT_SELECT_TEAM, ERR_COMPE_NOT_PLAYOFF_ENTRY,

ERR_QMATCH_NOTICE, ERR_QMATCH_STARTMATCHING, ERR_QMATCH_CANCELED, ERR_QMATCH_PRECONNECT,
ERR_QMATCH_CONNECTFAILED, ERR_GETPLAYERFAIL, ERR_PING_FAILED
```
</details>

### Connection type (`svrtype`)
`GATE` → `ACCOUNT` → `LOBBY`/`MENU`, confirmed in the flow above as literal string arguments at each stage
transition. `NETCLIENT` is a separate internal pseudo-command tag (not a real `svrtype` value), also sent
through the same wrapper.

### Other small enums (from earlier proximity-mining, lower confidence — see caveat)
`logoutstat`: `FIN`/`MOV`/`NONE`. Team category: `ALL`/`NATIONAL`/`NATIONALCLUB`/`CLUB`. Match/room type:
`OC_RANKING`/`OC_FREE`/`LEGENDS`/`COMMU`/`COMMU_LEAGUE`. These were *not* re-verified against decompiled
code in this pass — treat as plausible leads only. (The match-**phase** sequence, `ENTRY`/`1ST`/`BREAK1`/
`2ND`/.../`END`, *was* re-verified this pass — see [Verified enum tables](#verified-enum-tables) — and
turned out to have one extra value, `1ST`/`2ND`, that the earlier mining pass had missed.)

---

## Competition League (COMPLG) reachability

Checked specifically because the client visibly doesn't offer this feature by default: is it gated by a
server-sent flag, or is the code just not wired in?

**`CMD_GET_SVRLIST`'s response carries no such flag.** Its real parser (`FUN_0076b2a0`) only reads
`server_num` and, per entry, `svrlist[i].svrtype/svrname/svrport/svraddr/max_player_num/player_num/svrgid`
— pure connection routing. The client also only forwards entries whose `svrtype` is `MENU`, `ACCOUNT`, or
`VDPCHAT`; anything else present in the list is silently dropped by this build.

**The real cause: the menu path into COMPLG is dead code, not conditionally hidden.**

| Function | Sends | Live references (call or data) in the whole binary |
|---|---|---|
| `0x007432c0` | `CMD_GET_COMPLG_KIND_LIST` | **none** |
| `0x0073e920` | `CMD_GET_COMPLG_LIST` | **none** |
| `0x0073fc10` | `CMD_SET_COMPLG_TEAM` | **none** |
| `0x00740650` | `CMD_WATCH_COMPLG_END` | **none** |
| `0x00741ad0` | `CMD_GET_COMPLG_TEAM_DATA` | called from `FUN_007a9340` (a small state-machine class) |
| `FUN_007a9340`'s vtable (2 copies, `0x0114ea4c`/`0x01157abc`) | — | **none** — the class itself is never instantiated |

The UI scene-name registry (`netMainmenu`, `netRoomCreate`, `netMatching`, `net_lobby_select`,
`net_server_select`, etc. — the same table `CMD_WATCH_ROOMLIST` and friends live next to) has **no**
`netComplg`/competition entry. A plain `"COMPETITION"` string exists in the binary, but it's in the generic
text/label table, unconnected to any menu item.

**Conclusion:** the protocol commands and a few backing C++ classes for Competition League are compiled
into the client, but the menu navigation path that would trigger them was removed/never wired up in this
build. It's not something a server response can toggle back on — publicly, PES2010's online competitions
were real, time-limited events Konami ran server-side during the game's live years, but the reachable
client-side trigger for them isn't present in this executable's menu graph.

**Not yet checked**: whether `CMD_JOIN_COMPLG` / `CMD_CREATE_COMPLG` (joining/creating a specific
competition, as opposed to browsing the list) have any live callers independent of the browse screen.

---

## Known gaps

- **Response field names not yet resolved** for `CMD_GET_SVRLIST`, `CMD_GET_SVRVERSION`, `CMD_GET_SVRTIME`,
  `CMD_GET_INFORMATIONLIST`, `CMD_GET_URLLIST`, `CMD_GET_RANKING`, `CMD_GET_GAMERESULTS`,
  `CMD_GET_GAMEADDPOINT` — each hands its response to a dedicated parser function that exists but wasn't
  itself decompiled. Request-side fields for all of these are confirmed. (The 7 entry-game watch pushes that
  were in this list last pass are now fully resolved — see [Room lifecycle](#room-lifecycle).)
- **`CMD_ADD_SCORE`**: all 7 key *names* are certain (including the `HOME`/`AWAY` enum values). Which of the
  4 raw event fields (`event[9]`, `event[0xb]`, `event[5]`, `event[7]`) fills which of the 4 "side" keys is
  inferred from call/computation order, not proven at the disassembly-register level — the only remaining
  ambiguity in this whole command.
- **`CMD_SET_PLAYERPROFILE`**: reuses the literal key `"svrtype"` for what's actually a profile-index value
  — flagged as ambiguous, not asserted as a real service-tag reuse.
- **`CMD_JOIN_BLOCK`**'s `nat` value provenance — it's read from a cached struct field, but the
  NAT-detection code that originally populates it wasn't traced.
- Community/League section is request-only; nobody has decompiled the response parsers for that subsystem
  yet.
- 28 commands from pass 1 (mostly `CMD_SEND_*`/`CMD_SET_*` one-way calls and some `CMD_WATCH_*`) have no
  verified data here at all — they weren't among the 123 call sites found, meaning either they're sent from
  a code path outside the normal UI flow (e.g. peer-to-peer relay) or via a helper this survey didn't reach.

### PES2010 room menu: "Match Room Manager" (2026-10-26)

- The room menu (FUN_008E6540, 9 items at 0x017018E8; item 4 = 0x01F9003E) enables
  Match Room Manager only when it is built, from FUN_00728e20 -> FUN_0075a950: the
  player's slot in the room-list cache (DAT_016f02bc+0x400, 1000 bytes per room; slot
  i at +0xb8+i*0x60, pid at +8, owner flag at +0xc) must have the owner flag. The
  refresh (FUN_008e6430) re-checks only Start Match and Select sides.
- The cache is rebuilt by FUN_00733eb0 (`room_num` / `rlist[i]`) from the client's
  own room list, which comes out empty while the room is being created. An update
  sent with CMD_CREATEJOIN_ROOM (before or after the reply) is lost. The create flow
  (FUN_007b9950) then sends CMD_SET_GAMEENV and builds the menu after that reply,
  so the server sends the owner its MSG_ROOMLISTUPDATE there. Confirmed live with CE.
