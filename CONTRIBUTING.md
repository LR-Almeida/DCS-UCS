# Contributing to DCS-UCS

Thanks for helping! Bug reports, test results and code contributions are all welcome.

## Aircraft profile policy (please read)

DCS-UCS **does not publish or distribute aircraft profiles**, for any mod.

- Profiles are generated **locally** on each user's machine, from the mod the user installed
  (automatically by `UCS-Client.exe`, or manually with `UCS-ProfileGen.exe`).
- Generated profiles contain only numeric ids (argument / device / command) and **neutral labels**
  such as `toggle 17`. No text, code or files from a mod are copied into them.
- Generated profiles **must not be committed** to this repository. `profiles/*.json` is ignored by git,
  and pull requests that add profile files, mod files, or text extracted from mods will be closed.
- Do not share generated profiles publicly. Each crew member generates their own; the UCS server checks
  that everyone has the same mod version and the same profile fingerprint before syncing.
- Official Eagle Dynamics modules are never targeted: UCS stays in Standby on them and only reads mods
  from `Saved Games\DCS*\Mods\aircraft`.

If a profile needs a fix (wrong command for a switch, for example), open an issue describing the
**generator** problem (mod name, mod version, argument id, what happens). The fix goes into
`UCS-ProfileGen` so everyone's local generation improves — not into a shared profile file.

### Mod authors

DCS-UCS is not affiliated with any mod team. If you are a mod author and want something changed —
including asking that UCS ignore your mod — open an issue or contact the maintainers; such requests are
honoured without discussion. You can also tell us about an official way to read your cockpit
(e.g. a documented command table) and we will gladly use it.

## Reporting a bug

Please include:

1. UCS version (shown in the client window title) and DCS version.
2. Aircraft and mod version (shown in the client *Perfil* line).
3. The `UCS` / `[DCS-UCS]` lines from `Saved Games\DCS\Logs\dcs.log`.
4. The client log (copy from the client window) and, if relevant, the `UCS-Server.exe` console output.
5. Which controls fail and how (not moving, moving the wrong way, bouncing...).

Do not attach generated profiles or mod files to public issues.

## Code contributions

- Requirements: .NET 10 SDK, optionally Inno Setup 7. Build with `build.bat`.
- Keep the Lua scripts compatible with Lua 5.1 and protect every DCS API call with `pcall`.
- `Export.lua` is shared with other tools: never replace it, only append and chain callbacks.
- Keep the wire protocol backward compatible when possible and document changes in `docs/PROTOCOL.md`.
- Open pull requests against `main`. CI must pass.

By contributing you agree that your contributions are licensed under the MIT License.
