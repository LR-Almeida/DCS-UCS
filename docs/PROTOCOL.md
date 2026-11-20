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
| Client → Export | `FLIGHT\|arg:axis:min:max;...` | 0.1.2+: flight-control animation args (stick, pedals, collective) from the profile's `flight` list |
| Client → Export | `FMODE\|<send>\|<inject>\|<diag>\|axis=cmd:sign;...` | 0.1.2+: flight channel settings and the DCS input command per axis |
| Client → Export | `FSET\|15=0.5;11=-0.2` | 0.1.2+: flight-control positions of the crew member flying |
| Export → Client | `FLT\|15=0.5000;11=-0.2000` | 0.1.2+: local flight-control positions, up to 30 Hz, on change (> 0.002) |
| Export → Client | `DIAG\|<text>` | 0.1.2+: diagnostic messages (flickering args, injection pauses), shown in the client log |

Loop prevention: after a `SET`, the argument is locked for 0.3 s (`ignore_until[arg]`) and its
last value is updated, so the resulting animation is not echoed back. The Client also drops
remote values that already match the local cockpit (± 0.01).

### Flight controls (0.1.2+, experimental)

Stick, pedals and collective are not clickable: the mod animates them every frame from its own
flight model (`mainpanel_init.lua` "parameter" gauges such as `CYCLIC_PITCH_COCKPIT` or
`PITCH_INPUT`). Writing those animation args from outside would only flicker (two sources
fighting over one argument), so UCS mirrors them differently:

- Every seat sends its flight args (`FLT` → `flight` message, separate rate budget on the server).
- **Injection (automatic, on by default):** the seat that is *not* flying feeds the received positions
  into DCS as joystick input (`LoSetCommand(cmd, -1..1)`), so the mod itself animates that seat's stick
  like the pilot's. In the seat that has the controls this really flies the aircraft (verified in single
  player), so the Export checks **every frame** `LoGetHelicopterFMData()` / `LoGetFMData()`.`master`
  ("player is actually in control of flight model for multiplayer multicrew sessions", DCS
  `Scripts/Export.lua`) and injects only while it is `false`. Without that flag (single player, or the
  server restricts ownship export) it falls back to the seat: `LoGetPlayerSeat()` > 0, else the hook's
  seat > 1. The Export reports `CTRL|master|seat|mirroring`; the seat that mirrors sends no `FLT`. Each axis has candidate commands (`client_config.json` →
  `FlightAxisCommands`, `axis=cmd[:-1],cmd...`): the Export tries them in order until the animation
  follows a clear deflection (0.6 s each) and logs the one it keeps. Command ids read from the DCS input
  environment: 2001 pitch, 2002 roll, 2003 rudder, 2004 thrust, 2087 `iCommandPlaneCollective`.
  OH-6A 1.7 (verified in game): pitch 2001 inverted, roll 2002, rudder 2003, collective 2004 inverted
  (it ignores 2087).
- Safety: per axis, if the local animation disagrees with the injected value for more than 1 s
  (someone moves the local control) that axis pauses for 3 s; after 3 pauses in a row it is
  disabled and reported (wrong command, inverted axis). Nothing is injected when the stream stops
  (> 0.5 s without `FSET`).
- **Diagnostics:** `FMODE` diag = 1 writes `Saved Games\DCS\Logs\UCS\UCS-diag-<time>.csv` every frame
  (local and remote value of each flight arg, injected values, paused axes) and reports, every
  5 s, any cockpit argument that bounces between two values (direction reversals frame to frame).

### Trim hand-over (0.1.5+)

The profile lists the mod's trim commands found in `command_defs.lua` (`trimRelease`,
`trimReleaseRelease`, `trimSet`, `trimReset`; Client → Export `TRIM|release|releaseEnd|set|reset`).
On the OH-6A the cockpit stick animation is input + trim, and the trim lives inside the mod's flight
model (not readable). So the trim is handed over with the controls instead of being copied:

