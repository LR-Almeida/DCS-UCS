> ⚠️ **Alpha release — for testing only.** Not yet validated in DCS on real multicrew flights. Expect bugs; please report them (see CONTRIBUTING.md for what to include).

## Downloads

| File | What it is |
|---|---|
| `DCS-UCS-Setup-*.exe` | Installer (client, server, profile generator, DCS scripts) |
| `DCS-UCS-portable-*.zip` | Same files without installer (manual setup) |
| `UCS-Client.exe` / `UCS-Server.exe` / `UCS-ProfileGen.exe` | Standalone executables |

Windows 10/11 x64. No .NET install needed (self-contained). The executables are not code-signed yet, so
Windows SmartScreen may warn on first run.

## Highlights

- Cockpit sync for community mods, relayed by a self-hosted `UCS-Server.exe` (UDP 5055).
- Automatic pairing by DCS server / group / unit, or a manual room.
- SRS-style Auto-Connect through an optional DCS server hook.
- Profiles are **generated locally** from your installed mod (no profiles are distributed) and regenerated when the mod updates.
- Crew members only sync when they have the **same mod version and profile**; otherwise the client shows a compatibility error.
- `Export.lua` is never replaced: one protected line is appended, other tools keep working.

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Ativo: <aircraft>`.
3. Repeat on a second PC with the same room and the server's IP to test real sync.
