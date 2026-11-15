> ⚠️ **Alpha release — for testing only.** Not yet validated in DCS on real multicrew flights. Expect bugs; please report them (see CONTRIBUTING.md for what to include).

## Downloads

| File | What it is |
|---|---|
| `DCS-UCS-Setup-*.exe` | Installer (client, server, profile generator, DCS scripts) |
| `DCS-UCS-portable-*.zip` | Same files without installer (manual setup) |
| `UCS-Client.exe` / `UCS-Server.exe` / `UCS-ProfileGen.exe` | Standalone executables |
| `SHA256SUMS.txt` | SHA-256 checksums (used by the in-app updater) |

Windows 10/11 x64. No .NET install needed (self-contained). The executables are not code-signed yet, so
Windows SmartScreen may warn on first run.

## What's new in 0.1.2-alpha

- **Flight controls mirror (experimental):** the stick, pedals and collective animations are found
  automatically in each mod and sent to the crew. In the seat that is *not* flying, the new option
  *Move my cockpit controls with the pilot's inputs* feeds them in as joystick input so your stick
  moves like the pilot's (off at every start, pauses an axis when you move your own control).
  It never runs in the first seat (where the injected input really flies the aircraft) and finds by
  itself which input each mod reads for every axis (OH-6A: pitch inverted, collective on the thrust axis).
- **All UCS logs in `Saved Games\DCS\Logs\UCS`**: the client log is now saved automatically
  (`UCS-Client.log`, previous session `UCS-Client.old.log`).
- Tested in DCS (single player and a private multiplayer server, OH-6A): switch sync both ways,
  late-join state, flight-control mirror and calibration, seat 1 lock. Still to be tested with two
  players: the mirror in seat 2 (see `docs/PENDING-TESTS.md`).
- **Diagnostic recording:** per-frame CSV in `Saved Games\DCS\Logs\UCS\UCS-diag-*.csv` and a log of
  cockpit animations that flicker between two positions.
- **Fix:** switches that a mod implements as toggles (e.g. UH-60L doors) could end up inverted when a
  crew member joined; UCS now skips commands for switches already in the requested position.
- Profiles made by 0.1.0/0.1.1 are regenerated once to add the flight controls (same fingerprint,
  so crew members still on 0.1.1 keep syncing switches).
- `SHA256SUMS.txt` uses LF line endings (`sha256sum -c` works).
- First release that 0.1.1-alpha can install through the in-app updater.

Flight controls need UCS-Server 0.1.2-alpha; older servers keep syncing switches.

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
