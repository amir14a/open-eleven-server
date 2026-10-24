# PES 2011 PC (`pes2011.exe`) — Complete CMD Reference

Generated from static analysis of `pes2011.exe` (image base `0x400000`) with Ghidra, cross-checked with live Cheat Engine breakpoints during the OpenEleven server bring-up.
Companion to [`pes2011-session-protocol.md`](./pes2011-session-protocol.md) (wire framing, per-phase narrative) and [`pes2011-implementation-status.md`](./pes2011-implementation-status.md).

Response-field attributions: **[C]** = the command send function is an ancestor of that parser in the call graph; **[I]** = matched by field names only (verify against a capture before relying on it). Enum names next to response keys are shown only where the enum agrees with the key known domain.

Confidence tags: **[V]** read directly from decompiled code / data tables; **[C]** derived through the call graph (command to response parser); **[I]** inferred from field names / behaviour; **[?]** unknown.

## 0. How this was extracted (so you can re-run it)

1. **Command names** — every `CMD_*` literal in the binary (246 distinct) plus the 4 name tables the sender walks (`0x014e4328`: `CMD_COMMON_SENDRECV` 212 names, `CMD_COMMON_WATCH` 31, `CMD_COMMON_REQWATCH` 4, `CMD_COMMON_REQWATCHL` 8). Plus 9 bespoke classes (`CONNECT`, `SENDECHO`, `SENDTEXT`, `KEEPLASTRCV`, `NETCLIENT`, `CMD_AUTH_GAMEID`, `CMD_DISCONNECT`, `CMD_DOWNLOAD_AD`, `CMD_GET_HTTP`, `CMD_GET_MYPLATFORMID`, `CMD_GET_RANKING`, `CMD_GET_COMMU_MEMBERSRESULTS_VS`, `CMD_GET_COMMU_TEAMRESULTS_VS`, `CMD_LOGIN`, `CMD_REG_SVRLIST`, `CMD_SEND_PCSPEC`, `CMD_SET_DUMMYSVR_REPLY`, `CMD_START_QUICKMATCH`, `CMD_CONNECT_SVR`) selected in `FUN_0100cd40`.
2. **Request parameters** — all 220 functions that call the message sender `FUN_00541e80(name)` were decompiled. Between the "begin message" call `FUN_00541eb0()` and the send, each setter call names one key: `FUN_00541ed0`/`ef0` = string (quoted), `FUN_00541f10` = uint, `FUN_00541f30` = int, `FUN_00541f90` = raw sub-object/list, enum values come from `FUN_0100b870(typeId, index)`.
3. **Response fields** — 192 functions that open the response KV parser (`FUN_00c79e50`) and read keys with `FUN_00c7a330/3d0` (int), `FUN_00c7a8c0` (string), `FUN_0100b8a0(type,str)` (string→enum). Each parser is attached to a command through the call graph where possible **[C]**, else by field-name inspection **[I]**.
4. **Errors** — the 12-byte rows at `0x012ebad8..` (`{cmd, ERR_name, dialog-msg-id}`, 483 rows) give the exact list of `result="ERR_*"` values the client knows for each command **[V]**.
5. **Ordering / links** — the per-feature state machines (`switch(state)` with `FUN_00521850(next)`) were parsed to get "command B is sent after A" edges **[V]**.
6. **Enum ranges** — the master enum tables (`PTR_PTR_01583e78[type]`, counts at `0x013bef08`), 77 types (0x00–0x4c) **[V]**.

Re-run: see `tools/pes2011-cmd-extract/README.md` (Ghidra inline script + Python generator). The raw outputs are the tables in this document; a machine-readable copy is `pes2011-cmd-reference.json`.

## 1. Message envelope & global rules

* Every request is a KV text line: `msg="CMD_X",rqid=<n>,svrtype="<MENU|ACCOUNT|GATE|VDPCHAT>",<fields…>` — `FUN_0056c5d0` writes `msg`, `FUN_00538a90` (called by "begin message" `FUN_00541eb0`) writes `svrtype` from the current connection **[V]**. So **nearly every request carries `svrtype`** even when it is not listed in the per-command tables below (those tables list only the explicitly added keys, with `svrtype` kept where the client sets it itself).
* Responses always carry `result="NOERR"` or `result="ERR_*"`; the client treats anything but `NOERR` as failure (`FUN_00522110` = `result != "NOERR"` **[V]**). On error the whole response text is stored (`FUN_00522170`) and the generic error dialog is shown (`FUN_005405c0`).
* `rqid` must be echoed in the reply. `msg` is echoed for pushes (`WATCH`).
* Client-side transport errors are synthesised locally and never come from the server: `ERR_CLIENT_OPTFORMAT` (request malformed), `ERR_CLIENT_SVRFORMAT` (reply unparsable), `ServerFormatErr` (no `result`), `ServerResultErr` (watch-list reply missing `list_type`/count — the bug fixed in this session).
* Boolean fields are the strings `YES`/`NO` (enum 0x4c); enums are sent/received as their **names**, never numbers (see §2).
* **Large messages are chunked, not rejected.** One frame body is <= 0x400 bytes, but the client reassembles multi-frame messages (packets 0x70 start / 0x71 continue / 0x72 end, up to 0x19000 = 102400 bytes) and can zlib-inflate them (flag byte). Exact layout in `pes2011-session-protocol.md` section 1.5 **[V]**. OpenEleven does not implement this yet: it sends one frame per message and ignores inbound packets other than 0x60.
* Command classes: **SENDRECV** (client request → one reply), **WATCH** (server push, no request), **REQWATCH** (client registers for a push stream), **REQWATCHL** (client registers a *list* watch; reply is an upsert/delete delta keyed by an id field, see §4).

## 2. Enumerations (value ranges) **[V]**

Index = position in the table (the on-wire value is the **name**). `FUN_0100b870(type,idx)` index→name, `FUN_0100b8a0(type,name)` name→index.

* **0x00**  (25): 0=HOUR_NONE · 1=HOUR_00_01 · 2=HOUR_01_02 · 3=HOUR_02_03 · 4=HOUR_03_04 · 5=HOUR_04_05 · 6=HOUR_05_06 · 7=HOUR_06_07 · 8=HOUR_07_08 · 9=HOUR_08_09 · 10=HOUR_09_10 · 11=HOUR_10_11 · 12=HOUR_11_12 · 13=HOUR_12_13 · 14=HOUR_13_14 · 15=HOUR_14_15 · 16=HOUR_15_16 · 17=HOUR_16_17 · 18=HOUR_17_18 · 19=HOUR_18_19 · 20=HOUR_19_20 · 21=HOUR_20_21 · 22=HOUR_21_22 · 23=HOUR_22_23 · 24=HOUR_23_00
* **0x01**  (2): 0=FALSE · 1=TRUE
* **0x02**  (3): 0=YELLOW · 1=RED · 2=NONE
* **0x03**  (8): 0=BLOCK · 1=ROOM · 2=TEAM · 3=GAME_QUICK · 4=TEAM_QUICK · 5=COMPETITION · 6=COMMUNITY · 7=COMMUNITY_LEAGUE
* **0x04**  (2): 0=CHAT_BAN · 1=CHAT_SEND
* **0x05**  (3): 0=LOGIN · 1=LOGOUT · 2=COMMU_DELETED
* **0x06**  (10): 0=TEAM_RANK_CNT_MATCH · 1=TEAM_RANK_CNT_WIN · 2=TEAM_RANK_CNT_LOSE · 3=TEAM_RANK_CNT_DRAW · 4=TEAM_RANK_AVR_WIN · 5=TEAM_RANK_AVR_SCORE · 6=TEAM_RANK_AVR_LOST · 7=TEAM_RANK_AVR_SHOT · 8=TEAM_RANK_AVR_SHOTED · 9=TEAM_RANK_AVR_POSSESSION
* **0x07**  (20): 0=USER_RANK_CNT_MATCH · 1=USER_RANK_CNT_WIN · 2=USER_RANK_CNT_LOSE · 3=USER_RANK_CNT_DRAW · 4=USER_RANK_AVR_WIN · 5=USER_RANK_AVR_SCORE · 6=USER_RANK_AVR_LOST · 7=USER_RANK_AVR_ASSIST · 8=USER_RANK_AVR_FOUL · 9=USER_RANK_AVR_YC · 10=USER_RANK_AVR_RC · 11=USER_RANK_AVR_SHOT · 12=USER_RANK_AVR_SHOTED · 13=USER_RANK_AVR_POSSESSION · 14=USER_RANK_WIN_LEAGUE · 15=USER_RANK_WIN_CUP · 16=USER_RANK_CONTINUOUS_WIN · 17=USER_RANK_CONTINUOUS_LOSE · 18=USER_RANK_TOTAL_COMBINATION · 19=USER_RANK_BEST_COMBINATION
* **0x08**  (4): 0=PLATINUM · 1=GOLD · 2=SILVER · 3=BRONZE
* **0x09**  (2): 0=WEEKRANK · 1=DIVISION
* **0x0a**  (80): 0=UEFA_Champions_League · 1=International_Club_Championship · 2=International_Cup_Online · 3=KONAMI_CHAMPIONSHIP · 4=KONAMI_CUP_ONLINE · 5=WEPES_CLUB_OPEN · 6=WEPES_NATIONAL_OPEN · 7=WEPES_CHAMPIONSHIP · 8=D1_Cup · 9=D2_Cup · 10=D3_Cup · 11=D4_Cup · 12=D5_Cup · 13=ML_UEFA_Champions_League · 14=ML_Open · 15=ML_Open_coach_mode · 16=ML_D1_Cup · 17=ML_D1_Cup_coach_mode · 18=ML_D2_Cup · 19=ML_D2_Cup_coach_mode · 20=ML_D3_Cup · 21=ML_D3_Cup_coach_mode · 22=ML_D4_Cup · 23=ML_D4_Cup_coach_mode · 24=ML_D5_Cup · 25=ML_D5_Cup_coach_mode · 26=RESERVED_CUP01 · 27=RESERVED_CUP02 · 28=RESERVED_CUP03 · 29=RESERVED_CUP04 · 30=RESERVED_CUP05 · 31=RESERVED_CUP06 · 32=RESERVED_CUP07 · 33=RESERVED_CUP08 · 34=RESERVED_CUP09 · 35=RESERVED_CUP10 · 36=RESERVED_CUP11 · 37=RESERVED_CUP12 · 38=RESERVED_CUP13 · 39=RESERVED_CUP14 · 40=RESERVED_CUP15 · 41=RESERVED_CUP16 · 42=RESERVED_CUP17 · 43=RESERVED_CUP18 · 44=RESERVED_CUP19 · 45=RESERVED_CUP20 · 46=RESERVED_CUP21 · 47=RESERVED_CUP22 · 48=RESERVED_CUP23 · 49=RESERVED_CUP24 · 50=RESERVED_CUP25 · 51=RESERVED_CUP26 · 52=RESERVED_CUP27 · 53=RESERVED_CUP28 · 54=RESERVED_CUP29 · 55=RESERVED_CUP30 · 56=RESERVED_CUP31 · 57=RESERVED_CUP32 · 58=RESERVED_CUP33 · 59=RESERVED_CUP34 · 60=RESERVED_CUP35 · 61=RESERVED_CUP36 · 62=RESERVED_CUP37 · 63=RESERVED_CUP38 · 64=RESERVED_CUP39 · 65=RESERVED_CUP40 · 66=RESERVED_CUP41 · 67=RESERVED_CUP42 · 68=RESERVED_CUP43 · 69=RESERVED_CUP44 · 70=RESERVED_CUP45 · 71=RESERVED_CUP46 · 72=RESERVED_CUP47 · 73=RESERVED_CUP48 · 74=RESERVED_CUP49 · 75=RESERVED_CUP50 · 76=RESERVED_CUP51 · 77=RESERVED_CUP52 · 78=RESERVED_CUP53 · 79=RESERVED_CUP54
* **0x0b**  (5): 0=NOW_PLAY · 1=NOW_WATCH · 2=OLD_ALL · 3=OLD_MY · 4=SCHEDULE
* **0x0c**  (4): 0=MATCH_1 · 1=MATCH_2 · 2=MATCH_3 · 3=MATCH_4
* **0x0d**  (2): 0=NORMAL · 1=ML
* **0x0e**  (12): 0=NOCREATE · 1=CREATED · 2=PERMITSTART · 3=ENTRY · 4=STARTED · 5=ENTRY_CLOSED · 6=MATCH_CLOSED · 7=FINISHED · 8=CANSELED · 9=END · 10=CLEARED · 11=DELETED
* **0x0f**  (3): 0=SECOND_ROUND_EXIT · 1=FINAL_ADVANCEMENT · 2=END_PLAYOFF
* **0x10**  (3): 0=TIME_5 · 1=TIME_10 · 2=TIME_15
* **0x11**  (7): 0=VICTORY · 1=SECOND_PLACE · 2=BEST_4 · 3=BEST_8 · 4=FINAL_ADVANCEMENT · 5=SECOND_ROUND_EXIT · 6=NON
* **0x12**  (80): 0=UEFA_Champions_League · 1=International_Club_Championship · 2=International_Cup_Online · 3=KONAMI_CHAMPIONSHIP · 4=KONAMI_CUP_ONLINE · 5=WEPES_CLUB_OPEN · 6=WEPES_NATIONAL_OPEN · 7=WEPES_CHAMPIONSHIP · 8=D1_Cup · 9=D2_Cup · 10=D3_Cup · 11=D4_Cup · 12=D5_Cup · 13=ML_UEFA_Champions_League · 14=ML_Open · 15=ML_Open_coach_mode · 16=ML_D1_Cup · 17=ML_D1_Cup_coach_mode · 18=ML_D2_Cup · 19=ML_D2_Cup_coach_mode · 20=ML_D3_Cup · 21=ML_D3_Cup_coach_mode · 22=ML_D4_Cup · 23=ML_D4_Cup_coach_mode · 24=ML_D5_Cup · 25=ML_D5_Cup_coach_mode · 26=RESERVED_CUP01 · 27=RESERVED_CUP02 · 28=RESERVED_CUP03 · 29=RESERVED_CUP04 · 30=RESERVED_CUP05 · 31=RESERVED_CUP06 · 32=RESERVED_CUP07 · 33=RESERVED_CUP08 · 34=RESERVED_CUP09 · 35=RESERVED_CUP10 · 36=RESERVED_CUP11 · 37=RESERVED_CUP12 · 38=RESERVED_CUP13 · 39=RESERVED_CUP14 · 40=RESERVED_CUP15 · 41=RESERVED_CUP16 · 42=RESERVED_CUP17 · 43=RESERVED_CUP18 · 44=RESERVED_CUP19 · 45=RESERVED_CUP20 · 46=RESERVED_CUP21 · 47=RESERVED_CUP22 · 48=RESERVED_CUP23 · 49=RESERVED_CUP24 · 50=RESERVED_CUP25 · 51=RESERVED_CUP26 · 52=RESERVED_CUP27 · 53=RESERVED_CUP28 · 54=RESERVED_CUP29 · 55=RESERVED_CUP30 · 56=RESERVED_CUP31 · 57=RESERVED_CUP32 · 58=RESERVED_CUP33 · 59=RESERVED_CUP34 · 60=RESERVED_CUP35 · 61=RESERVED_CUP36 · 62=RESERVED_CUP37 · 63=RESERVED_CUP38 · 64=RESERVED_CUP39 · 65=RESERVED_CUP40 · 66=RESERVED_CUP41 · 67=RESERVED_CUP42 · 68=RESERVED_CUP43 · 69=RESERVED_CUP44 · 70=RESERVED_CUP45 · 71=RESERVED_CUP46 · 72=RESERVED_CUP47 · 73=RESERVED_CUP48 · 74=RESERVED_CUP49 · 75=RESERVED_CUP50 · 76=RESERVED_CUP51 · 77=RESERVED_CUP52 · 78=RESERVED_CUP53 · 79=RESERVED_CUP54
* **0x13**  (4): 0=NORMAL_END · 1=BEFORE_ERR · 2=GAMEING_ERR · 3=TIMEOUT
* **0x14** condition (2): 0=NORMAL · 1=RANDOM
* **0x15** CPU level (6): 0=VERYEASY · 1=EASY · 2=NORMAL · 3=HARD · 4=VERYHARD · 5=HARDEST
* **0x16** division (6): 0=D5 · 1=D4 · 2=D3 · 3=D2 · 4=D1 · 5=NONE
* **0x17** frame rate (2): 0=FRAME_50 · 1=FRAME_60
* **0x18**  (3): 0=Friend · 1=SentReq · 2=RecievedReq
* **0x19** side (2): 0=HOME · 1=AWAY
* **0x1a** game phase (11): 0=ENTRY · 1=1ST · 2=BREAK1 · 3=2ND · 4=BREAK2 · 5=EX1ST · 6=BREAK3 · 7=EX2ND · 8=BREAK4 · 9=PK · 10=END
* **0x1b** game time (7): 0=NOSET · 1=5MINUTES · 2=10MINUTES · 3=15MINUTES · 4=20MINUTES · 5=25MINUTES · 6=30MINUTES
* **0x1c** language (18): 0=EN · 1=FR · 2=GR · 3=IT · 4=SP · 5=JP · 6=KR · 7=US · 8=PR · 9=MX · 10=RU · 11=HE · 12=SV · 13=NL · 14=BR · 15=TR · 16=CH · 17=ANY
* **0x1d**  (2): 0=LOGOUT · 1=LEGENDS_ROOM
* **0x1e** limit time (4): 0=NOSET · 1=SHORT · 2=MIDDLE · 3=LONG
* **0x1f** list_type (watch-list delta) (3): 0=ADD · 1=DEL · 2=UPDATE
* **0x20**  (2): 0=OC · 1=FD
* **0x21** login style (3): 0=LOGINID · 1=NPID · 2=XUID
* **0x22**  (3): 0=NONE · 1=FIN · 2=MOV
* **0x23**  (17): 0=MAIL_NORMAL · 1=CUP_WR_FINALWIN · 2=CUP_WR_FINALPRIZE · 3=CUP_WR_NORMALWIN · 4=CUP_WR_NORMALPRIZE · 5=CUP_WR_PREPASS · 6=CUP_WR_GOAL · 7=CUP_WR_ASSIST · 8=CUP_NOREPLAY · 9=CUP_NORMALEND · 10=COUTION · 11=MAILBOX_FLG · 12=CUP_MUKOUEND · 13=CUP_MLONLY · 14=GROUP_BANISH · 15=MAIL_REP_NOMINATED · 16=MAIL_REP_RANKEDHIGHT
* **0x24**  (2): 0=GOAL · 1=WIN
* **0x25**  (2): 0=RANK_MATCH · 1=COMPE_MATCH
* **0x26** match type (5): 0=OC_RANKING · 1=OC_FREE · 2=LEGENDS · 3=COMMU · 4=COMMU_LEAGUE
* **0x27** ML division (5): 0=ML_DIVISION_5 · 1=ML_DIVISION_4 · 2=ML_DIVISION_3 · 3=ML_DIVISION_2 · 4=ML_DIVISION_1
* **0x28**  (9): 0=TEAM_CREATE · 1=TEAM_NAME · 2=TEAM_RESET · 3=DIVISION_UP · 4=DIVISION_DOWN · 5=BUY · 6=SELL · 7=COMPE · 8=REGULAR
* **0x29** foot (4): 0=NONE · 1=RIGHT · 2=LEFT · 3=BOTH
* **0x2a**  (3): 0=UP · 1=DOWN · 2=FLAT
* **0x2b**  (4): 0=CREATE · 1=EDIT · 2=DIVISION_UP · 3=DIVISION_DOWN
* **0x2c**  (3): 0=DAY · 1=WEEK · 2=MONTH
* **0x2d**  (2): 0=DESC · 1=ASC
* **0x2e**  (12): 0=CF · 1=ST · 2=WG · 3=OH · 4=SH · 5=CH · 6=WB · 7=DH · 8=SB · 9=CB · 10=SW · 11=GK
* **0x2f**  (2): 0=TEAM_POWER · 1=TEAM_FUNDS
* **0x30**  (2): 0=OR · 1=AND
* **0x31**  (3): 0=OFF · 1=ON · 2=FUZZY
* **0x32**  (7): 0=PLAYER_NAME · 1=POSITION · 2=HEIGHT · 3=AGE · 4=OVERALL_RATING · 5=MARKET_VALUE · 6=NATIONALITY
* **0x33**  (3): 0=NO · 1=RIGHT · 2=LEFT
* **0x34**  (2): 0=NONE · 1=CREATED
* **0x35**  (6): 0=TEAM_TYPE_4_3_1_2 · 1=TEAM_TYPE_4_4_1_1 · 2=TEAM_TYPE_4_1_2_3 · 3=TEAM_TYPE_3_4_1_2 · 4=TEAM_TYPE_3_3_2_2 · 5=TEAM_TYPE_5_4_1
* **0x36** game type (13): 0=NONE · 1=NETWORK · 2=COMPE · 3=COMMUNITY · 4=COMMUNITY_COMPE · 5=LEGENDS · 6=LEGENDS_AUTO · 7=QUICK_RANK1VS1 · 8=QUICK_RANK2VS2 · 9=QUICK_FREE · 10=ML · 11=ML_QUICK_RANK · 12=ML_COMPE
* **0x37** NAT type (8): 0=OPEN_INTERNET · 1=SYMMETRIC_UDP_FIREWALL · 2=SYMMETRIC_OPEN · 3=FULL_CONE_NAT · 4=OPEN_CHOICE_PORT · 5=RESTRICTED_CONE_NAT · 6=RESTRICTED_PORT_CONE_NAT · 7=SYMMETRIC_NAT
* **0x38** room member event (5): 0=JOIN · 1=LEAVE · 2=DEL · 3=COMMUNICATIONFAILED · 4=MIGRATE
* **0x39** password type (2): 0=PASS_PLAIN · 1=PASS_AES
* **0x3a**  (3): 0=LOW · 1=MID · 2=HIGH
* **0x3b** player kind (4): 0=NORMAL · 1=GM · 2=EVENTER · 3=KONAMISTAFF
* **0x3c** platform (4): 0=PS2 · 1=PS3 · 2=PC · 3=X360
* **0x3d** self-report skill (5): 0=BEGINNER · 1=AMA · 2=PRO · 3=LEG · 4=STAR
* **0x3e** position (13): 0=GK · 1=LIB · 2=SW · 3=CB · 4=SB · 5=DMF · 6=WB · 7=CMF · 8=SMF · 9=OMF · 10=WG · 11=ST · 12=CF
* **0x3f**  (2): 0=NOT_FILL · 1=FILL
* **0x40** quickmatch kind (6): 0=OC_Rank · 1=OC_Free · 2=FD_Free · 3=COMPE · 4=ML_Rank · 5=ML_COMPE
* **0x41** quickmatch game time (2): 0=GT05 · 1=GT10
* **0x42**  (4): 0=LOGINMEMBER_NUM · 1=MATCH_NUM · 2=LEGENDS_MATCH_NUM · 3=LEAGUE_NUM
* **0x43** region (4): 0=REGION_ALL · 1=REGION_EU · 2=REGION_NA · 3=REGION_ASIA
* **0x44** room status (6): 0=WAITING · 1=SETENV · 2=GAME · 3=RESULT · 4=DISCONWAIT · 5=RESULTWAIT
* **0x45** room state event (4): 0=OwnerChanged · 1=AppointOwner · 2=Kicked · 3=StateChanged
* **0x46**  (3): 0=FORWARD · 1=PART · 2=PERFECT
* **0x47**  (2): 0=NAME · 1=COMMENT
* **0x48**  (2): 0=SELL · 1=BUY
* **0x49** svrtype (4): 0=GATE · 1=ACCOUNT · 2=MENU · 3=VDPCHAT
* **0x4a** gender (2): 0=MAN · 1=WOMAN
* **0x4b** url type (21): 0=RANK · 1=RANK_WEEK · 2=PC_SPEC · 3=DOWNLOAD · 4=DOWNLOAD_DESC · 5=KONAMIID · 6=KONAMIID_ALTER · 7=GAMEID_AUTH · 8=VIDEO · 9=EULA_PES · 10=COMMU_MEMBERS_VS · 11=COMMU_TEAM_VS · 12=HTTP_TEST · 13=ADFILE · 14=PATCH_DL · 15=BANNER · 16=CUP · 17=COMPE_DIGEST_UL · 18=COMPE_DIGEST_DL · 19=RECORD · 20=P2PMATCH
* **0x4c** YES/NO bool (2): 0=NO · 1=YES

