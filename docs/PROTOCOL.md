# DCS-UCS Protocol Reference

All traffic is UDP. Local links never leave `127.0.0.1`.

```
 DCS (player PC)                                   UCS-Client.exe                 UCS-Server.exe
 ┌───────────────────────┐  5051  ┌──────────────┐                     5055
 │ UCS-Export.lua        │ ─────▶ │              │  JSON  ┌──────────────────┐
 │ (Export environment)  │ ◀───── │  SyncEngine  │ ◀────▶ │ relay by         │ ◀──▶ other crew
 └───────────────────────┘  5052  │              │        │ SessionKey +     │      members
 ┌───────────────────────┐  5053  │              │        │ aircraft         │
 │ UCS-Hook.lua (GameGUI)│ ─────▶ │              │        └──────────────────┘
 └───────────────────────┘        └──────────────┘
          ▲  private chat "UCS_AUTOCONNECT:<addr>"
 DCS server: UCS-ServerHook.lua
```

## Export ⇄ Client (text, `|` separated)

| Direction | Message | Meaning |
|---|---|---|
| Export → Client | `HELLO\|<version>` | Mission started |
| Export → Client | `SELF\|<aircraft>\|<0/1>` | Every second: aircraft name (`LoGetSelfData().Name`) and whether sync is active |
| Export → Client | `ARG\|17=1.000;30=0.500` | Local cockpit changes (> 0.01), 20 Hz scan |
| Export → Client | `SNAP\|...` | Full state, answer to `SNAPSHOT` (stored, never broadcast) |
| Export → Client | `BYE` | Mission ended |
| Client → Export | `PROFILE_BEGIN\|<aircraft>\|<parts>` / `PROFILE_PART\|<aircraft>\|<i>\|arg:dev:cmd;...` / `PROFILE_END\|<aircraft>` | Arguments to watch (chunked, LuaSocket reads at most 8 KB per datagram) |
| Client → Export | `STANDBY` | No profile for this aircraft: do nothing |
| Client → Export | `SET\|17=1;30=0.5` | Remote changes → `GetDevice(dev):performClickableAction(cmd, val)` |
| Client → Export | `SNAPSHOT` | Send the full state once |

Loop prevention: after a `SET`, the argument is locked for 0.3 s (`ignore_until[arg]`) and its
last value is updated, so the resulting animation is not echoed back. The Client also drops
remote values that already match the local cockpit (± 0.01).

## Hook → Client (JSON)

```json
{"type":"slot","server":"1.2.3.4:10308","group":"Rescue-1","unit":"Rescue-1-1","aircraft":"UH-60L","in_aircraft":true}
{"type":"autoconnect","addr":"192.168.1.50:5055"}
```

`server` is `"LOCAL"` in single player. Auto-connect messages are only accepted from the
DCS server (player id 0/1) so other players cannot redirect you.

## Client ⇄ Server (JSON, snake_case)

| Type | Direction | Fields |
|---|---|---|
| `state` | C → S, every 2 s and on change | `client_id, name, version, server, group, unit, room, aircraft, standby` |
| `welcome` | S → C, answer to `state` | `session_key, peers, peer_names` |
| `args` | C → S → C | `client_id, aircraft, seq, values{"arg":value}`, optional `target` |
| `peer_joined` | S → oldest crew member | `client_id, name` → that client sends a targeted `args` snapshot |
| `leave` | C → S | `client_id` |
| `error` | S → C | `message` |

`SessionKey = "{server}|{group}|{unit}"` (or `"{server}|{group}"` with `"SessionMatch": "Group"`,
or `"ROOM|{room}"` with a manual room). Packets are relayed only between clients with the same
key **and** the same aircraft, not in standby, seen in the last 15 s.
