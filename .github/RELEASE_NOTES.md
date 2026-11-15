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

## What's new in 0.1.1-alpha

- **Client in English** with tabs: General (scrollable), Log (copy / save to file), Update, Help, About.
- **Pilot name detected automatically** from DCS (multiplayer nickname).
- **In-app updates:** automatic check (on by default), *Update now*, *Close DCS and update* (confirm the exit in
  DCS), *Update when DCS closes*, *Skip this version*. Downloads are verified with SHA-256.
  Users of 0.1.0-alpha must install this version manually once.
- **Backward compatibility:** new clients work with older UCS servers; unsupported features are disabled and
  listed in the log. Crew members on different UCS versions get a note.
- **UCS-Server shows the addresses to share:** public IP (like games do) and local/VPN addresses (Radmin,
  Hamachi, ZeroTier, Tailscale), plus a console notice when a new server version exists.
- Warning when DCS is still running older UCS scripts (restart DCS after updating).
- Privacy section in the README listing every network connection and how to disable it.

## Quick test

1. Install, start **UCS Server** and **UCS Client** (`127.0.0.1`, manual room `test`).
2. Fly a mod aircraft in single player: the client should show `Active: <aircraft>`.
3. Repeat on a second PC with the same room and the server's address to test real sync.
