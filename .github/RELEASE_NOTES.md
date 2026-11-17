> ⚠️ **Alpha release — for testing only.** Not yet validated in DCS on real multicrew flights. Expect bugs; please report them (see CONTRIBUTING.md for what to include).

[Code signing policy](https://github.com/LR-Almeida/DCS-UCS#code-signing-policy) ·
[Privacy policy](https://github.com/LR-Almeida/DCS-UCS/blob/main/PRIVACY.md)

## Downloads

| File | What it is |
|---|---|
| `DCS-UCS-Setup-*.exe` | Installer (client, server, profile generator, DCS scripts) |
| `DCS-UCS-portable-*.zip` | Same files without installer (manual setup) |
| `UCS-Client.exe` / `UCS-Server.exe` / `UCS-ProfileGen.exe` | Standalone executables |
| `SHA256SUMS.txt` | SHA-256 checksums (used by the in-app updater) |

Windows 10/11 x64. No .NET install needed (self-contained). The executables are not code-signed yet, so
Windows SmartScreen may warn on first run.

## What's new in 0.1.6-alpha

- **External states:** doors, landing light, searchlight and other things a mod changes only from the
  keyboard now follow the crew member who changed them. Found automatically in each mod's scripts (OH-6A,
  AH-6J/MH-6J, UH-60L...): UCS presses the mod's own key command on the other seat. Verified in game on the
  OH-6A (doors, landing light). Weapons, engine, fuel and cargo are never touched.
- **Throttle mirrored**, and every axis now uses the input commands the mod itself binds (UH-60L collective,
  OH-6A/AH-6J throttle...): no more guessing per aircraft.
- **Smooth trim hand-over:** the stick no longer wobbles while the mod fades the new trim in.
- **Fixes from the 2026-11-17 flights:** a trim hand-over happened when entering another aircraft without
  the controls; the calibration of one aircraft was reused in the next one (UH-60L mirror wrong); the
  hand-over was skipped when an axis near centre was not calibrated yet; diagnostic columns of the wrong
  aircraft.
- UCS-Server writes `UCS-Server.log` (Saved Games\DCS\Logs\UCS); build uses current GitHub Actions.

**Restart DCS after updating.** Both crew members and the UCS-Server need 0.1.6 for the external states.
Profiles are regenerated once.

### Since 0.1.4
- Trim goes with the controls; no stick flicker in the copilot seat; robust calibration (0.1.5).

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