## 3. Watch lists (`CMD_COMMON_REQWATCHL`) **[V]**

The client sends the shape in the request itself; the server must answer with exactly those key names.

| Command | request extra fields | max entries |
|---|---|---|
| `CMD_REQWATCHL_ROOMLIST` | `listnum_name="room_list_num",list_name="room_list",listid_name="room_id"` | 100 |
| `CMD_REQWATCHL_ROOMPLAYERLIST` | `listnum_name="roomplayer_list_num",list_name="roomplayer_list",listid_name="pid"` | 8 |
| `CMD_REQWATCHL_BLOCKPLAYERLIST` | `listnum_name="blockplayer_list_num",list_name="blockplayer_list",listid_name="pid"` | 100 |
| `CMD_REQWATCHL_COMMU_MATCHLIST` | `listnum_name="match_list_num",list_name="match_list",listid_name="match.match_id"` | 9 |
| `CMD_REQWATCHL_COMMU_MEMBERLIST` | `listnum_name="member_list_num",list_name="member_list",listid_name="info.pid"` | 32 |
| `CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST` | `listnum_name="match_list_num",list_name="match_list",listid_name="match_id"` | 300 |
| `CMD_REQWATCHL_COMMU_COMPEMEMBERLIST` | `listnum_name="member_list_num",list_name="member_list",listid_name="info.pid"` | 32 |
| `CMD_REQWATCHL_COMPLG_MATCHLIST` | `listnum_name="match_list_num",list_name="match_list",listid_name="match_id"` | 8 |

Reply (`FUN_011d63a0`): `result="NOERR"`, `<listnum_name>=N`, `list_type=ADD|DEL|UPDATE` (enum 0x1f; `DEL` removes by id, anything else upserts), then `<list_name>[0..N-1]` each containing the `<listid_name>` key. Missing `list_type` ⇒ client substitutes `ServerResultErr` and the lobby screen leaves (this is what made `CMD_LEAVE_LOBBY` fire). Later pushes for the same list arrive as `msg="CMD_REQWATCHL_…"` with the same shape.

## 4. Session flow (macro) **[V]/[C]**

```
NETCLIENT (CONNECT)
GATE    : CMD_REG_SVRLIST → CMD_GET_SVRLIST → CMD_GET_SVRVERSION → CMD_GET_SVRTIME → CMD_GET_URLLIST → [HTTP: eula_pes.txt, pcspec.bin, gameid_auth] → CMD_GET_INFORMATIONLIST → CMD_DISCONNECT
ACCOUNT : (CMD_AUTH_GAMEID if gameid-login flag) → CMD_DISCONNECT → CMD_LOGIN{style=LOGINID} (MSG_REQCCODE/MSG_REQAUTH challenge) → CMD_GET_PLAYERLIST → [CMD_CREATE_PLAYER → CMD_SET_LANGUAGE] → CMD_GET_PRIVATEINFO → CMD_GET_DIVISIONUPDATE
MENU    : CMD_DISCONNECT(ACCOUNT) → CMD_LOGIN(MENU) → CMD_SET_CURRENTPLAYER → CMD_SEND_IPANDPORT → CMD_GET_PLAYERINFO → CMD_GET_BLACKLIST/CMD_GET_FRIENDLIST → CMD_GET_QUICKMATCHOPTION
         → CMD_GET_LOBBYLIST → CMD_JOIN_LOBBY → CMD_GET_BLOCKLIST → CMD_JOIN_BLOCK → CMD_REQWATCHL_BLOCKPLAYERLIST + CMD_REQWATCHL_ROOMLIST
ROOM    : CMD_CREATEJOIN_ROOM | CMD_JOIN_ROOM → CMD_REQWATCHL_ROOMPLAYERLIST, CMD_GET_IPANDPORT, CMD_SET_GUEST, CMD_ENTRY_GAME, CMD_SET_GAMEENV → (P2P: CMD_SEND_P2PSTATUS/FAILED) → in-match CMD_CHANGE_GAMEPHASE / CMD_ADD_SCORE / CMD_ADD_FOUL / CMD_SET_GAMETEAM → CMD_GET_GAMEADDPOINT → CMD_LEAVE_ROOM
QUICK   : CMD_GET_QUICKMATCHNUMBERS / CMD_REQWATCH_QUICK_PLAYERNUM → CMD_START_QUICKMATCH → (MSG_QUICK_* pushes) → CMD_SET_GAMEMEMBERENV → CMD_SET_GAMEMEMBER → … same in-match tail; cancel = CMD_CANCEL_STARTQUICKMATCH / CMD_CANCEL_DECIDE_QUICKMATCH
```

The per-command "Follows / Followed by" lines in §6 are generated from the real state machines (`FUN_00521850(next)` transitions), so they show the exact order the client uses inside one feature.

## 5. ID / parameter provenance (which response field feeds which request field)

| ID field | produced by (response) | consumed by (request) |
|---|---|---|
| `pid` | `CANCEL_DECIDE_QUICKMATCH`, `CANCEL_STARTQUICKMATCH`, `GET_BLACKLIST`, `GET_COMPLG_ASSISTRANKING`, `GET_COMPLG_GOALRANKING`, `GET_COMPLG_PRE_RESULTS`, `GET_COMPLG_TITLEHOLDER`, `GET_FD_GAMERESULTS`, `GET_FRIENDLIST`, `GET_GAMEADDPOINT` … | `DEL_COMMU_MEMBER`, `DEL_COMMU_LEAGUEMEMBER`, `GET_COMMU_MEMBERSRESULTS`, `GET_GAMEPLAN`, `GET_IPANDPORT`, `GET_BLACKLIST`, `GET_COMPLG_TEAM_DATA`, `GET_PLAYERINFO`, `GET_PRIVATEINFO`, `GET_GAMERESULTS`, `GET_FD_GAMERESULTS`, `GET_ML_MATCHRECORD` … |
| `npid` | `GET_BLACKLIST`, `GET_COMPLG_ASSISTRANKING`, `GET_COMPLG_GOALRANKING`, `GET_COMPLG_PRE_RESULTS`, `GET_COMPLG_TITLEHOLDER`, `GET_FRIENDLIST`, `REQWATCHL_BLOCKPLAYERLIST`, `WATCH_FRIENDLIST` | — |
| `target_pid` | — | `SEARCH_COMMU`, `GET_FRIENDLIST`, `KICK_ROOMMEMBER`, `DEL_BLACKLIST`, `ADD_BLACKLIST`, `GET_ML_DIVISIONRANKING_BYID` |
| `to_pid` | `GET_SENDSHORTMAIL`, `GET_SENDSHORTMAILLIST` | `SEND_SHORTMAIL`, `DEL_FRIENDREQ`, `SEND_FRIENDREQ` |
| `from_pid` | `GET_SHORTMAIL`, `GET_SHORTMAILLIST`, `SEND_TEXTCHAT`, `WATCH_TEXTCHAT` | `REPLY_FRIENDREQ`, `DEL_FRIEND` |
| `base_pid` | — | `GET_COMMU_MEMBERSRESULTS_VS` |
| `oppo_pid` | — | `GET_COMMU_MEMBERSRESULTS_VS` |
| `room_id` | `REQWATCHL_BLOCKPLAYERLIST`, `REQWATCHL_ROOMLIST` | `APPLY_COMMU_MATCH`, `JOIN_ROOM`, `REQWATCHL_ROOMPLAYERLIST` |
| `block_id` | `GET_BLOCKLIST` | `JOIN_BLOCK` |
| `lobby_id` | `GET_LOBBYLIST` | `JOIN_LOBBY` |
| `commu_id` | `GET_COMMU_INFO`, `GET_JOINEDCOMMULIST`, `SEARCH_COMMU` | `REQWATCHL_COMMU_MEMBERLIST`, `REQWATCHL_COMMU_COMPEMEMBERLIST`, `DEL_COMMU_MEMBER`, `SET_CURRENT_COMMU`, `JOIN_COMMU`, `GET_COMMU_INFO`, `GET_COMMU_MEMBERSRESULTS_VS`, `GET_COMMU_TEAMRESULTS_VS`, `WITHDRAW_COMMU` |
| `compe_id` | `GET_COMPLG_INFO`, `GET_COMPLG_LIST` | `REQWATCHL_COMMU_COMPEMEMBERLIST`, `GET_COMMU_PASTLEAGUERESULT`, `JOIN_COMMU_LEAGUE`, `END_COMMU_LEAGUE`, `REQWATCHL_COMMU_MATCHLIST`, `GET_COMMU_GOALRANKING`, `GET_COMMU_ASSISTRANKING`, `GET_COMPLG_INFO`, `GET_COMPLG_TEAMRANKING`, `JOIN_COMPLG`, `GET_COMPLG_TOURNAMENT`, `GET_COMPLG_GOALRANKING` … |
| `match_id` | `GET_MATCHID` | `APPLY_COMMU_MATCH`, `REPLY_COMMU_MATCH`, `CANCEL_COMMU_MATCH` |
| `team_id` | `GET_COMPLG_ASSISTRANKING`, `GET_COMPLG_GOALRANKING`, `GET_COMPLG_PRE_RESULTS`, `GET_COMPLG_TEAMRANKING`, `GET_COMPLG_TITLEHOLDER` | `GET_COMMU_TEAMRESULTS`, `SEARCH_ML_LEAGUE` |
| `gameplayer_id` | `GET_ML_MARKET_CHECKLIST`, `GET_ML_MARKET_GAINRANKING`, `GET_ML_MARKET_POPULARRANKING`, `GET_ML_MARKET_PRICERANKING`, `GET_ML_MYTEAM_GAMEPLAYERLIST`, `GET_ML_TRADEHISTORY`, `SEARCH_ML` | `DELETE_ML_MARKET_CHECKLIST`, `BUY_ML_MARKET_GAMEPLAYER`, `SELL_ML_MARKET_GAMEPLAYER`, `REQWATCH_ML_MARKET_PRICE`, `ADD_ML_MARKET_CHECKLIST`, `GET_ML_MARKET_GAMEPLAYERPRICEHISTORY`, `ACK_ML_MARKET_PRICE` |
| `smail_id` | `GET_SENDSHORTMAILLIST`, `GET_SHORTMAILLIST` | `DEL_SENDSHORTMAIL`, `GET_SENDSHORTMAIL`, `DEL_SHORTMAIL`, `GET_SHORTMAIL` |
| `cell_id` | — | `ENTRY_COMMU_LEAGUECELL`, `GET_COMMU_TEAM` |
| `base_team_id` | `GET_ML_DIVISIONRANKING`, `GET_ML_MYTEAM`, `GET_ML_RANKING`, `GET_ML_RANKING_BYID`, `GET_PLAYERINFO` | `GET_COMMU_TEAMRESULTS_VS`, `SET_ML_MYTEAM_BASE` |
| `history_id` | `GET_ML_MYTEAM_HISTORY` | `GET_ML_MYTEAM_GAMEPLAYERLIST_OLD` |

