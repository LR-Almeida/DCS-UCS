# DCS-UCS — Universal Control Sync

[![Build](https://github.com/LR-Almeida/DCS-UCS/actions/workflows/build.yml/badge.svg)](https://github.com/LR-Almeida/DCS-UCS/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/LR-Almeida/DCS-UCS)](https://github.com/LR-Almeida/DCS-UCS/releases)

**Free, open-source and decentralized multicrew cockpit synchronization for DCS World community mods.**

Many great community aircraft (helicopters, transports, trainers…) have no native multicrew.
DCS-UCS mirrors cockpit switches, buttons and knobs between crew members — in the spirit of
SRS and YourControls. There is no central service: every server admin or virtual squadron
hosts its own lightweight `UCS-Server.exe`.

---

## Features

- **Decentralized** — anyone can run `UCS-Server.exe` (portable, single file, no install required).
- **Automatic pairing** — players are grouped by `DCS server | Group | Unit`, read straight from DCS. No room codes needed.
- **SRS-style Auto-Connect** — an optional DCS server hook tells every joining player which UCS server to use.
- **Safe with official modules** — automatic *Standby* when you are in the menu, in an ED module or in any aircraft without a profile, so it never interferes with ED's native multicrew.
- **Loop prevention** — remote commands lock the argument for 0.3 s, so switches never bounce back and forth.
- **Late join sync** — a crew member joining a running cockpit receives its full state.
- **Profile generator** — `UCS-ProfileGen.exe` reads a mod's `clickabledata.lua` / `devices.lua` and builds the profile for you.
- **Plays nice with other mods** — `Export.lua` callbacks are chained; SRS, Tacview, DCS-BIOS keep working.

## Components

| Component | Where it runs | What it does |
|---|---|---|
| `UCS-Client.exe` | Every crew member | GUI; links DCS to the UCS server and loads aircraft profiles |
| `UCS-Server.exe` | Anyone (squadron / server admin) | UDP relay on port `5055` |
| `UCS-ProfileGen.exe` | Profile authors | Generates `profiles/<aircraft>.json` from a mod folder |
| `UCS-Hook.lua` | `Saved Games\DCS\Scripts\Hooks\` | Slot detection + auto-connect interception (GameGUI) |
| `UCS-Export.lua` | `Saved Games\DCS\Scripts\UCS\` | Reads/writes cockpit arguments at 20 Hz |
| `UCS-ServerHook.lua` *(optional)* | DCS server `Scripts\Hooks\` | Sends the UCS server address to joining players |

```
Pilot PC                                  UCS host                         Copilot PC
DCS ─ UCS-Export.lua ┐                                                ┌ UCS-Export.lua ─ DCS
    ─ UCS-Hook.lua  ─┴─ UCS-Client.exe ⇄ UCS-Server.exe :5055 ⇄ UCS-Client.exe ─┴─ UCS-Hook.lua
```

See [docs/PROTOCOL.md](docs/PROTOCOL.md) for the wire protocol.

---

## Installing the client (players)

1. Download `DCS-UCS-Setup-x.y.z.exe` from the [Releases page](https://github.com/LR-Almeida/DCS-UCS/releases).
2. Run it and keep **"Configure DCS World client"** checked. The installer:
   - copies `UCS-Hook.lua` to `Saved Games\DCS\Scripts\Hooks\` (and `DCS.openbeta` if present);
   - copies `UCS-Export.lua` to `Saved Games\DCS\Scripts\UCS\`;
   - appends one protected line to your `Export.lua` (a backup `Export.lua.ucs-backup` is created):
     ```lua
     pcall(function() local ucsLfs=require('lfs'); dofile(ucsLfs.writedir()..[[Scripts\UCS\UCS-Export.lua]]); end) -- DCS-UCS
     ```
3. Start **UCS Client**, enter the UCS server address (or let Auto-Connect do it) and your pilot name.
4. Start DCS, join the same server and take the **same unit** as your crew mate (or units of the same group — see `SessionMatch`).

The status panel shows:

- **DCS** — `Standby` or `Ativo: UH-60L`
- **Sessão** — `Server | Group | Unit` (or manual room)
- **Conexão** — `Conectado` / `Desconectado`
- **Tripulação** — the other crew members in your cockpit

**Manual install (portable zip):** copy the Lua files as above and add the `Export.lua` line yourself.

**Manual room override:** fill *Override Manual de Sala* with any shared word (e.g. `rescue-42`) on both
clients to bypass automatic pairing (useful in single player tests or mods that put each crew member in a
different unit).

Settings are stored in `client_config.json` (next to the exe, or `%APPDATA%\DCS-UCS\` when installed under Program Files).

---

## Hosting a UCS server

1. Run `UCS-Server.exe` (from the installer or the portable zip). On first run it creates `server_config.json`:
   ```json
   {
     "BindAddress": "0.0.0.0",
     "Port": 5055,
     "TimeoutSeconds": 15,
     "SessionMatch": "Unit",
     "MaxClients": 256,
     "MaxPacketsPerSecond": 200,
     "StatusIntervalSeconds": 60,
     "Verbose": false
   }
   ```
   - `SessionMatch: "Unit"` → key `{DcsServer}|{Group}|{Unit}` (crew sharing one multicrew unit).
   - `SessionMatch: "Group"` → key `{DcsServer}|{Group}` (each crew member in their own unit of the same group).
   - Command line: `UCS-Server.exe --port 5055 --config path\to\server_config.json`
2. Open **UDP 5055** in the Windows Firewall (the installer has an option for it) and forward it on your router
   if players connect over the internet.
3. Give players your public IP / DDNS name — or set up Auto-Connect below.

The server only relays packets between clients with the **same SessionKey and the same aircraft**, ignores
clients in Standby and drops clients that send no heartbeat for 15 s.

### Auto-Connect (SRS style)

On the machine that hosts the **DCS server**:

1. Copy `Server-Files\UCS-ServerHook.lua` and `UCS-AutoConnect.cfg` to
   `Saved Games\DCS.server\Scripts\Hooks\` (dedicated server) or `Saved Games\DCS\Scripts\Hooks\` (hosting from the game).
   The installer option *"Install UCS-ServerHook.lua"* does this and asks for the address.
2. Edit `UCS-AutoConnect.cfg` with the address of your UCS server:
   ```
   192.168.1.50:5055
   ```
   or a DDNS name (`myserver.ddns.net`, port 5055 assumed). Leave it empty to auto-detect the public IP
   (falls back to the local IP) on port 5055.
3. Restart the DCS server. Every player that connects receives a silent private chat message
   `UCS_AUTOCONNECT:<address>`; their `UCS-Hook.lua` forwards it to `UCS-Client.exe`, which disconnects from any
   previous session and connects automatically (if *Aceitar Auto-Connect do Servidor DCS* is checked).

> Only messages sent by the DCS server itself are accepted, so other players cannot redirect your client.
> Hiding the message from the chat window depends on the DCS build; on builds that do not allow hooks to
> suppress chat, it may appear briefly in the chat log.

---

## Profiles

A profile maps each cockpit argument to the clickable command that reproduces it:

```json
{
  "aircraft_name": "UH-60L",
  "args": {
    "17": { "device_id": 1, "command_id": 3001, "description": "Battery Switch", "type": "toggle" }
  }
}
```

`aircraft_name` and the file name must match the aircraft type DCS reports (`LoGetSelfData().Name`).
Profiles are searched in `%APPDATA%\DCS-UCS\profiles\`, then `profiles\` next to `UCS-Client.exe`.

> ⚠️ `profiles/UH-60L.json` in this repository is an **illustrative example**. Argument, device and command
> ids differ between mods and mod versions — regenerate it from your installed mod before flying.

### Generating a profile for a new mod

```bat
UCS-ProfileGen.exe "C:\Users\<you>\Saved Games\DCS\Mods\aircraft\UH-60L" "UH-60L"
```

The tool:

1. finds `Cockpit\Scripts\clickabledata.lua` and `devices.lua` inside the mod;
2. executes them in a **sandboxed Lua interpreter** (MoonSharp, no file/OS access) emulating the DCS cockpit
   environment — so `devices.lua`, `command_defs.lua` and `counter()` resolve to real numbers. When your DCS
   install is detected (registry or `--dcs`), ED's common `clickable_defs.lua` is used too;
3. falls back to a text parser if the scripts cannot be executed;
4. filters continuous flight controls (cyclic, collective, pedals, throttles, brakes…);
5. writes `profiles\<aircraft>.json`.

Options: `--out <dir|file.json>`, `--dcs <DCS folder>`, `--no-axes`, `--keep-flight-axes`, `--text-parser`, `--verbose`.

Please share working profiles through a pull request!

---

## Building from source

Requirements: Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or newer, optionally
[Inno Setup 7](https://jrsoftware.org/isinfo.php) for the installer (found via `PATH`, the `ISCC` variable or its
registry entry, so any install folder works).

```bat
git clone https://github.com/LR-Almeida/DCS-UCS.git
cd DCS-UCS
build.bat
```

Single-file, self-contained executables are written to `publish\`; the installer (if Inno Setup is found)
to `installer\Output\`. GitHub Actions builds everything on each push to `main` and publishes a release for
every `v*` tag.

```
DCS-UCS/
├── src/
│   ├── Lua/                UCS-Hook.lua, UCS-Export.lua, UCS-ServerHook.lua
│   ├── UCS.Common/         protocol, profile model, paths
│   ├── UCS.Server/         UCS-Server.exe
│   ├── UCS.Client/         UCS-Client.exe (WinForms)
│   └── UCS.ProfileGen/     UCS-ProfileGen.exe
├── profiles/               aircraft profiles
├── Server-Files/           UCS-AutoConnect.cfg template
├── installer/              UCS-Installer.iss
├── docs/PROTOCOL.md
└── build.bat
```

## Troubleshooting

| Symptom | Check |
|---|---|
| DCS stays `Standby (DCS não detectado)` | `Export.lua` line present? Look for `UCS` in `Saved Games\DCS\Logs\dcs.log` |
| `Sem perfil para 'X'` | Generate a profile named exactly `X.json` |
| `Conectando...` forever | Server running? UDP 5055 open in firewall/router? |
| Crew not paired | Same DCS server, same unit (or `SessionMatch: Group`, or the same manual room) |
| Switch moves but systems don't react | Regenerate the profile; some mods need different command ids |
| Server blocks `LoGetSelfData` (export restrictions) | UCS uses the aircraft type reported by the GameGUI hook instead |

## License

[MIT](LICENSE) — free for any use. DCS World is a trademark of Eagle Dynamics SA; this project is not
affiliated with or endorsed by Eagle Dynamics.
