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

## What's new in 0.1.4-alpha

- **Fix: crew members in the second seat were never paired.** In a multicrew seat other than the first
  one, DCS reports the slot as `<unit>_2`; the hook could not find the unit, so that client never joined
  the cockpit session (no switch sync, no flight mirror). Found in the first two-player OH-6A test.
  The hook now strips the seat suffix and, if a unit name still cannot be read, uses the unit id
  (shared by every seat of the aircraft).

**Restart DCS after updating** (the fix is in the DCS hook, loaded when DCS starts). Both crew members
need 0.1.4.

### Since 0.1.2
- Automatic flight mirror: while your seat does not have the controls, your stick, pedals and collective
  follow the crew member flying; it stops on the frame you take the controls (key C).
- Privacy policy and network options in the installer.

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
