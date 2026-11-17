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

## What's new in 0.1.5-alpha

- **Trim hand-over:** the trim now goes with the controls. When you lose the controls your trim is reset,
  so your stick shows exactly the pilot's; when you take them, your trim is set to the position the
  aircraft was being flown with, so a spring joystick at centre continues without a jump (pedals too
  when the mod's pedal trim is on). The trim commands are found automatically in each mod
  (OH-6A, AH-6J: trim release/reset; UH-60L: trim set/reset, still to be verified). Not used with
  force-feedback / springless sticks (mod option).
- **No more stick flicker in the copilot seat:** the pilot re-sends the full stick / pedals / collective
  position every 0.4 s, so the other seat always has a value to show, and the mirror no longer pauses
  while DCS reports who has the controls (the pause let the native multicrew flicker through).
- **Robust axis calibration:** works while the pilot is moving the stick, switches straight to the
  inverted command when the animation moves the wrong way, and retries instead of giving up.
- Collective keeps the last mirrored position after a handover until you move your own.
- Profiles are regenerated once (they now list the trim commands).

**Restart DCS after updating.** Both crew members need 0.1.5.

### Since 0.1.3
- Crew members in the second seat are paired (0.1.4).
- Automatic flight mirror while you do not have the controls (0.1.3).

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