- losing the controls → `trimReset`: the mirrored stick shows exactly the pilot's position;
- taking the controls → `trimRelease`, `trimReleaseRelease` (or `trimSet`) while the input still holds
  the mirrored position, then the pitch/roll (and yaw when the mod's `PEDAL_TRIM` is on) inputs go back
  to centre: the new pilot's trim is the position the aircraft was being flown with, so a spring
  joystick at centre continues seamlessly. Collective keeps the mirrored value until the physical
  control moves. Skipped when the mod's `TRIMMING_METHOD` is 1 (force feedback / no springs).

The pilot also re-sends the full flight state every 0.4 s, and the seat without the controls never
pauses the mirror while DCS reports who has them (pausing only showed the native multicrew flicker).

### External states (0.1.6+)

Some states are changed only from the keyboard and animate the external model: OH-6A doors
(`Keys.ToggleDoors` → external arg 38), landing light (`Keys.LandingLight` → arg 115)... The profile
generator scans the mod's cockpit scripts: for each script, the external draw args it sets and the
`Keys.*` commands it listens to, skipping weapons/engine/fuel/cargo scripts, brightness/movement commands
and commands that only press a cockpit switch (`performClickableAction`, already synced by the switch
channel). It also ties each argument to the command that changes the variable animating it
(`LandingLight` sets `taxi_light`, which animates arg 115). Client → Export:
`EXTDEF|args:cmds:arg>cmd,cmd/arg>cmd|...`.

The Export reads the external args (`LoGetAircraftDrawArgumentValue`), sends settled changes (`EXT`,
relayed as `ext` messages, server feature `ext_state`) and every 5 s the full state (`EXTFULL`, kept by the
client for late joiners). The initial state after entering an aircraft is never broadcast: the crew member
already in the aircraft wins. A receiver whose settled value differs by more than 0.3 presses the mod's own
command, verifies it after 2.5 s and learns it; it acts only with a command known to change that argument,
restores any cockpit switch the press moved, undoes a toggle that changed another argument, gives up after
8 tries, and never lets an older crew state undo a change made in its own seat.

## Hook → Client (JSON)

```json
{"type":"slot","server":"MP:My Server/Rescue Ops","group":"Rescue-1","unit":"Rescue-1-1","aircraft":"UH-60L","in_aircraft":true}
{"type":"autoconnect","addr":"192.168.1.50:5055"}
```

`server` is `"LOCAL"` in single player and `"MP:{name of player id 1 (the DCS server / host)}/{mission name}"`
in multiplayer (0.1.9+; earlier hooks sent the server `ip:port` to clients and `MP:...` only to the host, so a
crew that included the player hosting the DCS server never paired, and a 0.1.8 hook does not pair with a 0.1.9 one). 0.1.2+ also sends `player` (DCS name, "" for the single-player
placeholder) and `seat` (1 = first seat, 2 = slot `"<unit>_2"`...). Auto-connect messages are only accepted from the
DCS server (player id 0/1) so other players cannot redirect you.

## Client ⇄ Server (JSON, snake_case)

| Type | Direction | Fields |
|---|---|---|
| `state` | C → S, every 2 s and on change | `client_id, name, version, server, group, unit, room, aircraft, standby, mod_version, profile_hash, protocol, args_format` |
| `welcome` | S → C, answer to `state` | `version, session_key, peers, peer_names, incompatible[...]`; level 2+: `protocol, args_format, features[], crew[{name, mod_version, profile_hash, ucs_version, args_format}]` |
| `args` | C → S → C | `client_id, aircraft, seq, values{"arg":value}`, optional `target` |
| `flight` | C → S → C | level 3+ (server feature `flight_mirror`): same fields as `args`, flight-control positions; max 90/s per client |
| `peer_joined` | S → oldest compatible crew member | `client_id, name` → that client sends a targeted `args` snapshot |
| `leave` | C → S | `client_id` |
| `error` | S → C | `message` |

`SessionKey = "{server}|{group}|{unit}"` (or `"{server}|{group}"` with `"SessionMatch": "Group"`,
or `"ROOM|{room}"` with a manual room). Packets are relayed only between clients with the same
key **and** the same aircraft, the same `mod_version` and `profile_hash`, not in standby, seen in the last 15 s.
Crew members in the same cockpit with a different version or profile are reported in `incompatible`.

## Versioning and backward compatibility

- Messages are JSON; unknown fields are ignored, so new fields never break older peers.
- `protocol` (level): 1 = 0.1.0-alpha (field absent), 2 = 0.1.1-alpha.
- `args_format`: format of `args.values`. Only clients with the same format are paired (absent = 1).
- `features` (welcome, level 2+): optional server features. A server that sends no list is treated as
  `["mod_check"]` (0.1.0-alpha). The client logs every known feature the server lacks:

| Feature | Since server | Effect when missing |
|---|---|---|
| `mod_check` | 0.1.0-alpha | no mod version / profile compatibility check |
| `ucs_version_check` | 0.1.1-alpha | no warning when crew members run different UCS versions; args-format pairing check |

Rule: a client must keep working with every older server; it may only lose optional features.