Concrete chains (all **[V]**/**[C]**): `lobby_id` ← `GET_LOBBYLIST.lobby_list[].lobby_id` → `JOIN_LOBBY`; `block_id` ← `GET_BLOCKLIST.bklist[].block_id` → `JOIN_BLOCK`; `room_id` ← `REQWATCHL_ROOMLIST.room_list[].room_id` → `JOIN_ROOM` / `REQWATCHL_ROOMPLAYERLIST` / `APPLY_COMMU_MATCH`; `pid` ← `REQWATCHL_BLOCKPLAYERLIST.blockplayer_list[].pid`, `REQWATCHL_ROOMPLAYERLIST.roomplayer_list[].pid`, friend/mail lists → `GET_PLAYERINFO`, `KICK_ROOMMEMBER.target_pid`, `SEND_SHORTMAIL.to_pid`, `SEND_FRIENDREQ.to_pid`, `ADD_BLACKLIST.target_pid`; `smail_id` ← `GET_(SEND)SHORTMAILLIST.smlist[].smail_id` → `GET/DEL_(SEND)SHORTMAIL`; `compe_id` ← `GET_COMPLG_LIST.compe_list[].compe_id` → `JOIN_COMPLG` / `GET_COMPLG_INFO` / rankings; `commu_id` ← `SEARCH_COMMU` / `GET_JOINEDCOMMULIST` → `JOIN_COMMU`, `SET_CURRENT_COMMU`; `gameplayer_id` ← `GET_ML_MYTEAM_GAMEPLAYERLIST` / `SEARCH_ML*` → `BUY/SELL_ML_MARKET_GAMEPLAYER`, checklist cmds.

## 6. Command reference

Legend — *Class*: S=SENDRECV, W=WATCH push, RW=REQWATCH, RWL=REQWATCHL, B=bespoke. *Server*: inferred routing (`svrtype` the client is connected to). *Impl*: ✅ real handler in OpenEleven, ◻ generic ack / not implemented (client will see whatever the generic fallback returns).

### 6.1 Gate

#### `CMD_GET_HTTP`
*Class* B · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_00604880`, `FUN_0061e620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `uri` | string |  |  |

Follows: `GET_SVRLIST` · Followed by: —

#### `CMD_GET_INFORMATIONLIST`
*Class* S · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_0061cf20`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | enum 0x43 | `REGION_ALL|REGION_EU|REGION_NA|REGION_ASIA` | `"GATE"` |
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |
| `region` | string | `REGION_ALL|REGION_EU|REGION_NA|REGION_ASIA` (inferred) | `"REGION_ALL"` |

Follows: — · Followed by: `DISCONNECT`

#### `CMD_GET_SVRLIST`
*Class* S · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_0061e620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `timeout` | uint |  | `20` ; `5` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"GATE"` |
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |

Response fields:
* `FUN_005104f0` **[C]**: `server_num`, `svrlist`, `svrtype`, `svrport`, `svraddr`, `max_player_num`, `player_num`, `svrgid`
* `FUN_0056cfd0` **[C]**: `server_num`, `svrlist[`, `svrtype`, `svrport`, `svraddr`, `max_player_num`, `player_num`, `svrgid`, `userCount`

Follows: `REG_SVRLIST` · Followed by: `GET_HTTP`, `GET_SVRVERSION`

#### `CMD_GET_SVRTIME`
*Class* S · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_0061e620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"GATE"` |

Response fields:
* `FUN_0050f610` **[C]**: `date`

Follows: `GET_SVRVERSION` · Followed by: `GET_URLLIST`

#### `CMD_GET_SVRVERSION`
*Class* S · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_0061e620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"GATE"` |

Response fields:
* `FUN_005102c0` **[C]**: `version`, `patch_version`, `dlc_version`, `eula_version`

Follows: `GET_SVRLIST` · Followed by: `GET_SVRTIME`

#### `CMD_GET_URLLIST`
*Class* S · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_0061e620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"GATE"` |
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |
| `region` | string | `REGION_ALL|REGION_EU|REGION_NA|REGION_ASIA` (inferred) | `"REGION_ALL"` |

Response fields:
* `FUN_00513de0` **[C]**: `url_num`, `ulist[`, `type` (enum 0x4b), `url`, `version`, `file_size`, `md5`

Follows: `GET_SVRTIME` · Followed by: —

#### `CMD_REG_SVRLIST`
*Class* B · *Server* GATE · *Impl* ✅ · *Sent from* `FUN_0061e620`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0056cfd0` **[C]**: `server_num`, `svrlist[`, `svrtype`, `svrport`, `svraddr`, `max_player_num`, `player_num`, `svrgid`, `userCount`

Follows: `DISCONNECT` · Followed by: `GET_SVRLIST`

#### `CMD_SEND_PCSPEC`
*Class* B · *Server* GATE · *Impl* ◻ · *Sent from* `FUN_0061bdb0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `uri` | string |  |  |
| `pcspec` | string | `LOW|MID|HIGH` (inferred) |  |

### 6.2 Account

#### `CMD_AUTH_GAMEID`
*Class* B · *Server* ACCOUNT · *Impl* ✅ · *Sent from* `FUN_0061c360`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameid` | string |  |  |
| `gameid_password` | string |  |  |
| `password_type` | string | `PASS_PLAIN|PASS_AES` (inferred) |  |
| `gameid_url_num` | uint |  |  |
| `gameid_url` | raw/object |  |  |

Response fields:
* `FUN_0050fac0` **[I]**: `loginid`, `loginid_password`, `aes_password`

Follows: — · Followed by: `DISCONNECT`, `LOGIN`

Errors **[V]**: `ERR_AUTHGID_URL`, `ERR_AUTHGID_SSLEXPIRE`, `ERR_AUTHGID_AUTH`, `ERR_AUTHGID_SSLETC`

#### `CMD_AUTH_NPTICKET`
*Class* S · *Server* ACCOUNT · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_GET_TICKETINFO`, `ERR_READ_PASSPHRASE`, `ERR_READ_CIPHERINFO`, `ERR_PREPARE_CIPHERINFO`, `ERR_INVALID_TICKET`

#### `CMD_CHECK_STRING`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_005ffb80`, `FUN_0061e100`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `str` | string |  |  |

Errors **[V]**: `ERR_INVALIDLETTER`

#### `CMD_CREATE_PLAYER`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_005f7de0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"ACCOUNT"` |
| `name` | string |  | `"marcos"` |

Follows: — · Followed by: `SET_LANGUAGE`

Errors **[V]**: `ERR_DATABASE`, `ERR_ALREADYEXISTS`, `ERR_INVALIDLETTER`, `ERR_NGWORDINPNAME`

#### `CMD_DEL_PLAYER`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_005f81a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) |  |

Follows: `LOGIN` · Followed by: —

Errors **[V]**: `ERR_DATABASE`, `ERR_NOPLAYER`, `ERR_DELINDAYS`

#### `CMD_GET_DIVISIONUPDATE`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_0061b940`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"ACCOUNT"` |
| `mypid` | int |  | `1` |

Response fields:
* `FUN_00512700` **[I]**: `updated` (enum 0x4c), `div_result.`, `newdiv`, `newrank`, `olddiv`, `oldrank`, `date`, `rating`, `div01`, `div02`, `div3A`, `div3B`, `div_result.divcount[`, `div_result.rankflag[`

Follows: `GET_PRIVATEINFO` · Followed by: —

#### `CMD_GET_KONAMIID`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_005ff200`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005fefd0` **[C]**: `konamiid` (<=511 bytes)

Errors **[V]**: `ERR_NOTREGISTERED`

#### `CMD_GET_PLAYERLIST`
*Class* S · *Server* ACCOUNT · *Impl* ✅ · *Sent from* `FUN_0061b940`, `FUN_0061c360`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"ACCOUNT"` |

Response fields:
* `FUN_0050f550` **[C]**: `player_count`
* `FUN_00511b90` **[I]**: `player_count`, `player_data`, `pid`, `date`, `point`, `name`, `match_num`, `division` (enum 0x16), `rating`

Follows: `LOGIN` · Followed by: `GET_PRIVATEINFO`, `SET_LANGUAGE`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_PRIVATEINFO`
*Class* S · *Server* ACCOUNT · *Impl* ✅ · *Sent from* `FUN_0061b940`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"ACCOUNT"` |
| `pid` | int |  | `1` |

Response fields:
* `FUN_00513000` **[I]**: `count`, `privInfo[`, `reldate`, `iid`, `title`, `info`, `important`

Follows: `GET_PLAYERLIST` · Followed by: `GET_DIVISIONUPDATE`

#### `CMD_LOGIN`
*Class* B · *Server* ACCOUNT · *Impl* ✅ · *Sent from* `FUN_006425c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) |  |
| `platform` | string | `PS2|PS3|PC|X360` (inferred) |  |
| `style` | string | `LOGINID|NPID|XUID` (inferred) |  |
| `loginid` | string |  |  |
| `loginid_password` | string |  |  |
| `use_regcode` | YES/NO | `NO|YES` |  |
| `regcode` | string |  |  |
| `patchver` | string |  |  |
| `dlcver` | string |  |  |
| `parahash` | string |  |  |
| `entryhash` | string |  |  |
| `dont_check_heartbeat` | YES/NO | `NO|YES` |  |

Follows: `AUTH_GAMEID`, `DISCONNECT` · Followed by: `DEL_PLAYER`, `GET_PLAYERLIST`, `SET_CURRENTPLAYER`

Errors **[V]**: `ERR_NOTGETCCODE`, `ERR_PARAMCHECK_FAILED`, `ERR_INVALID_ACCOUNT`, `ERR_DIFFERENT_PATCHVERSION`, `ERR_DEFCLIENTVER`, `ERR_DATABASE`, `ERR_LOGIN_FAILED`, `ERR_ALREADYLOGGEDIN`, `ERR_REGCODENOTISSUED`, `ERR_REGCODE_FAILED`, `ERR_SERVERNOTREADY`, `ERR_REGCODEDIFLASTTIME`

#### `CMD_SET_KONAMIID`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_005fec30`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `konamiid` | string |  |  |
| `konamiid_password` | string |  |  |

Errors **[V]**: `ERR_LOGIN_FAILED`, `ERR_ALREADYREGISTERED`

#### `CMD_SET_LANGUAGE`
*Class* S · *Server* ACCOUNT · *Impl* ✅ · *Sent from* `FUN_005f7de0`, `FUN_0061c360`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"ACCOUNT"` |
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |

Follows: `CREATE_PLAYER`, `GET_PLAYERLIST` · Followed by: —

#### `CMD_SET_PLAYERPROFILE`
*Class* S · *Server* ACCOUNT · *Impl* ◻ · *Sent from* `FUN_005ff6a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) |  |

Errors **[V]**: `ERR_INVALIDDATE`, `ERR_NGWORDININTRO`, `ERR_DATABASE`

### 6.3 Lobby / session

#### `CMD_DISCONNECT`
*Class* B · *Server* any · *Impl* ✅ · *Sent from* `FUN_005eea40`, `FUN_005f81a0`, `FUN_0061c360`, `FUN_0061cf20`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) |  |
| `logoutstat` | string |  | `"MOV"` ; `"FIN"` |

Follows: `AUTH_GAMEID`, `GET_INFORMATIONLIST` · Followed by: `LOGIN`, `REG_SVRLIST`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_DOWNLOAD_AD`
*Class* B · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005664e0`, `FUN_005dcb60`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `uri` | string |  |  |
| `filename` | string |  |  |

#### `CMD_GET_BLOCKLIST`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005ef0f0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Response fields:
* `FUN_00514350` **[I]**: `count`, `bklist[`, `player_num`, `max_player_num`
* `FUN_00514680` **[I]**: `count`, `bklist[`, `name`, `block_id`, `max_player_num`, `player_num`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_LOBBYLIST`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005efb60`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `lobby_type` | string |  | `"OC"` |

Response fields:
* `FUN_005ef7c0` **[C]**: `lobby_list_num`, `lobby_list[`, `lobby_id`, `name` (<=32 bytes), `player_num`

#### `CMD_GET_PLAYERINFO`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_00573650`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  | `1` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Response fields:
* `FUN_00572590` **[C]**: `xuid`, `name` (<=16 bytes), `match_num`, `win_num`, `lose_num`, `draw_num`, `contWin_num`, `contWinMax_num`, `disconnect_num`, `disconWin_num`, `disconLose_num`, `birthmonth`, `birthday`, `country`, `area`, `favoriteTeam`, `favoritePlayer`, `intro` (<=256 bytes), `kind` (enum 0x3b), `rating`, `manner`, `goal`, `lostGoal`, `rank`, `lang` (enum 0x1c), `enable_chat` (enum 0x4c), `teamLog[`, `total_combination`, `max_combination`, `dlcontent` (<=16 bytes), `patch_version` (<=16 bytes), `selfreport_level` (enum 0x3d), `automatch_want` (enum 0x4c), `beginnermark` (enum 0x4c), `position_want` (enum 0x3e), `desired_position[` (enum 0x4c), `top10_num`, `top100_num`, `FD_match_num`, `FD_goal`, `FD_assist`, `FD_top10_num`, `FD_top100_num`, `is_star` (enum 0x4c), `star_event_flag`, `team_name` (<=144 bytes), `base_team_id`, `team_power`, `team_resource`, `ml_division`, `ml_point`, `ml_win_num`, `ml_lose_num`, `ml_draw_num`, `ml_contWin_num`, `ml_contWinMax_num`, `ml_goal`, `ml_lostGoal`, `list_num`, `compe_list[`, `compe_name` (<=96 bytes), `compe_kind`, `cup_file_name` (<=32 bytes), `mode`, `prize_num`, `playoff_num`, `join_num`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_JOIN_BLOCK`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005ef3e0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `block_id` | uint |  | `0` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Errors **[V]**: `ERR_BLOCKNOTEXIST`, `ERR_BLOCKISFULL`, `ERR_DIVISIONREFUSED`

#### `CMD_JOIN_LOBBY`
*Class* S · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_005f0030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lobby_id` | int |  | `1` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Errors **[V]**: `ERR_LOBBYUSERMAX`

#### `CMD_LEAVE_LOBBY`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005efde0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

#### `CMD_REQWATCHL_BLOCKPLAYERLIST`
*Class* RWL · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005f6f80`

List watch shape: `listnum_name="blockplayer_list_num",list_name="blockplayer_list",listid_name="pid",list_max=100`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `listnum_name` | string |  | `"blockplayer_list_num"` |
| `list_name` | string |  | `"blockplayer_list"` |
| `listid_name` | string |  | `"pid"` |
| `list_max` | int |  | `100` |

Response fields:
* `FUN_00514a80` **[I]**: `blockplayer_list_num`, `blockplayer_list[`, `pid`, `name`, `division` (enum 0x16), `room_id`, `point`, `rating`, `mcount`, `win`, `lose`, `draw`, `kind`, `lang`, `npid`, `xuid`, `x36_ina`, `x36_ina_online`, `x36_port_online`, `x36_enet`, `x36_online`, `manner`, `selfreport_level` (enum 0x3d), `beginnermark` (enum 0x4c), `position_want` (enum 0x3e), `desired_position`, `player_name`

Errors **[V]**: `ERR_NOBLOCKBELONG`

#### `CMD_SEND_HEARTBEAT`
*Class* S · *Server* MENU · *Impl* ✅

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `result` | string |  | `"NOERR"` |

Sent periodically on every connection; reply `result="NOERR"` (+ echoed rqid).

#### `CMD_SEND_NOTICE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00621950`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid_list_num` | int |  |  |
| `data` | string |  |  |

#### `CMD_SET_CURRENTPLAYER`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005eea40`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |
| `frame_rate` | string | `FRAME_50|FRAME_60` (inferred) | `"FRAME_60"` |
| `pcspec` | string | `LOW|MID|HIGH` (inferred) | `"HIGH"` |
| `region` | string | `REGION_ALL|REGION_EU|REGION_NA|REGION_ASIA` (inferred) | `"REGION_ALL"` |
| `name` | string |  | `"marcos"` |

Response fields:
* `FUN_00513610` **[I]**: `useable_team_size`, `useable_team_id[`, `mailcount`, `newmail` (enum 0x4c), `chat_probi` (enum 0x4c), `camera_probi` (enum 0x4c), `voice_probi` (enum 0x4c), `band_probi` (enum 0x4c), `green_ping`, `green_band`, `green_loss`, `yellow_ping`, `yellow_band`, `yellow_loss`, `red_ping`, `red_band`, `red_loss`, `green_delay`, `yellow_delay`, `red_delay`, `p2ptimeout`, `first_login` (enum 0x4c), `lossframe_match`, `avg_max_combination`, `avg_total_combination`, `invite_num`, `check_lobbygame_ping_diff`, `check_game_ping_max`, `mcount10flg`, `delay_frame_2players`, `delay_frame_4players`, `max_input_delay_frames`, `proto_opt`

Follows: `LOGIN` · Followed by: `SEND_IPANDPORT`

Errors **[V]**: `ERR_REFUSED`, `ERR_DATABASE`, `ERR_NOPLAYER`

#### `CMD_SET_FDPROFILE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0061dbe0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `player_name` | string |  |  |
| `selfreport_level` | string | `BEGINNER|AMA|PRO|LEG|STAR` (inferred) |  |
| `automatch_want` | string |  |  |
| `beginnermark` | string |  |  |
| `position_want` | string | 13 values, see enum 0x3e (inferred) |  |
| `desired_position` | raw/object |  |  |
| `star_event_flag` | string |  |  |

#### `CMD_WATCH_EMERGENCY`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005213d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_MAINTETIME`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005213d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_NOTICE`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006015b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

### 6.4 Room

#### `CMD_CHANGE_ROOMNAME`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e02c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `name` | string |  |  |
| `passwd` | string |  |  |
| `legends_playstyle` | string |  |  |
| `is_legends_vs_player` | string |  |  |
| `team_name` | string |  |  |
| `short_teamname` | string |  |  |

Errors **[V]**: `ERR_NOTINROOM`, `ERR_NOTROOMOWNER`, `ERR_INVALIDARG`, `ERR_NGWORDINRNAME`, `ERR_ROOMNOTFOUND`, `ERR_DATABASE`

#### `CMD_CREATEJOIN_ROOM`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e5c20`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `name` | string |  | `"Room marcos"` |
| `match_type` | string | `OC_RANKING|OC_FREE|LEGENDS|COMMU|COMMU_LEAGUE` (inferred) | `"OC_RANKING"` |
| `team_flag` | raw/object |  | `["YES", "YES", "NO", ]` |
| `lang` | string | 18 values, see enum 0x1c (inferred) | `"EN"` |
| `max_players` | uint |  | `2` |
| `is_invite_limit` | string |  | `"NO"` |
| `password` | string |  | `""` |
| `enable_guest` | string |  | `"YES"` |
| `legends_playstyle` | string |  | `"EVERYONE"` |
| `is_legends_vs_player` | string |  | `"NO"` |
| `team_name` | string |  | `""` |
| `short_teamname` | string |  | `""` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Response fields:
* `FUN_005e7460` **[I]**: `name`, `match_type` (enum 0x26), `team_flag` (enum 0x4c), `is_invite_limit` (enum 0x4c), `team_name`, `short_teamname`, `is_legends_vs_player` (enum 0x4c), `lang`, `max_players`, `password`, `game_env`, `cpuLevel` (enum 0x15), `gametime` (enum 0x1b), `injury` (enum 0x4c), `condition` (enum 0x14), `ball_type`, `exGame` (enum 0x4c), `pkOnOff` (enum 0x4c), `substitution`, `limitTime` (enum 0x1e)

Errors **[V]**: `ERR_INVALIDARG`, `ERR_ALREADYINROOM`, `ERR_NGWORDINRNAME`, `ERR_ROOMTOOMANY`

#### `CMD_ENTRY_GAME`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e15a0`, `FUN_005e18c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `entry` | enum 0x01 | `FALSE|TRUE` |  |
| `side` | enum 0x19 | `HOME|AWAY` |  |

#### `CMD_GET_IPANDPORT`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005363c0`, `FUN_005e4d60`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Response fields:
* `FUN_0050f860` **[C]**: `ex_ip`, `ex_port`, `in_ip`, `in_port`, `pid`

Follows: — · Followed by: `LEAVE_ROOM`

#### `CMD_JOIN_ROOM`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e4d60`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `room_id` | int |  |  |
| `is_invited` | YES/NO | `NO|YES` |  |
| `password` | string |  |  |

Response fields:
* `FUN_005e7460` **[I]**: `name`, `match_type` (enum 0x26), `team_flag` (enum 0x4c), `is_invite_limit` (enum 0x4c), `team_name`, `short_teamname`, `is_legends_vs_player` (enum 0x4c), `lang`, `max_players`, `password`, `game_env`, `cpuLevel` (enum 0x15), `gametime` (enum 0x1b), `injury` (enum 0x4c), `condition` (enum 0x14), `ball_type`, `exGame` (enum 0x4c), `pkOnOff` (enum 0x4c), `substitution`, `limitTime` (enum 0x1e)
* `FUN_0050f860` **[C]**: `ex_ip`, `ex_port`, `in_ip`, `in_port`, `pid`

Errors **[V]**: `ERR_DATABASE`, `ERR_ROOMNOTFOUND`, `ERR_INVALIDARG`, `ERR_ALREADYINROOM`, `ERR_TOOMANYMEMBERS`, `ERR_PASSWDINCORRECT`, `ERR_DIFFDCVERSION`, `ERR_ROOMISFULL`, `ERR_ROOMSTATNOTMATCH`

#### `CMD_KICK_ROOMMEMBER`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e09c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `target_pid` | int |  |  |

Errors **[V]**: `ERR_NOTINROOM`, `ERR_NOTROOMOWNER`, `ERR_ROOMSTATNOTMATCH`, `ERR_TARGETPLAYERNOTEXIST`, `ERR_PLAYERISONESELF`

#### `CMD_LEAVE_ROOM`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e4d60`, `FUN_005e63b0`, `FUN_005e6f20`, `FUN_005ed6a0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Follows: `CANCEL_DECIDE_QUICKMATCH`, `GET_IPANDPORT`, `SET_GUEST`, `START_QUICKMATCH` · Followed by: —

#### `CMD_REQWATCHL_ROOMLIST`
*Class* RWL · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005f6f80`, `FUN_0063ff30`

List watch shape: `listnum_name="room_list_num",list_name="room_list",listid_name="room_id",list_max=100`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `listnum_name` | string |  | `"room_list_num"` |
| `list_name` | string |  | `"room_list"` |
| `listid_name` | string |  | `"room_id"` |
| `list_max` | int |  | `100` |

Response fields:
* `FUN_00515770` **[I]**: `room_list_num`, `room_list[`, `match_type` (enum 0x26), `room_id`, `status` (enum 0x44), `game_phase`, `name`, `cpuLevel` (enum 0x15), `gametime` (enum 0x1b), `injury` (enum 0x4c), `condition` (enum 0x14), `ball_type`, `exGame` (enum 0x4c), `pkOnOff` (enum 0x4c), `substitution`, `limitTime` (enum 0x1e), `team`, `score`, `score_1st`, `score_2nd`, `score_ex1st`, `score_ex2nd`, `score_pk`, `is_nogame` (enum 0x4c), `is_passwd` (enum 0x4c), `max_players`, `team_flag` (enum 0x4c), `is_invite_limit` (enum 0x4c), `team_name`, `short_teamname`, `is_legends_vs_player` (enum 0x4c), `total_combination`, `max_combination`, `dlcontent`, `gamer_num`, `enable_guest` (enum 0x4c), `gamer[`, `pid`, `is_room_owner` (enum 0x4c), `side` (enum 0x19), `is_watcher` (enum 0x4c), `mpid`, `guest_mpid`, `enter_no`, `game_no`

#### `CMD_REQWATCHL_ROOMPLAYERLIST`
*Class* RWL · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_00641650`

List watch shape: `listnum_name="roomplayer_list_num",list_name="roomplayer_list",listid_name="pid",list_max=8`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `room_id` | int |  | `1` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `listnum_name` | string |  | `"roomplayer_list_num"` |
| `list_name` | string |  | `"roomplayer_list"` |
| `listid_name` | string |  | `"pid"` |
| `list_max` | int |  | `8` |

Response fields:
* `FUN_006407f0` **[C]**: `roomplayer_list_num`, `roomplayer_list[`, `pid`, `room_player_id`, `room_entry_no`, `game_entry_no`, `desiredPosition[` (enum 0x4c), `gameplayer_name` (<=48 bytes)

#### `CMD_SEND_ALIVE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e42b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005e4080` **[C]**: `entry_count`, `entrygame[`

Errors **[V]**: `ERR_NOTINROOM`

#### `CMD_SEND_IPANDPORT`
*Class* S · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_005eea40`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `ex_ip` | string |  | `"186.123.216.200"` |
| `ex_port` | int |  | `29602` |
| `in_ip` | string |  | `"192.168.100.7"` |
| `in_port` | int |  | `25110` |
| `nat` | string |  | `"FULL_CONE_NAT"` |

Follows: `SET_CURRENTPLAYER` · Followed by: —

#### `CMD_SEND_P2PFAILED`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e1d10`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `p2p_player_num` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`

#### `CMD_SEND_P2PSTATUS`
*Class* S · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_005e22e0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `p2p_player_num` | int |  | `1` |
| `p2p_event` | string | `JOIN|LEAVE|DEL|COMMUNICATIONFAILED|MIGRATE` (inferred) | `"JOIN"` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |
| `p2p_status` | list |  | `[` |
| `pid` | int |  | `1` |
| `mpid` | int |  | `1` |
| `guest_mpid` | int |  | `0` |

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`

#### `CMD_SEND_ROOMP2PDATA`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_SEND_ROOMP2PDATABYPID`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_SET_GAMEENV`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e0710`, `FUN_005e62e0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `game_env` | object |  | `{` |
| `cpuLevel` | string | `VERYEASY|EASY|NORMAL|HARD|VERYHARD|HARDEST` (inferred) | `"NORMAL"` |
| `gametime` | string | `NOSET|5MINUTES|10MINUTES|15MINUTES|20MINUTES|25MINUTES|30MINUTES` (inferred) | `"10MINUTES"` |
| `injury` | string |  | `"NO"` |
| `condition` | string | `NORMAL|RANDOM` (inferred) | `"RANDOM"` |
| `ball_type` | int |  | `8` |
| `exGame` | string |  | `"YES"` |
| `pkOnOff` | string |  | `"YES"` |
| `substitution` | int |  | `3` |
| `limitTime` | string | `NOSET|SHORT|MIDDLE|LONG` (inferred) | `"MIDDLE"` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_NORFOUNDCLIENT`, `ERR_ROOMSTATNOTMATCH`, `ERR_NOTGAMEHOST`

#### `CMD_SET_GAMEMEMBER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ed6a0`, `FUN_0065a7d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005eb880` **[C]**: `try_info.`, `try_count`, `ping`, `ping_add`, `band_try_count`, `band`, `band_add`, `player_num`, `member[`, `pid`, `matching_order`, `name`, `is_owner` (enum 0x4c), `side` (enum 0x19), `is_leader` (enum 0x4c), `is_begin` (enum 0x4c), `lang` (enum 0x1c), `division` (enum 0x16), `rating`, `ml_point`, `ml_division` (enum 0x27), `manner`, `frrate`, `country`, `area`, `player_name`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `det_ip`, `det_port`, `ping_result`, `fd_pic_url`, `fd_selfreport_level`, `fd_beginnermark` (enum 0x4c), `fd_position_want`, `fd_desired_position`, `has_guestplayer` (enum 0x4c)

Follows: `SET_GAMEMEMBERENV` · Followed by: `CANCEL_DECIDE_QUICKMATCH`

#### `CMD_SET_GAMEMEMBERENV`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ed6a0`, `FUN_005fa030`, `FUN_0065a7d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005eb880` **[C]**: `try_info.`, `try_count`, `ping`, `ping_add`, `band_try_count`, `band`, `band_add`, `player_num`, `member[`, `pid`, `matching_order`, `name`, `is_owner` (enum 0x4c), `side` (enum 0x19), `is_leader` (enum 0x4c), `is_begin` (enum 0x4c), `lang` (enum 0x1c), `division` (enum 0x16), `rating`, `ml_point`, `ml_division` (enum 0x27), `manner`, `frrate`, `country`, `area`, `player_name`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `det_ip`, `det_port`, `ping_result`, `fd_pic_url`, `fd_selfreport_level`, `fd_beginnermark` (enum 0x4c), `fd_position_want`, `fd_desired_position`, `has_guestplayer` (enum 0x4c)

Follows: `START_QUICKMATCH` · Followed by: `SET_GAMEMEMBER`

Errors **[V]**: `ERR_ROOMSTATNOTMATCH`, `ERR_NOTGAMEHOST`, `ERR_MSGFORMAT`, `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`

#### `CMD_SET_GUEST`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e0ec0`, `FUN_005e6f20`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `has_guestplayer` | string |  |  |

Follows: — · Followed by: `LEAVE_ROOM`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`, `ERR_ROOMISFULL`, `ERR_TOOMANYGUEST`, `ERR_NOGUEST`, `ERR_CANTADDGUEST`

#### `CMD_UPDATE_ROOMSTATE`
*Class* S · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `status` | string | `WAITING|SETENV|GAME|RESULT|DISCONWAIT|RESULTWAIT` (inferred) |  |

Follows: — · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

#### `CMD_WATCH_ALIVE`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e42b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005e4080` **[C]**: `entry_count`, `entrygame[`

#### `CMD_WATCH_DECIDE_GAMEENV`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_DECIDE_GAMEPLAYER`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_DECIDE_GAMEPLAYERENV`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_ENTRY_GAME`
*Class* W · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005e42b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005e4080` **[C]**: `entry_count`, `entrygame[`

#### `CMD_WATCH_FORCELEAVEROOM`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e42b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_IPANDPORT`
*Class* W · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_005213d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0050f860` **[C]**: `ex_ip`, `ex_port`, `in_ip`, `in_port`, `pid`

#### `CMD_WATCH_ROOMSTATE`
*Class* W · *Server* MENU · *Impl* ✅ · *Sent from* `FUN_0053cee0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0051c110` **[C]**: `event` (enum 0x45), `owner_pid`, `state`

### 6.5 Quick match / Legends

#### `CMD_CANCEL_DECIDE_QUICKMATCH`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ed6a0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005eb880` **[C]**: `try_info.`, `try_count`, `ping`, `ping_add`, `band_try_count`, `band`, `band_add`, `player_num`, `member[`, `pid`, `matching_order`, `name`, `is_owner` (enum 0x4c), `side` (enum 0x19), `is_leader` (enum 0x4c), `is_begin` (enum 0x4c), `lang` (enum 0x1c), `division` (enum 0x16), `rating`, `ml_point`, `ml_division` (enum 0x27), `manner`, `frrate`, `country`, `area`, `player_name`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `det_ip`, `det_port`, `ping_result`, `fd_pic_url`, `fd_selfreport_level`, `fd_beginnermark` (enum 0x4c), `fd_position_want`, `fd_desired_position`, `has_guestplayer` (enum 0x4c)

Follows: `SET_GAMEMEMBER`, `START_QUICKMATCH` · Followed by: `LEAVE_ROOM`

Errors **[V]**: `ERR_ROOMNOTFOUND`

#### `CMD_CANCEL_LEGENDSROOMMATCHING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0061f260`, `FUN_0061f2a0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_MATCHPHASE`, `ERR_NOTFOUNDCLIENT`

#### `CMD_CANCEL_QUICK_GAMEBEGINNING_HOPE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e91a0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_MATCHPHASE`, `ERR_NOTFOUNDCLIENT`

#### `CMD_CANCEL_STARTQUICKMATCH`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e9400`, `FUN_005ed6a0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005eb880` **[C]**: `try_info.`, `try_count`, `ping`, `ping_add`, `band_try_count`, `band`, `band_add`, `player_num`, `member[`, `pid`, `matching_order`, `name`, `is_owner` (enum 0x4c), `side` (enum 0x19), `is_leader` (enum 0x4c), `is_begin` (enum 0x4c), `lang` (enum 0x1c), `division` (enum 0x16), `rating`, `ml_point`, `ml_division` (enum 0x27), `manner`, `frrate`, `country`, `area`, `player_name`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `det_ip`, `det_port`, `ping_result`, `fd_pic_url`, `fd_selfreport_level`, `fd_beginnermark` (enum 0x4c), `fd_position_want`, `fd_desired_position`, `has_guestplayer` (enum 0x4c)

Follows: `START_QUICKMATCH` · Followed by: —

Errors **[V]**: `ERR_MATCHPHASE`, `ERR_NOTFOUNDCLIENT`

#### `CMD_GET_LEGENDSROOMMATCHING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0061f620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `timeout` | int |  |  |

Errors **[V]**: `ERR_MATCHING_FAILED`, `ERR_MATCHING_TIMEOUT`, `ERR_MATCHING_CANCELED`

#### `CMD_GET_LEGENDS_PLAYHISTORY`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_GET_LEGENDS_RANKING`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_LEGENDS_RANKING_BYID`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_LEGENDS_ROOMSETTING`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_GET_QUICKMATCHNUMBERS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056e5a0`, `FUN_005daf80`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005dad00` **[C]**: `rank_1vs1_num`, `rank_2vs2_num`, `free_2vs2_num`

#### `CMD_GET_QUICKMATCHOPTION`
*Class* S · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_00600200`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Response fields:
* `FUN_00600200` **[I]**: `opp_lang`, `oc_is_rating_rank`, `oc_is_rating_free`, `ml_is_division` (enum 0x4c)

#### `CMD_REQWATCH_QUICK_PLAYERNUM`
*Class* RW · *Server* MENU · *Impl* ◻

#### `CMD_SEND_QUICK_GAMEBEGINNING_HOPE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e90c0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_MATCHPHASE`, `ERR_NOTFOUNDCLIENT`

#### `CMD_SET_QUICKMATCHOPTION`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00600530`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_START_QUICKMATCH`
*Class* B · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ed6a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `my_pid` | int |  |  |
| `qmatchsetting` | raw/object |  |  |
| `qmatchsetting.player_num` | int |  |  |
| `qmatchsetting.game_time` | enum 0x41 | `GT05|GT10` |  |
| `qmatchsetting.opp_lang` | enum 0x1c | 18 values, see enum 0x1c |  |
| `qmatchsetting.is_chat` | YES/NO | `NO|YES` |  |
| `qmatchsetting.has_guestplayer` | YES/NO | `NO|YES` |  |
| `qmatchsetting.frrate` | enum 0x17 | `FRAME_50|FRAME_60` |  |
| `qmatchsetting.oc_is_rating` | YES/NO | `NO|YES` |  |
| `qmatchsetting.oc_is_pcspec` | enum 0x3f | `NOT_FILL|FILL` |  |
| `qmatchsetting.fd_is_level` | YES/NO | `NO|YES` |  |
| `qmatchsetting.fd_position` | enum 0x3e | 13 values, see enum 0x3e |  |
| `qmatchsetting.fd_posflag` | int |  |  |
| `qmatchsetting.is_coach_mode` | YES/NO | `NO|YES` |  |
| `qmatchsetting.ml_is_division` | YES/NO | `NO|YES` |  |

Response fields:
* `FUN_005eb880` **[C]**: `try_info.`, `try_count`, `ping`, `ping_add`, `band_try_count`, `band`, `band_add`, `player_num`, `member[`, `pid`, `matching_order`, `name`, `is_owner` (enum 0x4c), `side` (enum 0x19), `is_leader` (enum 0x4c), `is_begin` (enum 0x4c), `lang` (enum 0x1c), `division` (enum 0x16), `rating`, `ml_point`, `ml_division` (enum 0x27), `manner`, `frrate`, `country`, `area`, `player_name`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `det_ip`, `det_port`, `ping_result`, `fd_pic_url`, `fd_selfreport_level`, `fd_beginnermark` (enum 0x4c), `fd_position_want`, `fd_desired_position`, `has_guestplayer` (enum 0x4c)
* `FUN_00fffdf0` **[I]**: `tryCount`, `ping`, `pingAdd`, `bandTryCount`, `band`, `bandAdd`, `result`, `try_info`, `try_count`, `ping_add`, `band_try_count`, `band_add`, `member_num`, `is_old_member`, `member`, `matching_order`, `is_owner`, `pid`, `pname`, `has_guestplayer`, `is_begin`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `manner`, `from_country`, `from_area`, `player_name`, `fd_pic_url`, `fd_selfreport_level`, `fd_beginnermark`, `fd_position_want`, `fd_desired_position`, `division`, `rating`, `ml_division`, `ml_point`, `frrate`, `player_num`, `name`, `side`, `is_leader`, `det_ip`, `det_port`, `ping_result`, `country`, `area`

Follows: — · Followed by: `CANCEL_DECIDE_QUICKMATCH`, `CANCEL_STARTQUICKMATCH`, `LEAVE_ROOM`, `SET_GAMEMEMBERENV`

Errors **[V]**: `ERR_GETPLAYERFAIL`, `ERR_QMATCH_STARTMATCHING`, `ERR_QMATCH_NOTICE`, `ERR_QMATCH_PRECONNECT`, `ERR_QMATCH_CANCELED`, `ERR_PING_FAILED`, `ERR_PLAYERNOTEXIST`, `ERR_REFUSED`, `ERR_QMATCH_CONNECTFAILED`, `ERR_CHECK_LANG`, `ERR_CHECK_GUEST`, `ERR_CHECK_CPU`, `ERR_CHECK_NATTYPE`, `ERR_CHECK_BLACKLIST`

### 6.6 In-match / results

#### `CMD_ADD_FOUL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `card_pid` | int |  |  |
| `card_side` | string |  |  |
| `card_gameplayer_id` | int |  |  |
| `card_kind` | string | `YELLOW|RED|NONE` (inferred) |  |
| `injury_side` | string |  |  |
| `injury_gameplayer_id` | int |  |  |
| `injury_all` | uint |  |  |
| `injury_one` | uint |  |  |

Follows: `ADD_SCORE`, `SEND_ABNORMALEND` · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_NOTGAMEHOST`, `ERR_ROOMSTATNOTMATCH`, `ERR_INVALIDARG`

#### `CMD_ADD_SCORE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `goal_side` | string |  |  |
| `goal_gameplayer_id` | uint |  |  |
| `goal_player_side` | string |  |  |
| `assist_side` | string |  |  |
| `assist_gameplayer_id` | uint |  |  |
| `assist_player_side` | string |  |  |
| `goal_player_pid` | uint |  |  |

Follows: `ADD_FOUL`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SEND_ML_GAMEPLAYERS`, `SET_GAMETEAM`, `SET_INJURYGAMEEND`, `UPDATE_ROOMSTATE`, `WATCH_ABNORMALEND`, `WATCH_FORFEITEDGAMEEND`, `WATCH_INJURYGAMEEND`, `WATCH_OWNGOALGAMEEND` · Followed by: `ADD_FOUL`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`, `ERR_MSGFORMAT`

#### `CMD_CHANGE_GAMEPHASE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `phase` | string | 11 values, see enum 0x1a (inferred) |  |
| `is_sync_failed` | string |  |  |

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`, `ERR_NOTGAMEHOST`

#### `CMD_GET_FD_GAMERESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006272e0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Response fields:
* `FUN_00625e10` **[C]**: `rlist_num`, `rlist`, `date`, `ex_type`, `pk_flag`, `no_match`, `cpu_lv`, `score`, `ex1st`, `ex2nd`, `pk`, `team`, `team_name`, `player`, `name`, `manner`, `pid`, `has_guestplayer`, `division`, `rating`, `side`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_GAMEADDPOINT`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f9ec0`, `FUN_006441b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005405c0` **[C]**: `player_count`, `player_list[`, `pid`, `addpoint`, `point`, `rating`, `oldrating`, `vs_count`, `trophy.sccessive5`, `trophy.high_success`, `trophy.final_advancement`, `trophy.competition_victory`, `rating_compe`, `get_spike_id[`, `get_ball_id[`

Follows: `ADD_FOUL`, `ADD_SCORE`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SEND_ML_GAMEPLAYERS`, `SET_GAMETEAM`, `SET_INJURYGAMEEND`, `UPDATE_ROOMSTATE`, `WATCH_ABNORMALEND`, `WATCH_FORFEITEDGAMEEND`, `WATCH_INJURYGAMEEND`, `WATCH_OWNGOALGAMEEND` · Followed by: `ADD_SCORE`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_GAMETEAM`, `SET_INJURYGAMEEND`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`

#### `CMD_GET_GAMEPLAN`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0052a5f0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Errors **[V]**: `ERR_DATANOTFOUND`

#### `CMD_GET_GAMERESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00624790`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Response fields:
* `FUN_00623420` **[C]**: `rlist_num`, `rlist`, `date`, `mode`, `ex_type`, `pk_flag`, `no_match`, `score`, `ex1st`, `ex2nd`, `pk`, `team`, `player`, `name`, `manner`, `pid`, `has_guestplayer`, `division`, `rating`, `side`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_MATCHID`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f9660`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005f9350` **[C]**: `match_id`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_REQ_FORFEITEDGAME_REPLY`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ea390`, `FUN_005fa030`, `FUN_00657c20`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Follows: `ADD_FOUL`, `ADD_SCORE`, `GET_GAMEADDPOINT`, `SEND_ABNORMALEND`, `SEND_ML_GAMEPLAYERS`, `SET_GAMETEAM`, `SET_INJURYGAMEEND`, `UPDATE_ROOMSTATE`, `WATCH_ABNORMALEND`, `WATCH_FORFEITEDGAMEEND`, `WATCH_INJURYGAMEEND`, `WATCH_OWNGOALGAMEEND` · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `SEND_ABNORMALEND`, `SET_GAMETEAM`, `SET_INJURYGAMEEND`

#### `CMD_SEND_ABNORMALEND`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `is_sync_failed` | string |  |  |

Follows: `ADD_FOUL`, `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ML_GAMEPLAYERS`, `SET_GAMETEAM`, `SET_INJURYGAMEEND`, `UPDATE_ROOMSTATE`, `WATCH_ABNORMALEND`, `WATCH_FORFEITEDGAMEEND`, `WATCH_INJURYGAMEEND`, `WATCH_OWNGOALGAMEEND` · Followed by: `ADD_FOUL`, `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SET_INJURYGAMEEND`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`

#### `CMD_SEND_CPUKIND`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`

#### `CMD_SEND_DISCON_PLAYER`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_NOTFOUNDCLIENT`

#### `CMD_SET_FORFEITEDGAMEEND`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ea390`, `FUN_00657c20`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_REFUSED`, `ERR_ROOMSTATNOTMATCH`

#### `CMD_SET_GAMEPLAN`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0052a4c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplan_data_size` | int |  |  |
| `gameplan_data` | string |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_SET_GAMETEAM`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `home_team_id` | uint |  |  |
| `away_team_id` | uint |  |  |

Follows: `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SET_INJURYGAMEEND` · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`, `ERR_NOTGAMEHOST`

#### `CMD_SET_INJURYGAMEEND`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `side` | string | `HOME|AWAY` (inferred) |  |

Follows: `ADD_FOUL`, `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SEND_ML_GAMEPLAYERS`, `SET_GAMETEAM`, `UPDATE_ROOMSTATE`, `WATCH_ABNORMALEND`, `WATCH_FORFEITEDGAMEEND`, `WATCH_INJURYGAMEEND`, `WATCH_OWNGOALGAMEEND` · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_GAMETEAM`

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_NOTGAMEHOST`, `ERR_ROOMSTATNOTMATCH`

#### `CMD_SET_XSESSION`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_NOTINROOM`, `ERR_NOTROOMOWNER`, `ERR_ROOMNOTFOUND`

#### `CMD_UPDATE_COMBINATION`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_NOTFOUNDCLIENT`

#### `CMD_WATCH_ABNORMALEND`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ea310`, `FUN_005fa030`, `FUN_00657ba0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Follows: — · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

#### `CMD_WATCH_DISCON_PLAYERENV`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_DISCON_PLAYERMATCH`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_FORFEITEDGAMEEND`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005ea310`, `FUN_005fa030`, `FUN_00657ba0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Follows: — · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

#### `CMD_WATCH_INJURYGAMEEND`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Follows: — · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

#### `CMD_WATCH_OWNGOALGAMEEND`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Follows: — · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

#### `CMD_WATCH_SERVERMATCHEND`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00560e00`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

### 6.7 Social (mail / friends / blacklist)

#### `CMD_ADD_BLACKLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fe5e0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `target_pid` | int |  |  |
| `target_npid` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`, `ERR_INFRIENDLIST`, `ERR_PLAYERISONESELF`, `ERR_INBLACKLIST`, `ERR_FRIENDCOUNTFULL`

#### `CMD_DEL_BLACKLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f62c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `target_pid` | int |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_DEL_FRIEND`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f6a70`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `from_pid` | int |  |  |

#### `CMD_DEL_FRIENDREQ`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f6580`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `to_pid` | uint |  |  |

#### `CMD_DEL_SENDSHORTMAIL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e7ca0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `smail_id` | uint |  |  |

#### `CMD_DEL_SHORTMAIL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e8520`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `smail_id` | uint |  |  |

#### `CMD_GET_BLACKLIST`
*Class* S · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_00541810`, `FUN_005f5930`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  | `1` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Response fields:
* `FUN_00549460` **[I]**: `pid`, `name`, `manner`, `division`, `rating`, `status`, `lang`, `index`, `npid` (<=101 bytes)

#### `CMD_GET_FRIENDLIST`
*Class* S · *Server* MENU · *Impl* ⚠ generic ack (seen in log) · *Sent from* `FUN_00541740`, `FUN_00543b80`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `target_pid` | int |  | `1` |
| `svrtype` | string | `GATE|ACCOUNT|MENU|VDPCHAT` (inferred) | `"MENU"` |

Response fields:
* `FUN_00549460` **[I]**: `pid`, `name`, `manner`, `division`, `rating`, `status`, `lang`, `index`, `npid` (<=101 bytes)

#### `CMD_GET_FRIENDREQ`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_GET_SENDSHORTMAIL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e8230`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `smail_id` | uint |  |  |

Response fields:
* `FUN_005e7f50` **[C]**: `to_pid`, `to_name` (<=48 bytes), `date`, `mes_subject` (<=192 bytes), `mes_body` (<=1536 bytes)

#### `CMD_GET_SENDSHORTMAILLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f4ac0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005f45a0` **[C]**: `smail_num`, `smlist[`, `smail_id`, `to_pid`, `to_name` (<=48 bytes), `date`, `mes_subject` (<=192 bytes)

#### `CMD_GET_SHORTMAIL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e8bd0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `smail_id` | uint |  |  |

Response fields:
* `FUN_005e87e0` **[C]**: `from_pid`, `from_name` (<=48 bytes), `mail_type`, `reply_enable`, `date`, `mes_subject` (<=192 bytes), `mes_body` (<=1536 bytes)

#### `CMD_GET_SHORTMAILLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f55f0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005f4e10` **[C]**: `smail_num`, `smlist[`, `smail_id`, `from_pid`, `from_name` (<=48 bytes), `mail_type`, `reply_enable`, `read_flag`, `date`, `mes_subject` (<=192 bytes)

#### `CMD_REPLY_FRIENDREQ`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f67f0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `from_pid` | uint |  |  |
| `answer` | YES/NO | `NO|YES` |  |

#### `CMD_SEND_FRIENDREQ`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fe850`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `to_pid` | int |  |  |
| `mes_body` | string |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_INFRIENDLIST`, `ERR_FRIENDCOUNTFULL`, `ERR_INBLACKLIST`, `ERR_FRIEND_PERMISSION_WAITING_STATE`

#### `CMD_SEND_SHORTMAIL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005e3b60`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `to_pid` | int |  |  |
| `to_npid` | uint |  |  |
| `mes_subject` | string |  |  |
| `mes_body` | string |  |  |

Errors **[V]**: `ERR_NOTFOUNDCLIENT`

#### `CMD_WATCH_FRIENDLIST`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00541810`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00549460` **[I]**: `pid`, `name`, `manner`, `division`, `rating`, `status`, `lang`, `index`, `npid` (<=101 bytes)

#### `CMD_WATCH_FRIENDREQ`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00541810`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_SHORTMAIL`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005213d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

### 6.8 Ranking / search / stats

#### `CMD_GET_PLAYERNUMBERS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056e690`, `FUN_005db750`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005db480` **[C]**: `network_player_num`, `compe_player_num`, `commu_player_num`, `legends_player_num`

#### `CMD_GET_RANKING`
*Class* B · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f1710`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `uri` | string |  |  |
| `data` | string |  |  |

Response fields:
* `FUN_005f15f0` **[C]**: `ranking_data`

#### `CMD_GET_WEEKLYRANKINGDATELIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f2870`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_WEEKLYRANKINGNOTFOUND`

#### `CMD_SEARCH_PLAYER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005f2fe0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `option` | string |  |  |
| `pname` | string |  |  |

Errors **[V]**: `ERR_TOOMANYRECORDS`

### 6.9 Community

#### `CMD_APPLY_COMMU_MATCH`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00527f80`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `match_id` | uint |  |  |
| `room_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_MATCHNOTEXIST`, `ERR_TARGETINGAME`, `ERR_PLAYERNOTINCOMPE`, `ERR_BUSY`, `ERR_COMMUMATCHREFUSED`, `ERR_DATABASE`

#### `CMD_CANCEL_COMMU_MATCH`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005280e0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `match_id` | uint |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_PLAYERISNOTGAMER`

#### `CMD_CREATE_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525c30`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_name` | string |  |  |
| `commu_description` | string |  |  |
| `password` | string |  |  |
| `invite_only` | YES/NO | `NO|YES` |  |
| `week` | raw/object |  |  |
| `start_time` | uint |  |  |
| `end_time` | uint |  |  |

Errors **[V]**: `ERR_NGWORD`, `ERR_TOOMANYCOMMU`, `ERR_DATABASE`

#### `CMD_CREATE_COMMU_LEAGUE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526b20`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_name` | string |  |  |
| `match_time` | enum 0x10 | `TIME_5|TIME_10|TIME_15` |  |
| `injury` | YES/NO | `NO|YES` |  |
| `ball_type` | uint |  |  |
| `home_and_away` | YES/NO | `NO|YES` |  |
| `comment` | string |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NGWORD`, `ERR_COMMUCOMPEISFULL`, `ERR_DIFFDCVERSION`, `ERR_NOTJOINEDCOMMU`

#### `CMD_DEL_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525e00`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_NOTCOMMUOWNER`

#### `CMD_DEL_COMMU_LEAGUEMEMBER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00527c80`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_NOTCOMMUOWNER`, `ERR_NOTENTRY`

#### `CMD_DEL_COMMU_MEMBER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525b70`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |
| `pid` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_PLAYERNOTINCOMMU`, `ERR_NOTCOMMUOWNER`

#### `CMD_END_COMMU_LEAGUE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00527d10`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_NOTCOMMUOWNER`

#### `CMD_ENTRY_COMMU_LEAGUECELL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526e30`, `FUN_005277b0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `cell_id` | uint |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_ENTRYISFULL`, `ERR_DATABASE`, `ERR_DIFFDCVERSION`

#### `CMD_GET_COMMU_ASSISTRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528990`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_COMMUCOMPENOTEXIST`, `ERR_DATABASE`

#### `CMD_GET_COMMU_COMPERANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528bc0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_COMMUCOMPENOTEXIST`, `ERR_DATABASE`

#### `CMD_GET_COMMU_COMPERESULT`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528de0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_COMMUCOMPENOTEXIST`, `ERR_DATABASE`

#### `CMD_GET_COMMU_GOALRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528760`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_NOTJOINEDCOMMU`, `ERR_COMMUCOMPENOTEXIST`, `ERR_DATABASE`

#### `CMD_GET_COMMU_INFO`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00529210`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |

Response fields:
* `FUN_0054fe50` **[I]**: `commu_info`, `value`, `commu_id`, `member_count`, `logon_member_count`, `total_match_count`, `total_compe_count`, `update_date`, `start_time`, `end_time`, `commu_name`, `commu_description`, `invite_only`, `password`, `week`

Errors **[V]**: `ERR_COMMUNOTEXIST`

#### `CMD_GET_COMMU_LEAGUEENTRYCELLINFO`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526c40`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`

#### `CMD_GET_COMMU_LEAGUELIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526920`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_DATABASE`

#### `CMD_GET_COMMU_MEMBERRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005299d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_DATABASE`

#### `CMD_GET_COMMU_MEMBERSRESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005295a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_PLAYERNOTINCOMMU`, `ERR_DATABASE`

#### `CMD_GET_COMMU_MEMBERSRESULTS_VS`
*Class* B · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00529780`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |
| `base_pid` | int |  |  |
| `oppo_pid` | int |  |  |
| `uri` | string |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_PLAYERNOTINCOMMU`

#### `CMD_GET_COMMU_PASTLEAGUELIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526530`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`

#### `CMD_GET_COMMU_PASTLEAGUERESULT`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526720`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`

#### `CMD_GET_COMMU_TEAM`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528fe0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `cell_id` | uint |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`

#### `CMD_GET_COMMU_TEAMLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00529cf0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`

#### `CMD_GET_COMMU_TEAMRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0052a1a0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`

#### `CMD_GET_COMMU_TEAMRESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00529d90`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `team_id` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_PLAYERNOTINCOMMU`, `ERR_NOTJOINEDCOMMU`

#### `CMD_GET_COMMU_TEAMRESULTS_VS`
*Class* B · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00529f50`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |
| `base_team_id` | uint |  |  |
| `oppo_team_id` | uint |  |  |
| `uri` | string |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`

#### `CMD_GET_JOINEDCOMMULIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525540`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0054e980` **[I]**: `commu_id`, `member_count`, `logon_member_count`, `total_match_count`, `total_legends_count`, `total_compe_count`, `update_date`, `start_time`, `end_time`, `commu_name`, `commu_description`, `invite_only`, `password`, `week`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_JOIN_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526460`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |
| `password` | string |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_PASSWDINCORRECT`, `ERR_DATABASE`, `ERR_COMMUNITYISFULL`, `ERR_ALREADYINCOMMU`, `ERR_TOOMANYCOMMU`

#### `CMD_JOIN_COMMU_LEAGUE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00527860`, `FUN_00527a30`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_DATABASE`, `ERR_DIFFDCVERSION`

#### `CMD_LEAVE_COMMU_LEAGUE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00527c00`, `FUN_0052bd00`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_PLAYERNOTINCOMPE`

#### `CMD_REPLY_COMMU_MATCH`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528020`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `match_id` | uint |  |  |
| `reply` | string |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_MATCHNOTEXIST`, `ERR_COMMUMATCHCANCELED`

#### `CMD_REQWATCHL_COMMU_COMPEMEMBERLIST`
*Class* RWL · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525910`

List watch shape: `listnum_name="member_list_num",list_name="member_list",listid_name="info.pid",list_max=32`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |
| `commu_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`

#### `CMD_REQWATCHL_COMMU_MATCHLIST`
*Class* RWL · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528560`

List watch shape: `listnum_name="match_list_num",list_name="match_list",listid_name="match.match_id",list_max=9`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`, `ERR_DATABASE`

#### `CMD_REQWATCHL_COMMU_MEMBERLIST`
*Class* RWL · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525720`

List watch shape: `listnum_name="member_list_num",list_name="member_list",listid_name="info.pid",list_max=32`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`

#### `CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST`
*Class* RWL · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00527da0`

List watch shape: `listnum_name="match_list_num",list_name="match_list",listid_name="match_id",list_max=300`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_MATCHNOTEXIST`, `ERR_COMMUNOTENTRY`

#### `CMD_RESET_CURRENT_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005263c0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_PLAYERNOTINCOMMU`, `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`

#### `CMD_SEARCH_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525f20`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `keyword` | string |  |  |
| `target` | enum 0x47 | `NAME|COMMENT` |  |
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |
| `has_friends` | YES/NO | `NO|YES` |  |
| `target_pid` | int |  |  |
| `week` | raw/object |  |  |
| `active_time` | enum 0x00 | 25 values, see enum 0x00 |  |
| `index` | uint |  |  |

Response fields:
* `FUN_0054e980` **[I]**: `commu_id`, `member_count`, `logon_member_count`, `total_match_count`, `total_legends_count`, `total_compe_count`, `update_date`, `start_time`, `end_time`, `commu_name`, `commu_description`, `invite_only`, `password`, `week`

Errors **[V]**: `ERR_TOOMANYLIST`, `ERR_INVALIDARG`, `ERR_NOPLAYER`

#### `CMD_SEARCH_COMMU_BYRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00525e90`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `ranking_kind` | enum 0x42 | `LOGINMEMBER_NUM|MATCH_NUM|LEGENDS_MATCH_NUM|LEAGUE_NUM` |  |

Errors **[V]**: `ERR_TOOMANYLIST`, `ERR_INVALIDARG`, `ERR_NOPLAYER`

#### `CMD_SET_COMMU_MATCHINFO`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`, `FUN_006441b0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `possession_average_h` | uint |  |  |
| `shot_h` | uint |  |  |
| `suffer_shot_h` | uint |  |  |
| `possession_average_a` | uint |  |  |
| `shot_a` | uint |  |  |
| `suffer_shot_a` | uint |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`, `ERR_PLAYERNOTINCOMMU`, `ERR_ROOMSTATNOTMATCH`, `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_SET_COMMU_OPTION`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005293d0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_name` | string |  |  |
| `commu_description` | string |  |  |
| `password` | string |  |  |
| `invite_only` | YES/NO | `NO|YES` |  |
| `week` | raw/object |  |  |
| `start_time` | uint |  |  |
| `end_time` | uint |  |  |

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_NOTCOMMUOWNER`, `ERR_NGWORD`

#### `CMD_SET_COMMU_TEAM`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526ed0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUNOTEXIST`, `ERR_COMMUCOMPENOTEXIST`

#### `CMD_SET_CURRENT_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00526300`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_NOTJOINEDCOMMU`

#### `CMD_WATCH_COMMU_MATCH`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528370`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_COMMU_MATCH_WAITING`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00528170`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_COMMUMATCHCANCELED`

#### `CMD_WITHDRAW_COMMU`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0052b730`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `commu_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMMUNOTEXIST`, `ERR_PLAYERNOTINCOMMU`

### 6.10 Competition (COMPLG)

#### `CMD_ADD_COMPLG_RESULTS`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`

#### `CMD_CREATE_COMPLG`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_ASSISTRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00562680`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |
| `start_rank` | int |  |  |
| `get_count` | int |  |  |
| `get_pid` | int |  |  |

Response fields:
* `FUN_00562760` **[I]**: `rank`, `team_id`, `pid`, `assist`, `npid`, `name`

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_COMMENDATION_DATA`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005619b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_DIGESTLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005dc620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `digest_begin` | uint |  |  |
| `digest_num` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_COMPLG_GOALRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00561fc0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |
| `start_rank` | int |  |  |
| `get_count` | int |  |  |
| `get_pid` | int |  |  |

Response fields:
* `FUN_005620a0` **[I]**: `rank`, `team_id`, `pid`, `score`, `npid`, `name`

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_INFO`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0055da90`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Response fields:
* `FUN_0055daf0` **[I]**: `compe_data`, `compe_kind`, `compe_id`, `entry_start_time`, `entry_end_time`, `match_start_time`, `match_end_time`, `compe_end_time`, `compe_name`, `status`, `category`, `entry_limit`, `entry_limit_rank`, `dlcver`, `player_capacity`, `player_num`, `champion_notice`, `use_national`, `use_club`, `use_ml`, `guest_count`, `useable_team_size`, `usable_ball_pre_num`, `usable_ball_playoff_num`, `usable_ball_final_num`, `gametime`, `is_coach_mode`, `fee`, `need_konamiid`, `is_digest`, `banner_file_name` (<=15 bytes), `useable_team_id`, `entry_limit_division`, `get_ball_id`, `get_spike_id`, `ad_file_name` (<=101 bytes), `usable_ball_pre_list`, `usable_ball_playoff_list`, `usable_ball_final_list`

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_KIND_LIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00565c80`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00565da0` **[I]**: `compe_kind_num`, `compe_kind_list`, `compe_kind`, `compe_name` (<=101 bytes), `banner_file_name` (<=101 bytes)

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_COMPLG_LIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0055cb30`, `FUN_0056e470`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_list_kind` | string / enum 0x0b | `NOW_PLAY|NOW_WATCH|OLD_ALL|OLD_MY|SCHEDULE` | yes |
| `compe_kind` | string / enum 0x0a | always `KONAMI_CUP_ONLINE` except for OLD_ALL | yes |
| `compe_mode` | string / enum 0x0d | `NORMAL|ML` | yes |

Reply (FUN_0055cdb0, verified): must echo `compe_list_kind` (and `compe_kind` for OLD_ALL); `compe_num` <= 20; every `compe_list[i]` key is required. `dlcver` is a non-empty string, `champion_notice` is enum 0x01 (TRUE/FALSE), `status` 0x0e, `category` 0x08, `entry_limit` 0x09, `entry_limit_division` a 6-entry TRUE/FALSE array. Implemented in `CompetitionHandlers`.

Response fields:
* `FUN_0055cdb0` **[I]**: `compe_num`, `compe_list_kind`, `compe_list`, `compe_kind`, `compe_id`, `entry_start_time`, `entry_end_time`, `match_start_time`, `match_end_time`, `compe_end_time`, `compe_name`, `status`, `category`, `entry_limit`, `entry_limit_rank`, `dlcver`, `player_capacity`, `player_num`, `champion_notice`, `guest_count`, `is_coach_mode`, `need_konamiid`, `banner_file_name` (<=15 bytes), `entry_limit_division`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_COMPLG_MATCH_START_TIME`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005610e0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_GET_COMPLG_MYENTRY_COMPEID`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0055c990`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_COMPLG_MYRESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00564050`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_COMPLG_PLAYOFF_MATCH`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00564830`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_STATUS`, `ERR_COMPE_NOT_PLAYOFF_ENTRY`

#### `CMD_GET_COMPLG_PLAYOFF_PLAY_OK`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00561930`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `has_guestplayer` | YES/NO | `NO|YES` |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`, `ERR_COMPE_STATUS`

#### `CMD_GET_COMPLG_PLAYOFF_RESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00564870`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_PRE_RESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00562d50`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |
| `start_rank` | int |  |  |
| `get_count` | int |  |  |
| `get_pid` | int |  |  |
| `get_cl_pid` | int |  |  |

Response fields:
* `FUN_00562e60` **[I]**: `date`, `rank`, `rank_by_team`, `team_id`, `pid`, `win_count`, `lose_count`, `draw_count`, `point`, `score`, `loss`, `scorediff`, `update_date`, `rating`, `ml_point`, `continuous_win`, `npid`, `is_qualifier`, `name`

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`, `ERR_TEAM_NOTFOUND`, `ERR_INDEX_OUTOFRANGE`

#### `CMD_GET_COMPLG_TEAMRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0055ebe0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Response fields:
* `FUN_0055ec30` **[I]**: `rank_list_num`, `rank_list`, `team_id`, `user_num`

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_GET_COMPLG_TEAM_DATA`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00564090`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_PLAYERNOTINCOMPE`, `ERR_COMPE_NOT_SELECT_TEAM`

#### `CMD_GET_COMPLG_TITLEHOLDER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00563780`, `FUN_00645200`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `is_ml_compe` | YES/NO | `NO|YES` |  |

Response fields:
* `FUN_00561a10` **[I]**: `compe_name`, `trophy`, `name`, `team_id`, `pid`, `npid`, `cup_file_name`
* `FUN_006461b0` **[I]**: `titleholder_num`, `titleholder_list`, `compe_name`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_COMPLG_TOURNAMENT`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00561f60`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`, `ERR_COMPE_STATUS`

#### `CMD_JOIN_COMPLG`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0055f0b0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `compe_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_JOIN_OTHERCOMPE`, `ERR_COMPE_BEFORE_ENTRYTIME`, `ERR_COMPE_AFTER_ENTRYTIME`, `ERR_COMPE_FULL`, `ERR_DIFFDCVERSION`, `ERR_COMPE_JOIN_DIVISION`, `ERR_COMPE_JOIN_RANKING`, `ERR_COMPE_FUNDSHORTAGE`, `ERR_COMPE_PLAYERSHORTAGE`, `ERR_NOTREGISTERED`

#### `CMD_LEAVE_COMPLG`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0055f460`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTENTRY`

#### `CMD_QUIT_COMPLG`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTENTRY`

#### `CMD_REQWATCHL_COMPLG_MATCHLIST`
*Class* RWL · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00561f20`

List watch shape: `listnum_name="match_list_num",list_name="match_list",listid_name="match_id",list_max=8`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_REQWATCH_COMPLG_PLAYERNUM`
*Class* RW · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005604c0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`

#### `CMD_SET_COMPLG_TEAM`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_COMPE_NOTFOUND`, `ERR_COMPE_NOTENTRY`

#### `CMD_WATCH_COMPLG_END`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005606e0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_COMPLG_END_MATCH`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005614c0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_COMPLG_GAMEDATA`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_COMPLG_MATCHNOTICE`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_COMPLG_MATCH_START`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00560b40`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

#### `CMD_WATCH_COMPLG_MATCH_START_TIME`
*Class* W · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00561060`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

### 6.11 Master League

#### `CMD_ACK_ML_MARKET_PRICE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060ec30`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Response fields:
* `FUN_0060daa0` **[C]**: `result`, `market_price`, `trade_rate`, `enable_buy_num`, `team_funds`
* `FUN_0060dd70` **[C]**: `result`, `market_price`, `trade_rate`, `enable_buy_num`, `team_funds`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_ADD_ML_EVENT_COMPE_TEST`
*Class* ? · *Server* MENU · *Impl* ◻

Errors **[V]**: `ERR_DATABASE`

#### `CMD_ADD_ML_MARKET_CHECKLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060d1a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_BUY_ML_MARKET_GAMEPLAYER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060d1a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_INSUFFICIENT_FUNDS`, `ERR_TRADE_STOP`, `ERR_NOTFOUND`, `ERR_TEAM_MEMBER_FULL`, `ERR_ENABLE_BUY_NUM`

#### `CMD_CREATE_ML_MYTEAM`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0064d570`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `team_name` | string |  |  |
| `team_name_short` | string / uint |  |  |
| `check_team_name` | string |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_ALREADYEXISTS`, `ERR_LICENSE`, `ERR_NOTFOUND`, `ERR_NGWORD`

#### `CMD_DECIDE_ML_MYTEAM_PLAYER`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`

#### `CMD_DELETE_ML_MARKET_CHECKLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00605070`, `FUN_0060d1a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_DELETE_ML_MYTEAM`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_COMPE_GAMEPLAYERLIST`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_COMPE_PRIZE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00632770`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00632460` **[I]**: `prize`, `team_funds`, `compe_result`, `compe_name`, `my_pre_rank`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_DIVISION`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056e370`, `FUN_0064dae0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0064d800` **[I]**: `is_update`, `div_before`, `div_after`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_DIVISIONRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006519d0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `start_index` | int |  |  |
| `team_division` | string | `D5|D4|D3|D2|D1|NONE` (inferred) |  |

Response fields:
* `FUN_00651e20` **[I]**: `date`, `rank_up`, `rank_down`, `rank_list_num`, `total_rank_num`, `team_division`, `rank_list`, `pid`, `rank`, `base_team_id`, `match_rec`, `win_num`, `draw_num`, `lose_num`, `division_point`, `name`, `team_name`

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_ML_DIVISIONRANKING_BYID`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00651360`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `target_pid` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_ML_DIVISIONUPDATE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0064d710`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0064df40` **[I]**: `up_point`, `down_point`, `up_rank`, `down_rank`, `next_update_date`, `rank`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_EVENTLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00647a10`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |

Response fields:
* `FUN_00647f50` **[I]**: `event_num`, `event_list`, `event_date`, `event_type`, `title`, `item_list`, `item_name`, `item_text`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_GAMEPLAN`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056dfe0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0056f880` **[I]**: `gameplan_data_size`, `gameplan_data`, `result`

Errors **[V]**: `ERR_DATANOTFOUND`

#### `CMD_GET_ML_GAMERESULTS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00618260`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MARKET_CHECKLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056df00`, `FUN_0060b4d0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00609c10` **[I]**: `player_list_num`, `player_list`, `gameplayer_id`, `market_price`, `market_price_old`, `buy_price`, `market_price_max`, `market_price_min`, `uniform_number`, `overall`, `goal`, `assist`, `games`, `trade_rate`, `is_trade_stop`, `trade_stop_date`, `trade_stop_term`, `injury`, `part`, `symptom`, `level`, `level_no_self_conscious`, `mcount_left`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MARKET_ENV`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MARKET_GAINRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00616900`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00616be0` **[I]**: `rank_list_num`, `rank_list`, `gameplayer_id`, `market_price_this_week`, `market_price_last_week`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MARKET_GAMEPLAYERPRICEHISTORY`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060d1a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Response fields:
* `FUN_0060e030` **[I]**: `owner_num`, `price_history_num`, `price_history`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MARKET_POPULARRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006178f0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_006179a0` **[I]**: `rank_list_num`, `rank_list`, `gameplayer_id`, `owner_num`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MARKET_PRICERANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00615ba0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00615c50` **[I]**: `rank_list_num`, `rank_list`, `gameplayer_id`, `market_price`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MATCHEXPENSE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0064fdf0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0064fa70` **[I]**: `expense`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MATCHINCOME_INFO`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0064f570`, `FUN_0064fee0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0064f240` **[I]**: `fee`, `prize`, `income`, `yellowcard_cnt`, `redcard_cnt`, `purchase_cnt`, `team_funds`

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`, `ERR_NOTFOUND`

#### `CMD_GET_ML_MATCHRECORD`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00644d30`, `FUN_00644e20`, `FUN_0064dbd0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |

Response fields:
* `FUN_00644f20` **[I]**: `match_record`, `win_num`, `lose_num`, `draw_num`, `consecutive_win`, `consecutive_win_record`, `avr_get_goal`, `avr_loss_goal`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MYRANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0064d9f0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0064d240` **[I]**: `teampower_rank`, `teampower_total_rank_num`, `teamfunds_rank`, `teamfunds_total_rank_num`, `date`

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_ML_MYTEAM`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056dd40`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_0056e780` **[I]**: `result`, `team_funds`, `team_name`, `team_name_short`, `stadium_id`, `division`, `base_team_id`, `enable_buy_num`, `point`, `team_power`, `total_price_old`, `team_resource`, `team_status`

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`, `ERR_NOTFOUND`

#### `CMD_GET_ML_MYTEAM_EVENTLIST`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_GET_ML_MYTEAM_GAMEPLAYERLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056de20`, `FUN_00609bd0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_005702d0` **[I]**: `player_list_num`, `player_list`, `gameplayer_id`, `uniform_number`, `injury`, `part`, `symptom`, `level`, `level_no_self_conscious`, `mcount_left`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MYTEAM_GAMEPLAYERLIST_OLD`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00645470`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |
| `history_type` | string |  |  |
| `history_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_MYTEAM_HISTORY`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006452f0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |
| `type` | string | 21 values, see enum 0x4b (inferred) |  |

Response fields:
* `FUN_00645870` **[I]**: `myteam_history_num`, `myteam_history`, `history_id`, `division`, `team_resource`, `date`

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_GET_ML_NEWS_LIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056e230`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |

Response fields:
* `FUN_00571160` **[I]**: `news_list_num`, `news_list`, `text`, `comment`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_RANKING`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00651530`, `FUN_00651670`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `start_index` | int |  |  |
| `order` | string | `DESC|ASC` (inferred) |  |
| `rankall_kind` | string |  |  |

Response fields:
* `FUN_00652a80` **[I]**: `date`, `rank_list_num`, `total_rank_num`, `rank_list`, `pid`, `rank`, `base_team_id`, `division`, `team_power`, `name`, `team_name`

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_ML_RANKING_BYID`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006517a0`, `FUN_006518c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `rankall_kind` | string |  |  |
| `pid` | int |  |  |

Response fields:
* `FUN_006533f0` **[I]**: `date`, `rank_list_num`, `total_rank_num`, `rank_list`, `pid`, `rank`, `base_team_id`, `division`, `team_resource`, `name`, `team_name`

Errors **[V]**: `ERR_DATABASE`, `ERR_NOTFOUND`

#### `CMD_GET_ML_REGULAREVENTLIST`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006478a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `pid` | int |  |  |
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |

Response fields:
* `FUN_00648d20` **[I]**: `daily_event_num`, `weekly_event_num`, `monthly_event_num`, `daily_event_list`, `event_date`, `title`, `item_list`, `item_name`, `item_text`, `weekly_event_list`, `monthly_event_list`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_GET_ML_TRADEHISTORY`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00611a30`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00611b10` **[I]**: `trade_history_num`, `trade_history`, `date`, `gameplayer_id`, `trade_type`, `gameplayer_sell_price`, `gameplayer_buy_price`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_REQWATCH_ML_MARKET_PRICE`
*Class* RW · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060d1a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Errors **[V]**: `ERR_NOTFOUND`, `ERR_COMPE_ENTRY`

#### `CMD_RESET_ML_MYTEAM`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00650fb0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Errors **[V]**: `ERR_DATABASE`, `ERR_FORBIDDEN`

#### `CMD_SEARCH_ML`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060bc90`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |
| `sort` | string | `PLAYER_NAME|POSITION|HEIGHT|AGE|OVERALL_RATING|MARKET_VALUE|NATIONALITY` (inferred) |  |
| `order` | string | `DESC|ASC` (inferred) |  |
| `start_index` | int |  |  |
| `name` | string |  |  |
| `position` | enum 0x31 | `OFF|ON|FUZZY` |  |
| `height` | enum 0x31 | `OFF|ON|FUZZY` |  |
| `age` | enum 0x31 | `OFF|ON|FUZZY` |  |
| `stronger_foot` | string | `NO|RIGHT|LEFT` (inferred) |  |
| `overall` | enum 0x31 | `OFF|ON|FUZZY` |  |
| `market_price` | enum 0x31 | `OFF|ON|FUZZY` |  |

Response fields:
* `FUN_0060e380` **[I]**: `gameplayer_list_num`, `gameplayer_list`, `gameplayer_id`, `market_price`, `market_price_old`, `market_price_max`, `market_price_min`, `overall`, `trade_rate`, `is_trade_stop`, `trade_stop_date`, `trade_stop_term`, `total_gameplayer_num`

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_SEARCH_ML_ALL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006100d0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |
| `sort` | string | `PLAYER_NAME|POSITION|HEIGHT|AGE|OVERALL_RATING|MARKET_VALUE|NATIONALITY` (inferred) |  |
| `order` | string | `DESC|ASC` (inferred) |  |
| `start_index` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_SEARCH_ML_LEAGUE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060c650`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |
| `sort` | string | `PLAYER_NAME|POSITION|HEIGHT|AGE|OVERALL_RATING|MARKET_VALUE|NATIONALITY` (inferred) |  |
| `order` | string | `DESC|ASC` (inferred) |  |
| `start_index` | int |  |  |
| `team_id` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_SEARCH_ML_NATIONAL`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060c900`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `lang` | string | 18 values, see enum 0x1c (inferred) |  |
| `sort` | string | `PLAYER_NAME|POSITION|HEIGHT|AGE|OVERALL_RATING|MARKET_VALUE|NATIONALITY` (inferred) |  |
| `order` | string | `DESC|ASC` (inferred) |  |
| `start_index` | int |  |  |
| `national` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_SELL_ML_MARKET_GAMEPLAYER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0060d1a0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id` | int |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_TRADE_STOP`, `ERR_TEAM_MEMBER_LACK`

#### `CMD_SEND_ML_GAMEPLAYERS`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_005fa030`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplayer_id_list_num` | int |  |  |
| `gameplayer_id_list` | raw/object |  |  |

Follows: — · Followed by: `ADD_SCORE`, `GET_GAMEADDPOINT`, `REQ_FORFEITEDGAME_REPLY`, `SEND_ABNORMALEND`, `SET_INJURYGAMEEND`

Errors **[V]**: `ERR_DATABASE`

#### `CMD_SET_ML_GAMEPLAN`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0056e0c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `gameplan_data_size` | int |  |  |
| `gameplan_data` | string |  |  |

Errors **[V]**: `ERR_DATABASE`

#### `CMD_SET_ML_MYTEAM_BASE`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006502d0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `base_team_id` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_FORBIDDEN`

#### `CMD_SET_ML_MYTEAM_NAME`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_006503d0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `team_name` | string |  |  |
| `team_name_short` | string |  |  |
| `check_team_name` | string |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_NGWORD`, `ERR_LICENCE`, `ERR_NOTFOUND`, `ERR_FORBIDDEN`

#### `CMD_SET_ML_MYTEAM_TYPE`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_DATABASE`, `ERR_INVALIDARG`

#### `CMD_SET_ML_MYTEAM_UNIFORMNUMBER`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_00650620`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `no_list_num` | uint |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_FORBIDDEN`

#### `CMD_SET_ML_NEWS_COMMENT`
*Class* S · *Server* MENU · *Impl* ◻ · *Sent from* `FUN_0064e6c0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `comment` | string |  |  |

Errors **[V]**: `ERR_DATABASE`, `ERR_NGWORD`

### 6.12 Misc

#### `CMD_COMMON_SENDRECV`
*Class* ? · *Server* MENU · *Impl* ◻

#### `CMD_REC_STATISTIC`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_REQWATCH_NETWORK_PLAYERNUM`
*Class* RW · *Server* MENU · *Impl* ◻

#### `CMD_RETRY_AUTOMATCH`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_SEND_DISCON_SETPLAYER`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

Errors **[V]**: `ERR_NOTINROOM`, `ERR_ROOMNOTFOUND`, `ERR_NOTFOUNDCLIENT`, `ERR_NOTGAMEHOST`

#### `CMD_SEND_OFFEROWNER`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_SEND_TEXTCHAT`
*Class* S · *Server* VDPCHAT · *Impl* ✅ · *Sent from* `FUN_0056c1b0`

| Request key | type | range / enum | seen from real client |
|---|---|---|---|
| `channel` | string |  |  |
| `statement` | string |  |  |
| `list_flg` | string |  |  |

Response fields:
* `FUN_00510a20` **[C]**: `from_pid`, `channel`, `name`, `statement`

Errors **[V]**: `ERR_CHATENABLE`, `ERR_ISNOTGM`, `ERR_TARGETINGAME`, `ERR_TARGETISNOTLOGIN`, `ERR_ROOMNOTFOUND`, `ERR_ROOMSTATNOTMATCH`, `ERR_PLAYERISNOTGAMER`

#### `CMD_SET_CHATENABLE`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_SET_TRACKRECORD`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_SET_XNADDR`
*Class* S · *Server* MENU · *Impl* ◻

Request: not found at a normal call site (sent from a code path Ghidra did not attach to a function, or never sent by this build).

#### `CMD_WATCH_INVITATION`
*Class* W · *Server* MENU · *Impl* ◻

Server-originated push; no request.

#### `CMD_WATCH_TEXTCHAT`
*Class* W · *Server* VDPCHAT · *Impl* ✅ · *Sent from* `FUN_0056c1b0`

Request: only the implicit `msg`/`rqid`/`svrtype`/`client`.

Response fields:
* `FUN_00510a20` **[C]**: `from_pid`, `channel`, `name`, `statement`

Errors **[V]**: `ERR_CHATENABLE`, `ERR_ISNOTGM`, `ERR_ROOMSTATNOTMATCH`

## 7. Response structures not tied to a single command

Parsers whose owning command could not be pinned through the call graph (invoked through vtables). Grouped by what their keys say; use them when implementing the matching commands.

* `FUN_0050f420` **[I]**: `rank_1vs1_num`, `rank_2vs2_num`, `free_2vs2_num`
* `FUN_00511880` **[I]**: `player_data`, `pid`
* `FUN_00511a10` **[I]**: `player_data`, `name`
* `FUN_00521130` **[I]**: `date`
* `FUN_005213d0` **[I]**: `result`
* `FUN_00543b80` **[I]**: `target_pid`
* `FUN_00549fb0` **[I]**: `mode`, `lobby_name`, `block_name`, `commu_name`, `commu_id`, `room_id`, `beginnermark`, `position_want`, `selfreport_level`, `compe_id`
* `FUN_0054b190` **[I]**: `cell_id`, `team_id`, `entry_user_count`
* `FUN_0054b940` **[I]**: `match_id`
* `FUN_0054c1f0` **[I]**: `score_1st`, `score_2nd`, `score_ex1st`, `score_ex2nd`, `score_pk`
* `FUN_0054c990` **[I]**: `disp_rank`, `ga_count`, `entry_info`
* `FUN_0054d1c0` **[I]**: `team_rank_disp`, `team_rank_point`, `team_rank_win`, `team_rank_draw`, `team_rank_lose`, `team_rank_score`, `team_rank_lost`, `team_rank_scorediff`
* `FUN_0054dac0` **[I]**: `played`, `home_id`, `home_pts`, `away_id`, `away_pts`
* `FUN_0054e2c0` **[I]**: `gameplayer_id`, `goal`, `assist`, `uniform_number`, `now_yellow_card_count`, `now_red_card_count`, `total_yellow_card`, `total_red_card`, `injury_all`, `injury_one`, `injury_data`
* `FUN_00551120` **[I]**: `commu_name`, `commu_description`, `invite_only`, `password`, `start_time`, `end_time`, `week`
* `FUN_00552280` **[I]**: `total_count`, `average`
* `FUN_00552990` **[I]**: `match_count`, `win_count`, `lose_count`, `draw_count`, `win_average`, `possession_average`
* `FUN_005533f0` **[I]**: `disp_rank`, `value`, `member_info`
* `FUN_00553c20` **[I]**: `team_id`, `disp_rank`, `value`
* `FUN_005542d0` **[I]**: `compe_id`, `compe_name`, `opening_date`, `finish_date`, `user_count`
* `FUN_00554c70` **[I]**: `compe_name`, `match_time`, `injury`, `ball_type`, `home_and_away`, `comment` (<=245 bytes)
* `FUN_005556b0` **[I]**: `match_status`
* `FUN_00555ff0` **[I]**: `match_count`, `win_count`, `lose_count`, `draw_count`, `win_average`, `possession_average`, `league_win_count`, `cup_win_count`, `continuous_win_current`, `continuous_win_max`, `continuous_lose_current`, `continuous_lose_max`, `best_combination_max`, `total_combination_max`
* `FUN_00556e40` **[I]**: `compe_id`, `opening_date`, `entered_cell_count`, `finished_match_count`, `total_match_count`
* `FUN_0055c9d0` **[I]**: `compe_id`
* `FUN_0055f140` **[I]**: `result`, `is_select_team`
* `FUN_0055f4b0` **[I]**: `result`
* `FUN_005602e0` **[I]**: `result`
* `FUN_00560500` **[I]**: `result`, `player_num`
* `FUN_00560730` **[I]**: `result`, `end_kind`, `is_disqualified`
* `FUN_00560ba0` **[I]**: `result`, `is_match_start`
* `FUN_00560e70` **[I]**: `result`, `limit_time`
* `FUN_00561180` **[I]**: `result`, `is_waiting_time`, `match_start_time`
* `FUN_00561520` **[I]**: `result`, `end_kind`, `is_pk`, `is_draw`, `win_pid`
* `FUN_005637f0` **[I]**: `start_time`, `team_id`, `pid`, `compe_name`, `name`, `trophy`, `npid`, `banner_file_name`, `cup_file_name`
* `FUN_005640f0` **[I]**: `member_num`, `pid`, `team_id`, `name`, `team_name`, `member`, `injury_all`, `injury_one`, `uniform_number`, `now_yellow_card_count`, `now_red_card_count`, `gameplayer_id`, `goal`, `assist`, `total_yellow_card`, `total_red_card`, `injury_data` (<=149 bytes)
* `FUN_005648d0` **[I]**: `pid`, `manner`, `rating`, `team_id`, `name`, `division`, `is_guest_player`, `npid`
* `FUN_00564e20` **[I]**: `name`, `pid`, `team_id`, `manner`, `rating`, `division`, `last_result`, `npid`
* `FUN_00565330` **[I]**: `is_end`, `home_pid`, `away_pid`, `win_pid`, `home_score`, `away_score`
* `FUN_00565710` **[I]**: `name`, `pid`, `team_id`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `has_guestplayer`, `team_name` (<=16 bytes), `xsession_info`
* `FUN_005665f0` **[I]**: `received_size`, `buf_addr`
* `FUN_00567ab0` **[I]**: `match_count`, `match_list_num`, `match_list`, `match_id`, `game_phase`, `home`, `away`
* `FUN_00568010` **[I]**: `trophy`, `match_count`, `player_list_num`, `cup_file_name` (<=101 bytes), `player_list`, `game_result_1`, `game_result_2`, `game_result_3`, `game_result_4`
* `FUN_005687a0` **[I]**: `match_result_num`, `team_id`, `team_name`, `my_rank`, `match_result_list`, `date`, `home`, `away` (<=149 bytes)
* `FUN_00568d20` **[I]**: `match_table_num`, `match_count`, `match_table`, `home`, `away`
* `FUN_00569150` **[I]**: `compe_name`, `trophy`, `entry_count`, `my_result`, `my_pre_rank`, `my_team_id`, `banner_file_name`, `cup_file_name`, `pid`, `last_result`, `team_id`, `npid`, `name`
* `FUN_0056c800` **[I]**: `score_1st`, `score_2nd`, `score_ex1st`, `score_ex2nd`, `score_pk`
* `FUN_0056ca60` **[I]**: `part`, `symptom`, `level`, `level_no_self_conscious`
* `FUN_0056dac0` **[I]**: `ml_num`
* `FUN_0056dc00` **[I]**: `ml_compe_player_num`
* `FUN_0056fa50` **[I]**: `rank`
* `FUN_00570930` **[I]**: `gameplayer_list_num`, `gameplayer_list`
* `FUN_00570bb0` **[I]**: `compe_num`, `compe_list`, `entry_limit`, `entry_limit_division`, `entry_start_time`
* `FUN_005dc690` **[I]**: `pid`, `manner`, `name`, `npid`, `team_id`
* `FUN_005dcc70` **[I]**: `received_size`, `buf_addr`
* `FUN_005dcde0` **[I]**: `date`, `home`, `away`
* `FUN_005dd0e0` **[I]**: `compe_kind`, `compe_id`, `compe_name`, `category`, `dlcver`, `player_capacity`, `entry_count`, `entry_limit`, `is_ml_compe`, `is_coach_mode`, `game_result`, `env_filename_md5`, `highlight_num`
* `FUN_005dde40` **[I]**: `match_total_num`, `list_num`, `dlist`
* `FUN_005e2990` **[I]**: `date`, `start_date`, `end_date`, `total_ranking_num`, `ranking_list_num`, `ranking_list[`, `team_name`, `pid`, `rank`, `point`, `win_count`, `lose_count`, `draw_count`
* `FUN_005f2870` **[I]**: 
* `FUN_005f2fe0` **[I]**: `option`, `pname`
* `FUN_005f5930` **[I]**: `pid`
* `FUN_00601740` **[I]**: `data`
* `FUN_00604880` **[I]**: `read_buffer`, `uri`
* `FUN_0060ec30` **[I]**: `price`, `result`, `gameplayer_id`
* `FUN_006187b0` **[I]**: `rlist_num`, `rlist`, `date`, `ex_type`, `pk_flag`, `no_match`, `player[`, `pid`, `name`, `manner`, `has_guestplayer`, `division`, `rating`, `side`, `team[`, `team_name[`, `score[`, `score_1st`, `score_2nd`, `score_ex1st`, `score_ex2nd`, `score_pk`
* `FUN_0061c010` **[I]**: `player_count`
* `FUN_0061c360` **[I]**: `gameid`, `gameid_password`, `password_type`, `gameid_url_num`, `gameid_url`, `loginid`, `loginid_password`, `svrtype`, `logoutstat`, `account_first_login`, `result`, `lang`
* `FUN_0061f480` **[I]**: `result`
* `FUN_0061f9f0` **[I]**: `member_list_num`, `member_list[`, `pid`, `name`, `player_name`, `is_owner`, `is_leader`, `side`, `manner`, `division`, `rating`, `fd_selfreport_level`, `fd_position_want`, `fd_desired_position`, `ex_ip`, `ex_port`, `in_ip`, `in_port`, `det_ip`, `det_port`
* `FUN_006221e0` **[I]**: `player_list_num`, `player_list`, `name`, `pid`, `division`, `rating`, `manner`, `fd_selfreport_level`, `fd_position_want`, `gameplayer_name`
* `FUN_00645dc0` **[I]**: `player_list_num`, `player_list`, `gameplayer_id`
* `FUN_0064dcc0` **[I]**: `match_record`, `win_num`, `lose_num`, `draw_num`
* `FUN_0064fb80` **[I]**: `fee`, `income`, `now_yellow_card_count`, `now_red_card_count`, `purchase_cnt`, `team_funds`

Transport/bespoke message classes (`00ff…/010…/011…`): `FUN_011d63a0`/`FUN_011d6bf0` = REQWATCHL (see §3); `FUN_01002210` = `CMD_START_QUICKMATCH` request builder (`qmatchsetting`, below); `FUN_00fffdf0` = `MSG_QUICK_MATCHING_START_RES` / `MSG_QUICK_MATCH_MEMBER_NOTICE`; `FUN_010035c0` = `MSG_REQCCODE`/`MSG_REQAUTH` replies (`MSG_CHALLENGE{ccode}` / `MSG_AUTHRESULT{result,first_login}`).

### 7.1 `CMD_START_QUICKMATCH` — `qmatchsetting` object **[V]**
`player_num`, `game_time` (enum 0x41 `GT05|GT10`), `opp_lang` (enum 0x1c, `ANY`=17), `is_chat`, `has_guestplayer`, `frrate` (enum 0x17 `FRAME_50|FRAME_60`), `oc_is_rating`, `oc_is_pcspec` (enum 0x3f `NOT_FILL|FILL`), `fd_is_level`, `fd_position` (enum 0x3e), `fd_posflag`, `is_coach_mode`, `ml_is_division`, `my_pid`, plus a kind from enum 0x40 (`OC_Rank|OC_Free|FD_Free|COMPE|ML_Rank|ML_COMPE`). Pushed back as `MSG_QUICK_MATCHING_START_RES` (`tryCount`, `ping`, `pingAdd`, `bandTryCount`, `band`, `bandAdd`) then `MSG_QUICK_MATCH_MEMBER_NOTICE` (`member_num`, `is_old_member`, `member[]{matching_order,is_owner,pid,pname,has_guestplayer,…}`); also `MSG_QUICK_REQ_MATCHING_START`, `MSG_QUICK_ACCEPT_MATCH_OFFER`, `MSG_QUICK_MATCH_MEMBER_REPLY` (peer-to-peer matching handshake).

## 8. All `ERR_*` codes by command **[V]**

| Command | errors |
|---|---|
| `CMD_ADD_BLACKLIST` | ERR_DATABASE, ERR_INVALIDARG, ERR_INFRIENDLIST, ERR_PLAYERISONESELF, ERR_INBLACKLIST, ERR_FRIENDCOUNTFULL |
| `CMD_ADD_FOUL` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_NOTGAMEHOST, ERR_ROOMSTATNOTMATCH, ERR_INVALIDARG |
| `CMD_ADD_SCORE` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_ROOMSTATNOTMATCH, ERR_MSGFORMAT |
| `CMD_AUTH_GAMEID` | ERR_AUTHGID_URL, ERR_AUTHGID_SSLEXPIRE, ERR_AUTHGID_AUTH, ERR_AUTHGID_SSLETC |
| `CMD_AUTH_NPTICKET` | ERR_GET_TICKETINFO, ERR_READ_PASSPHRASE, ERR_READ_CIPHERINFO, ERR_PREPARE_CIPHERINFO, ERR_INVALID_TICKET |
| `CMD_CANCEL_DECIDE_QUICKMATCH` | ERR_ROOMNOTFOUND |
| `CMD_CANCEL_LEGENDSROOMMATCHING` | ERR_MATCHPHASE, ERR_NOTFOUNDCLIENT |
| `CMD_CANCEL_QUICK_GAMEBEGINNING_HOPE` | ERR_MATCHPHASE, ERR_NOTFOUNDCLIENT |
| `CMD_CANCEL_STARTQUICKMATCH` | ERR_MATCHPHASE, ERR_NOTFOUNDCLIENT |
| `CMD_CHANGE_GAMEPHASE` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_ROOMSTATNOTMATCH, ERR_NOTGAMEHOST |
| `CMD_CHANGE_ROOMNAME` | ERR_NOTINROOM, ERR_NOTROOMOWNER, ERR_INVALIDARG, ERR_NGWORDINRNAME, ERR_ROOMNOTFOUND, ERR_DATABASE |
| `CMD_CHECK_STRING` | ERR_INVALIDLETTER |
| `CMD_CREATEJOIN_ROOM` | ERR_INVALIDARG, ERR_ALREADYINROOM, ERR_NGWORDINRNAME, ERR_ROOMTOOMANY |
| `CMD_CREATE_PLAYER` | ERR_DATABASE, ERR_ALREADYEXISTS, ERR_INVALIDLETTER, ERR_NGWORDINPNAME |
| `CMD_DEL_BLACKLIST` | ERR_DATABASE |
| `CMD_DEL_PLAYER` | ERR_DATABASE, ERR_NOPLAYER, ERR_DELINDAYS |
| `CMD_DISCONNECT` | ERR_DATABASE |
| `CMD_GET_BLOCKLIST` | ERR_DATABASE |
| `CMD_GET_FD_GAMERESULTS` | ERR_DATABASE |
| `CMD_GET_GAMEADDPOINT` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_ROOMSTATNOTMATCH |
| `CMD_GET_GAMEPLAN` | ERR_DATANOTFOUND |
| `CMD_GET_GAMERESULTS` | ERR_DATABASE |
| `CMD_GET_KONAMIID` | ERR_NOTREGISTERED |
| `CMD_GET_LEGENDSROOMMATCHING` | ERR_MATCHING_FAILED, ERR_MATCHING_TIMEOUT, ERR_MATCHING_CANCELED |
| `CMD_GET_LEGENDS_RANKING` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_LEGENDS_RANKING_BYID` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_MATCHID` | ERR_DATABASE |
| `CMD_GET_PLAYERINFO` | ERR_DATABASE |
| `CMD_GET_PLAYERLIST` | ERR_DATABASE |
| `CMD_GET_WEEKLYRANKINGDATELIST` | ERR_WEEKLYRANKINGNOTFOUND |
| `CMD_JOIN_BLOCK` | ERR_BLOCKNOTEXIST, ERR_BLOCKISFULL, ERR_DIVISIONREFUSED |
| `CMD_JOIN_LOBBY` | ERR_LOBBYUSERMAX |
| `CMD_JOIN_ROOM` | ERR_DATABASE, ERR_ROOMNOTFOUND, ERR_INVALIDARG, ERR_ALREADYINROOM, ERR_TOOMANYMEMBERS, ERR_PASSWDINCORRECT, ERR_DIFFDCVERSION, ERR_ROOMISFULL, ERR_ROOMSTATNOTMATCH |
| `CMD_KICK_ROOMMEMBER` | ERR_NOTINROOM, ERR_NOTROOMOWNER, ERR_ROOMSTATNOTMATCH, ERR_TARGETPLAYERNOTEXIST, ERR_PLAYERISONESELF |
| `CMD_LOGIN` | ERR_NOTGETCCODE, ERR_PARAMCHECK_FAILED, ERR_INVALID_ACCOUNT, ERR_DIFFERENT_PATCHVERSION, ERR_DEFCLIENTVER, ERR_DATABASE, ERR_LOGIN_FAILED, ERR_ALREADYLOGGEDIN, ERR_REGCODENOTISSUED, ERR_REGCODE_FAILED, ERR_SERVERNOTREADY, ERR_REGCODEDIFLASTTIME |
| `CMD_REQWATCHL_BLOCKPLAYERLIST` | ERR_NOBLOCKBELONG |
| `CMD_SEARCH_PLAYER` | ERR_TOOMANYRECORDS |
| `CMD_SEND_ABNORMALEND` | ERR_NOTINROOM, ERR_ROOMNOTFOUND |
| `CMD_SEND_ALIVE` | ERR_NOTINROOM |
| `CMD_SEND_CPUKIND` | ERR_DATABASE |
| `CMD_SEND_DISCON_PLAYER` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_NOTFOUNDCLIENT |
| `CMD_SEND_DISCON_SETPLAYER` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_NOTFOUNDCLIENT, ERR_NOTGAMEHOST |
| `CMD_SEND_FRIENDREQ` | ERR_DATABASE, ERR_INFRIENDLIST, ERR_FRIENDCOUNTFULL, ERR_INBLACKLIST, ERR_FRIEND_PERMISSION_WAITING_STATE |
| `CMD_SEND_P2PFAILED` | ERR_DATABASE, ERR_NOTINROOM, ERR_ROOMNOTFOUND |
| `CMD_SEND_P2PSTATUS` | ERR_DATABASE, ERR_NOTINROOM, ERR_ROOMNOTFOUND |
| `CMD_SEND_QUICK_GAMEBEGINNING_HOPE` | ERR_MATCHPHASE, ERR_NOTFOUNDCLIENT |
| `CMD_SEND_SHORTMAIL` | ERR_NOTFOUNDCLIENT |
| `CMD_SEND_TEXTCHAT` | ERR_CHATENABLE, ERR_ISNOTGM, ERR_TARGETINGAME, ERR_TARGETISNOTLOGIN, ERR_ROOMNOTFOUND, ERR_ROOMSTATNOTMATCH, ERR_PLAYERISNOTGAMER |
| `CMD_SET_CURRENTPLAYER` | ERR_REFUSED, ERR_DATABASE, ERR_NOPLAYER |
| `CMD_SET_FORFEITEDGAMEEND` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_REFUSED, ERR_ROOMSTATNOTMATCH |
| `CMD_SET_GAMEENV` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_NORFOUNDCLIENT, ERR_ROOMSTATNOTMATCH, ERR_NOTGAMEHOST |
| `CMD_SET_GAMEMEMBERENV` | ERR_ROOMSTATNOTMATCH, ERR_NOTGAMEHOST, ERR_MSGFORMAT, ERR_NOTINROOM, ERR_ROOMNOTFOUND |
| `CMD_SET_GAMEPLAN` | ERR_DATABASE |
| `CMD_SET_GAMETEAM` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_ROOMSTATNOTMATCH, ERR_NOTGAMEHOST |
| `CMD_SET_GUEST` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_ROOMSTATNOTMATCH, ERR_ROOMISFULL, ERR_TOOMANYGUEST, ERR_NOGUEST, ERR_CANTADDGUEST |
| `CMD_SET_INJURYGAMEEND` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_NOTGAMEHOST, ERR_ROOMSTATNOTMATCH |
| `CMD_SET_KONAMIID` | ERR_LOGIN_FAILED, ERR_ALREADYREGISTERED |
| `CMD_SET_PLAYERPROFILE` | ERR_INVALIDDATE, ERR_NGWORDININTRO, ERR_DATABASE |
| `CMD_SET_XSESSION` | ERR_NOTINROOM, ERR_NOTROOMOWNER, ERR_ROOMNOTFOUND |
| `CMD_START_QUICKMATCH` | ERR_GETPLAYERFAIL, ERR_QMATCH_STARTMATCHING, ERR_QMATCH_NOTICE, ERR_QMATCH_PRECONNECT, ERR_QMATCH_CANCELED, ERR_PING_FAILED, ERR_PLAYERNOTEXIST, ERR_REFUSED, ERR_QMATCH_CONNECTFAILED, ERR_CHECK_LANG, ERR_CHECK_GUEST, ERR_CHECK_CPU, ERR_CHECK_NATTYPE, ERR_CHECK_BLACKLIST |
| `CMD_UPDATE_COMBINATION` | ERR_NOTINROOM, ERR_ROOMNOTFOUND, ERR_NOTFOUNDCLIENT |
| `CMD_WATCH_TEXTCHAT` | ERR_CHATENABLE, ERR_ISNOTGM, ERR_ROOMSTATNOTMATCH |
| `CMD_ADD_COMPLG_RESULTS` | ERR_DATABASE |
| `CMD_CREATE_COMPLG` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_ASSISTRANKING` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_COMMENDATION_DATA` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_DIGESTLIST` | ERR_DATABASE |
| `CMD_GET_COMPLG_GOALRANKING` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_INFO` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_KIND_LIST` | ERR_DATABASE |
| `CMD_GET_COMPLG_LIST` | ERR_DATABASE |
| `CMD_GET_COMPLG_MYENTRY_COMPEID` | ERR_DATABASE |
| `CMD_GET_COMPLG_MYRESULTS` | ERR_DATABASE |
| `CMD_GET_COMPLG_PLAYOFF_MATCH` | ERR_DATABASE, ERR_COMPE_STATUS, ERR_COMPE_NOT_PLAYOFF_ENTRY |
| `CMD_GET_COMPLG_PLAYOFF_PLAY_OK` | ERR_DATABASE, ERR_COMPE_NOTFOUND, ERR_COMPE_STATUS |
| `CMD_GET_COMPLG_PLAYOFF_RESULTS` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_PRE_RESULTS` | ERR_DATABASE, ERR_COMPE_NOTFOUND, ERR_TEAM_NOTFOUND, ERR_INDEX_OUTOFRANGE |
| `CMD_GET_COMPLG_TEAMRANKING` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_GET_COMPLG_TEAM_DATA` | ERR_DATABASE, ERR_PLAYERNOTINCOMPE, ERR_COMPE_NOT_SELECT_TEAM |
| `CMD_GET_COMPLG_TITLEHOLDER` | ERR_DATABASE |
| `CMD_GET_COMPLG_TOURNAMENT` | ERR_DATABASE, ERR_COMPE_NOTFOUND, ERR_COMPE_STATUS |
| `CMD_JOIN_COMPLG` | ERR_DATABASE, ERR_COMPE_JOIN_OTHERCOMPE, ERR_COMPE_BEFORE_ENTRYTIME, ERR_COMPE_AFTER_ENTRYTIME, ERR_COMPE_FULL, ERR_DIFFDCVERSION, ERR_COMPE_JOIN_DIVISION, ERR_COMPE_JOIN_RANKING, ERR_COMPE_FUNDSHORTAGE, ERR_COMPE_PLAYERSHORTAGE, ERR_NOTREGISTERED |
| `CMD_LEAVE_COMPLG` | ERR_DATABASE, ERR_COMPE_NOTENTRY |
| `CMD_QUIT_COMPLG` | ERR_DATABASE, ERR_COMPE_NOTENTRY |
| `CMD_REQWATCH_COMPLG_PLAYERNUM` | ERR_DATABASE, ERR_COMPE_NOTFOUND |
| `CMD_SET_COMPLG_TEAM` | ERR_DATABASE, ERR_COMPE_NOTFOUND, ERR_COMPE_NOTENTRY |
| `CMD_APPLY_COMMU_MATCH` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_MATCHNOTEXIST, ERR_TARGETINGAME, ERR_PLAYERNOTINCOMPE, ERR_BUSY, ERR_COMMUMATCHREFUSED, ERR_DATABASE |
| `CMD_CANCEL_COMMU_MATCH` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_PLAYERISNOTGAMER |
| `CMD_CREATE_COMMU` | ERR_NGWORD, ERR_TOOMANYCOMMU, ERR_DATABASE |
| `CMD_CREATE_COMMU_LEAGUE` | ERR_COMMUNOTEXIST, ERR_NGWORD, ERR_COMMUCOMPEISFULL, ERR_DIFFDCVERSION, ERR_NOTJOINEDCOMMU |
| `CMD_DEL_COMMU` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_NOTCOMMUOWNER |
| `CMD_DEL_COMMU_LEAGUEMEMBER` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_NOTCOMMUOWNER, ERR_NOTENTRY |
| `CMD_DEL_COMMU_MEMBER` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_PLAYERNOTINCOMMU, ERR_NOTCOMMUOWNER |
| `CMD_END_COMMU_LEAGUE` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_NOTCOMMUOWNER |
| `CMD_ENTRY_COMMU_LEAGUECELL` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_ENTRYISFULL, ERR_DATABASE, ERR_DIFFDCVERSION |
| `CMD_GET_COMMU_ASSISTRANKING` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_COMMUCOMPENOTEXIST, ERR_DATABASE |
| `CMD_GET_COMMU_COMPERANKING` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_COMMUCOMPENOTEXIST, ERR_DATABASE |
| `CMD_GET_COMMU_COMPERESULT` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_COMMUCOMPENOTEXIST, ERR_DATABASE |
| `CMD_GET_COMMU_GOALRANKING` | ERR_NOTJOINEDCOMMU, ERR_COMMUCOMPENOTEXIST, ERR_DATABASE |
| `CMD_GET_COMMU_INFO` | ERR_COMMUNOTEXIST |
| `CMD_GET_COMMU_LEAGUEENTRYCELLINFO` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST |
| `CMD_GET_COMMU_LEAGUELIST` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_DATABASE |
| `CMD_GET_COMMU_MEMBERRANKING` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_DATABASE |
| `CMD_GET_COMMU_MEMBERSRESULTS` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_PLAYERNOTINCOMMU, ERR_DATABASE |
| `CMD_GET_COMMU_MEMBERSRESULTS_VS` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_PLAYERNOTINCOMMU |
| `CMD_GET_COMMU_PASTLEAGUELIST` | ERR_COMMUNOTEXIST |
| `CMD_GET_COMMU_PASTLEAGUERESULT` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST |
| `CMD_GET_COMMU_TEAM` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST |
| `CMD_GET_COMMU_TEAMLIST` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU |
| `CMD_GET_COMMU_TEAMRANKING` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU |
| `CMD_GET_COMMU_TEAMRESULTS` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_PLAYERNOTINCOMMU, ERR_NOTJOINEDCOMMU |
| `CMD_GET_COMMU_TEAMRESULTS_VS` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU |
| `CMD_GET_JOINEDCOMMULIST` | ERR_DATABASE |
| `CMD_JOIN_COMMU` | ERR_COMMUNOTEXIST, ERR_PASSWDINCORRECT, ERR_DATABASE, ERR_COMMUNITYISFULL, ERR_ALREADYINCOMMU, ERR_TOOMANYCOMMU |
| `CMD_JOIN_COMMU_LEAGUE` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_DATABASE, ERR_DIFFDCVERSION |
| `CMD_LEAVE_COMMU_LEAGUE` | ERR_PLAYERNOTINCOMPE |
| `CMD_REPLY_COMMU_MATCH` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_MATCHNOTEXIST, ERR_COMMUMATCHCANCELED |
| `CMD_REQWATCHL_COMMU_COMPEMEMBERLIST` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST |
| `CMD_REQWATCHL_COMMU_MATCHLIST` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST, ERR_DATABASE |
| `CMD_REQWATCHL_COMMU_MEMBERLIST` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST |
| `CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST` | ERR_DATABASE, ERR_MATCHNOTEXIST, ERR_COMMUNOTENTRY |
| `CMD_RESET_CURRENT_COMMU` | ERR_DATABASE, ERR_PLAYERNOTINCOMMU, ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU |
| `CMD_SEARCH_COMMU` | ERR_TOOMANYLIST, ERR_INVALIDARG, ERR_NOPLAYER |
| `CMD_SEARCH_COMMU_BYRANKING` | ERR_TOOMANYLIST, ERR_INVALIDARG, ERR_NOPLAYER |
| `CMD_SET_COMMU_MATCHINFO` | ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU, ERR_PLAYERNOTINCOMMU, ERR_ROOMSTATNOTMATCH, ERR_DATABASE, ERR_INVALIDARG |
| `CMD_SET_COMMU_OPTION` | ERR_COMMUNOTEXIST, ERR_NOTCOMMUOWNER, ERR_NGWORD |
| `CMD_SET_COMMU_TEAM` | ERR_COMMUNOTEXIST, ERR_COMMUCOMPENOTEXIST |
| `CMD_SET_CURRENT_COMMU` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_NOTJOINEDCOMMU |
| `CMD_WATCH_COMMU_MATCH_WAITING` | ERR_COMMUMATCHCANCELED |
| `CMD_WITHDRAW_COMMU` | ERR_DATABASE, ERR_COMMUNOTEXIST, ERR_PLAYERNOTINCOMMU |
| `CMD_ACK_ML_MARKET_PRICE` | ERR_DATABASE |
| `CMD_ADD_ML_EVENT_COMPE_TEST` | ERR_DATABASE |
| `CMD_ADD_ML_MARKET_CHECKLIST` | ERR_DATABASE |
| `CMD_BUY_ML_MARKET_GAMEPLAYER` | ERR_DATABASE, ERR_INSUFFICIENT_FUNDS, ERR_TRADE_STOP, ERR_NOTFOUND, ERR_TEAM_MEMBER_FULL, ERR_ENABLE_BUY_NUM |
| `CMD_CREATE_ML_MYTEAM` | ERR_DATABASE, ERR_ALREADYEXISTS, ERR_LICENSE, ERR_NOTFOUND, ERR_NGWORD |
| `CMD_DECIDE_ML_MYTEAM_PLAYER` | ERR_DATABASE |
| `CMD_DELETE_ML_MARKET_CHECKLIST` | ERR_DATABASE |
| `CMD_DELETE_ML_MYTEAM` | ERR_DATABASE |
| `CMD_GET_ML_COMPE_GAMEPLAYERLIST` | ERR_DATABASE |
| `CMD_GET_ML_COMPE_PRIZE` | ERR_DATABASE |
| `CMD_GET_ML_DIVISION` | ERR_DATABASE |
| `CMD_GET_ML_DIVISIONRANKING` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_ML_DIVISIONRANKING_BYID` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_ML_DIVISIONUPDATE` | ERR_DATABASE |
| `CMD_GET_ML_EVENTLIST` | ERR_DATABASE |
| `CMD_GET_ML_GAMEPLAN` | ERR_DATANOTFOUND |
| `CMD_GET_ML_GAMERESULTS` | ERR_DATABASE |
| `CMD_GET_ML_MARKET_CHECKLIST` | ERR_DATABASE |
| `CMD_GET_ML_MARKET_ENV` | ERR_DATABASE |
| `CMD_GET_ML_MARKET_GAINRANKING` | ERR_DATABASE |
| `CMD_GET_ML_MARKET_GAMEPLAYERPRICEHISTORY` | ERR_DATABASE |
| `CMD_GET_ML_MARKET_POPULARRANKING` | ERR_DATABASE |
| `CMD_GET_ML_MARKET_PRICERANKING` | ERR_DATABASE |
| `CMD_GET_ML_MATCHEXPENSE` | ERR_DATABASE |
| `CMD_GET_ML_MATCHINCOME_INFO` | ERR_DATABASE, ERR_INVALIDARG, ERR_NOTFOUND |
| `CMD_GET_ML_MATCHRECORD` | ERR_DATABASE |
| `CMD_GET_ML_MYRANKING` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_ML_MYTEAM` | ERR_DATABASE, ERR_INVALIDARG, ERR_NOTFOUND |
| `CMD_GET_ML_MYTEAM_EVENTLIST` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_GET_ML_MYTEAM_GAMEPLAYERLIST` | ERR_DATABASE |
| `CMD_GET_ML_MYTEAM_GAMEPLAYERLIST_OLD` | ERR_DATABASE |
| `CMD_GET_ML_MYTEAM_HISTORY` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_GET_ML_NEWS_LIST` | ERR_DATABASE |
| `CMD_GET_ML_RANKING` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_ML_RANKING_BYID` | ERR_DATABASE, ERR_NOTFOUND |
| `CMD_GET_ML_REGULAREVENTLIST` | ERR_DATABASE |
| `CMD_GET_ML_TRADEHISTORY` | ERR_DATABASE |
| `CMD_REQWATCH_ML_MARKET_PRICE` | ERR_NOTFOUND, ERR_COMPE_ENTRY |
| `CMD_RESET_ML_MYTEAM` | ERR_DATABASE, ERR_FORBIDDEN |
| `CMD_SEARCH_ML` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_SEARCH_ML_ALL` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_SEARCH_ML_LEAGUE` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_SEARCH_ML_NATIONAL` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_SELL_ML_MARKET_GAMEPLAYER` | ERR_DATABASE, ERR_TRADE_STOP, ERR_TEAM_MEMBER_LACK |
| `CMD_SEND_ML_GAMEPLAYERS` | ERR_DATABASE |
| `CMD_SET_ML_GAMEPLAN` | ERR_DATABASE |
| `CMD_SET_ML_MYTEAM_BASE` | ERR_DATABASE, ERR_FORBIDDEN |
| `CMD_SET_ML_MYTEAM_NAME` | ERR_DATABASE, ERR_NGWORD, ERR_LICENCE, ERR_NOTFOUND, ERR_FORBIDDEN |
| `CMD_SET_ML_MYTEAM_TYPE` | ERR_DATABASE, ERR_INVALIDARG |
| `CMD_SET_ML_MYTEAM_UNIFORMNUMBER` | ERR_DATABASE, ERR_FORBIDDEN |
| `CMD_SET_ML_NEWS_COMMENT` | ERR_DATABASE, ERR_NGWORD |

## 9. State machines (raw) **[V]**

`FUN_xxx` = function; `sN` = `*(this+0x18)` state value; `->` transitions via `FUN_00521850`; `*` = command sent through a helper function; 99998 = done, 99999 = error.

* `FUN_005db750`: s0 ->1 ; s1 [GET_PLAYERNUMBERS] ->2 ; s2 ->3 ; s3 ->99998
* `FUN_005e0ec0`: s0 ->1 ; s1 ->2 ; s2 [SET_GUEST] ->3 ; s3 ->4/7/99998 ; s5 ->6/8 ; s8 [SET_GUEST] ->9 ; s9 ->99998
* `FUN_005e18c0`: s1 ->99998 ; s0 ->2 ; s2 [ENTRY_GAME] ->3 ; s3 ->99998
* `FUN_005e4d60`: s0 ->1 ; s1 [JOIN_ROOM] ->2 ; s2 ->4/99998 ; s3 ->7/12 ; s5 [GET_IPANDPORT] ->6 ; s6 ->3 ; s8 ->9/10 ; s9 ->10/99998 ; s10 ->11 ; s11 ->12 ; s12 [LEAVE_ROOM] ->13 ; s13 ->99999
* `FUN_005e5c20`: s4 [CREATEJOIN_ROOM] ->
* `FUN_005e6f20`: s0 ->1 ; s1 [SET_GUEST] ->2 ; s2 ->5 ; s3 [LEAVE_ROOM] ->4 ; s4 ->99998 ; s5 ->6 ; s6 ->3/7 ; s7 ->8/9 ; s8 ->3/9 ; s9 ->3
* `FUN_005e9400`: s0 ->1 ; s1 [CANCEL_STARTQUICKMATCH] ->2 ; s2 ->3/99998 ; s3 ->1
* `FUN_005ed6a0`: s0 ->1 ; s1 [START_QUICKMATCH] ->2 ; s2 ->3/14/16/99998 ; s3 ->4/8/9 ; s4 [SET_GAMEMEMBERENV] ->5 ; s5 ->6 ; s6 [SET_GAMEMEMBER] ->7 ; s7 ->8 ; s8 ->12/20 ; s10 ->11/18/20 ; s11 ->12/18 ; s12 [CANCEL_DECIDE_QUICKMATCH] ->13/14 ; s14 [LEAVE_ROOM] ->15 ; s16 [CANCEL_STARTQUICKMATCH] ->17 ; s17 ->99999 ; s18 ->19/20 ; s19 ->20 ; s20 ->12
* `FUN_005eea40`: s0 ->1 ; s1 ->2/4 ; s2 [DISCONNECT] ->3 ; s3 ->4/99998 ; s4 [LOGIN*] ->5 ; s5 ->8 ; s6 [SEND_IPANDPORT] ->7 ; s8 [SET_CURRENTPLAYER] ->9 ; s9 ->6/99998
* `FUN_005f7de0`: s0 ->1 ; s1 [CREATE_PLAYER] ->2 ; s2 ->3/99998 ; s3 [SET_LANGUAGE] ->4 ; s4 ->99998
* `FUN_005f81a0`: s0 ->1 ; s1 ->2/4/6 ; s2 [DISCONNECT] ->3 ; s3 ->4/99998 ; s4 [LOGIN*] ->5 ; s5 ->6/99998 ; s6 [DEL_PLAYER] ->7
* `FUN_005fa030`: s0 [WATCH_ABNORMALEND,WATCH_FORFEITEDGAMEEND,WATCH_INJURYGAMEEND,WATCH_OWNGOALGAMEEND] ->2 ; s1 [GET_GAMEADDPOINT*,REQ_FORFEITEDGAME_REPLY,SET_INJURYGAMEEND] ->2/5 ; s2 ->1/3 ; s3 [ADD_SCORE,SEND_ABNORMALEND] ->2/4 ; s4 [ADD_FOUL] ->2 ; s5 [SET_GAMETEAM] ->1/2/99998 ; s6 [GET_GAMEADDPOINT*] ->99998 ; s7 [UPDATE_ROOMSTATE] ->2 ; s8 ->2 ; s9 ->2 ; s10 [SEND_ABNORMALEND,SEND_ABNORMALEND] ->2 ; s11 [SET_GAMEMEMBERENV] -> ; s13 [SET_COMMU_MATCHINFO] -> ; s14 [SEND_ML_GAMEPLAYERS] ->2
* `FUN_00605070`: s0 [GET_ML_MYTEAM*] -> ; s2 [GET_ML_MYTEAM_GAMEPLAYERLIST*] -> ; s4 [GET_ML_MARKET_CHECKLIST*] -> ; s6 [DELETE_ML_MARKET_CHECKLIST] -> ; s8 [GET_ML_GAMEPLAN*] ->
* `FUN_0060d1a0`: s3 [REQWATCH_ML_MARKET_PRICE] -> ; s4 [ADD_ML_MARKET_CHECKLIST] -> ; s5 [DELETE_ML_MARKET_CHECKLIST] -> ; s6 [GET_ML_MARKET_GAMEPLAYERPRICEHISTORY] ->
* `FUN_0061b940`: s0 ->1 ; s1 [GET_PLAYERLIST] ->2 ; s2 ->3/99998 ; s3 [GET_PRIVATEINFO] ->4 ; s4 ->5 ; s5 [GET_DIVISIONUPDATE] ->6 ; s6 ->99998
* `FUN_0061c360`: s0 ->1 ; s1 ->2/4 ; s2 [AUTH_GAMEID] ->3 ; s3 ->4/99998 ; s4 ->5/7 ; s5 [DISCONNECT] ->6 ; s6 ->7/99998 ; s7 [LOGIN*] ->8 ; s8 ->9/99998 ; s9 [GET_PLAYERLIST] ->10 ; s10 ->11/99998 ; s11 [SET_LANGUAGE] ->12 ; s12 ->99998
* `FUN_0061cf20`: s0 ->1 ; s1 [GET_INFORMATIONLIST] ->2 ; s2 ->3/99998 ; s3 [DISCONNECT] ->4 ; s4 ->99998
* `FUN_0061e620`: s0 ->1 ; s1 [REG_SVRLIST] ->2 ; s2 ->5 ; s3 [GET_SVRVERSION] ->4 ; s4 ->9/99998 ; s5 [GET_SVRLIST] ->6 ; s6 ->3/7/99998 ; s7 [GET_HTTP] ->8 ; s9 [GET_SVRTIME] ->10 ; s10 ->13 ; s13 [GET_URLLIST] -> ; s14 ->15 ; s15 [DISCONNECT] ->16 ; s16 ->17/99998 ; s17 [REG_SVRLIST] -> ; s18 ->99998
* `FUN_00624790`: s0 ->1 ; s1 [GET_GAMERESULTS] ->2 ; s2 ->3/99998 ; s3 ->99998
* `FUN_00641650`: s2 [REQWATCHL_ROOMPLAYERLIST] -> ; s3 ->99998
* `FUN_006441b0`: s0 ->1 ; s2 ->3 ; s3 [GET_GAMEADDPOINT] ->4 ; s4 ->99998 ; s7 [SET_COMMU_MATCHINFO] ->8 ; s1 ->99998 ; s8 ->99998
* `FUN_00657c20`: s32 [SET_FORFEITEDGAMEEND] -> ; s35 [REQ_FORFEITEDGAME_REPLY] -> ; s38 [LEAVE_ROOM] ->
* `FUN_0065a7d0`: s0 ->1/5/7 ; s1 [SET_GAMEMEMBER] ->2 ; s2 ->7 ; s3 [SET_GAMEMEMBERENV] ->4 ; s4 ->1/99998 ; s5 ->99998 ; s7 ->99998

## 10. Value ranges & limits

### 10.1 Enumerations
Cardinality of every enum is in §2. Enum values travel as **names** and must match the table exactly (case-sensitive). Notable domains:

* `division` (0x16): `D5 D4 D3 D2 D1 NONE`. A name outside the table broke `GET_PLAYERINFO` parsing in live testing (error / crash), so always send a valid one.
* `lang` (0x1c): `EN FR GR IT SP JP KR US PR MX RU HE SV NL BR TR CH ANY` (18).
* `pcspec` (0x3a): `LOW MID HIGH`; `frame_rate` (0x17): `FRAME_50 FRAME_60`; `region` (0x43): `REGION_ALL REGION_EU REGION_NA REGION_ASIA`.
* Two different booleans exist: `YES/NO` (0x4c, almost everywhere) and `TRUE/FALSE` (0x01, e.g. `CMD_ENTRY_GAME.entry`).
* `list_type` (0x1f): `ADD DEL UPDATE`.

### 10.2 Collection sizes

* `room_list` <= 100, `blockplayer_list` <= 100, `roomplayer_list` <= 8 (client `list_max`); community member lists <= 32, community match list <= 9 / playable <= 300, competition match list <= 8 (section 3).
* `desiredPosition[0..12]` — exactly 13 YES/NO flags in enum 0x3e order `GK LIB SW CB SB DMF WB CMF SMF OMF WG ST CF` (client loops `i < 0xd`).
* `team_flag` — list of 3 YES/NO values observed (`[YES,YES,NO]`).
* `ulist[]` (URL list): entry type is enum 0x4b (21 values); the client does not record the type of index 0 (see GateHandlers: dummy `HTTP_TEST` entry first).
* Request/response frame body <= 0x400 bytes (section 1).

### 10.3 String buffer limits (client-side copy sizes in bytes **[C]**; key/limit pairing follows getter order inside each parser, treat as upper bounds)

| key | limit(s) |
|---|---|
| `ad_file_name` | 101 |
| `away` | 149 |
| `banner_file_name` | 15, 101 |
| `client` | 15 |
| `cmd` | 1000 |
| `comment` | 245 |
| `compe_name` | 96, 101 |
| `cup_file_name` | 32, 101 |
| `dlcontent` | 16 |
| `from_name` | 48 |
| `gameplayer_name` | 48 |
| `injury_data` | 149 |
| `intro` | 256 |
| `konamiid` | 511 |
| `mes_body` | 1536 |
| `mes_subject` | 192 |
| `name` | 16, 32 |
| `no_data` | 8192 |
| `npid` | 101 |
| `patch_version` | 16 |
| `team_name` | 16, 144 |
| `to_name` | 48 |

## 11. What the server still has to do (from live logs)

Commands the real client sent that OpenEleven answered with the **generic ack** (`result="NOERR"`, no payload):

* `CMD_GET_BLACKLIST` — expected reply fields: `pid`, `name`, `manner`, `division`, `rating`, `status`, `lang`, `index`, `npid` (<=101 bytes)
* `CMD_GET_FRIENDLIST` — expected reply fields: `pid`, `name`, `manner`, `division`, `rating`, `status`, `lang`, `index`, `npid` (<=101 bytes)
* `CMD_GET_QUICKMATCHOPTION` — expected reply fields: `opp_lang`, `oc_is_rating_rank`, `oc_is_rating_free`, `ml_is_division` (enum 0x4c)
* `CMD_JOIN_LOBBY` — expected reply fields: (none mapped, probably just `result`)
* `CMD_REQWATCHL_ROOMPLAYERLIST` — expected reply fields: `roomplayer_list_num`, `roomplayer_list[`, `pid`, `room_player_id`, `room_entry_no`, `game_entry_no`, `desiredPosition[` (enum 0x4c), `gameplayer_name` (<=48 bytes) · list shape `listnum_name="roomplayer_list_num",list_name="roomplayer_list",listid_name="pid",list_max=8`
* `CMD_SEND_IPANDPORT` — expected reply fields: (none mapped, probably just `result`)
* `CMD_SEND_P2PSTATUS` — expected reply fields: (none mapped, probably just `result`)
