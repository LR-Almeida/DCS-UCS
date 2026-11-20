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

## What's new in 0.1.9-alpha

- **Fix: crews that include the player hosting the DCS server now pair.** The host's UCS identified the DCS
  server differently from the players who joined it (`MP:<name>/<mission>` vs `ip:port`), so they were never
  put in the same cockpit. Every player now uses the DCS server name + mission name, which also pairs players
  who joined the same server through different addresses (public IP, LAN, Radmin/Tailscale).
- **⚠️ Everyone in the crew must update to 0.1.9:** a 0.1.8 player does not pair with a 0.1.9 one in multiplayer.
  Until then, the same word in *Manual room override* on every client works on any version.
- **Favorites tab** in the client: save UCS servers and connect with one click (add current, add, edit, remove).
- New icons for UCS-Server (network badge) and UCS-ProfileGen (profile sheet badge).

## What's new in 0.1.8-alpha

- **Application icon** for UCS-Client, UCS-Server, UCS-ProfileGen and the installer (sync arrows around a rotor).
- **General tab:** group boxes use the whole width (same margin on both sides); rows wrap only when they do not
  fit; the pilot name field shrinks so "Detect from DCS" stays beside it.
- No functional change to the sync: crew members on 0.1.7 and 0.1.8 work together.

## What's new in 0.1.7-alpha

- **UH-60L cargo doors** are now synced as external states (they were excluded together with sling-load
  commands). Sling load / cargo hook stays excluded on purpose.
- **Updater:** refusing the Windows permission prompt no longer closes the client without updating: it now
  says "Update cancelled" and keeps running. Old downloaded installers are deleted.
- **General tab:** no horizontal scroll bar; texts, check boxes and fields wrap / shrink to the window width.
- Log lines for "Remind me later" and for a cancelled scheduled update.

**Restart DCS after updating.** Profiles are regenerated once.

## What's new in 0.1.6-alpha

- **External states:** doors, landing light, searchlight and other things a mod changes only from the
  keyboard follow the crew member who changed them (found automatically in each mod's scripts).
- **Throttle mirrored**; every axis uses the input commands the mod itself binds.
- **Smooth trim hand-over**; per-aircraft state reset (no spurious hand-over, no calibration reused in
  another aircraft).
- UCS-Server writes `UCS-Server.log`.

### Since 0.1.4
- Trim goes with the controls; no stick flicker in the copilot seat; robust calibration (0.1.5).

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
