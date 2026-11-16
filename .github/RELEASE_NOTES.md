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

## What's new in 0.1.3-alpha

- **Automatic flight mirror:** while DCS says your seat does **not** have the controls, your stick, pedals
  and collective follow the crew member flying. It stops on the same frame you take the controls (key C,
  *Request Aircraft Control*) and resumes when you hand them back. On by default (option in the General
  tab); the client shows who has the controls. Uses the DCS multicrew flag `LoGetFMData().master`
  (verified on the OH-6A); without it, only outside the first seat.
- **Privacy and network options in the installer:** the privacy policy is shown during installation, and
  the update check and the public IP lookup can be turned off there (`PRIVACY.md`).
- The DCS server hook no longer looks up the public IP in single player; `DetectPublicIp=false` option.
- Same product name / version metadata in every file; code signing prepared (not active yet).
- Diagnostic CSV records who has the controls (`master`, `seat`).

Update from 0.1.1/0.1.2 with the in-app updater. **Restart DCS** after updating (the DCS scripts changed).
Flight controls need UCS-Server 0.1.2-alpha or newer.

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
