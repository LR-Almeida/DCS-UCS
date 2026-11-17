# Pending tests

Items that can only be validated with a real release or in DCS. Check them off (and note the
result) when tested.

## In-app updater (validated 0.1.1 -> 0.1.8, 2026-11-16/17)

Old downloaded installers are deleted on the next download (0.1.8: only the new one left).


The updater needs a newer release to update **to**, so the first real test happens when the release
*after* the one that introduced it is published (e.g. installed 0.1.1-alpha → update to 0.1.2-alpha).

- [x] Automatic check at startup finds the new version and shows the pop-up (0.1.1 -> 0.1.2, 2026-11-16)
- [x] **Update now** (DCS closed): download, SHA-256 OK, UAC prompt, silent install, client reopens (not elevated)
- [x] **Close DCS and update** (0.1.2 -> 0.1.3, 2026-11-16): DCS comes to the front showing its exit confirmation; after confirming,
      DCS closes normally (never killed) and the update installs
- [ ] **Close DCS and update**, exit NOT confirmed within 2 minutes: switches to "update when DCS closes"
- [ ] Same in the main menu and inside a mission (confirmation box may differ)
- [x] **Update when DCS closes** (0.1.3 -> 0.1.4, 2026-11-16): waits while DCS runs, installs right after DCS exits
- [x] **Cancel scheduled update** stops the wait (0.1.6, 2026-11-17: DCS closed afterwards, nothing installed)
- [x] **Skip this version** (0.1.6, 2026-11-17): no pop-up on the next start; *Check now* still shows it (to confirm)
- [x] **Remind me later** (0.1.6, 2026-11-17): pop-up returned at the next start of the client
- [x] UAC denied (0.1.5 -> 0.1.6, 2026-11-17): FAILED - the client closed and nothing was installed (Inno asks
      for elevation after it starts). Fixed for the next release: the client waits for Setup's log before
      closing; denial -> "Update cancelled", client keeps running (tested with fake installers)
- [x] UAC denied (0.1.7 -> 0.1.8, 2026-11-17): "Update cancelled", client kept running; then updated normally
- [x] Silent update keeps previous installer choices (DCS scripts re-copied to Saved Games, server hook,
      firewall rule not duplicated) and `Export.lua` still has exactly one UCS line
- [ ] After updating with DCS open: client warns that DCS runs older scripts until restarted
- [x] `SHA256SUMS.txt` present in every release and verified by the updater ("SHA-256 OK"); tampered file rejection not tested in the field
- [x] UCS-Server prints the "new version available" notice (0.1.7 server, also in UCS-Server.log)
- [x] Silent update to 0.1.3 writes install-options.ini with the new network options on (defaults)

## Backward compatibility (new client ↔ older server)

- [ ] Client 0.1.1+ with UCS-Server 0.1.0-alpha: sync works; log lists the disabled features
- [ ] Client 0.1.0-alpha with UCS-Server 0.1.1+: sync works (old client treated as args format 1)
- [ ] Crew members on different UCS versions: note in the log, sync still works

## Flight controls (0.1.2-alpha, in DCS)

Turn on **Diagnostic recording** on both PCs, fly a few minutes, then collect
the whole `Saved Games\DCS\Logs\UCS` folder (diagnostic CSVs + `UCS-Client.log`) from both seats into `test-logs\`.

Automated single-player run on 2026-11-15 (DCS driven through the Roll for Ejection studio hook, a fake crew
member on the UCS server; logs in `test-logs\2026-11-15-oh6a-sp-automatizado`):

- [x] UCS 0.1.2 hooks load; Export activates on the OH-6A; profile generated (61 controls, 7 flight animations)
- [x] Diagnostic CSV written to `Logs\UCS` (~45 fps); no flickering args in single player (one source only)
- [x] Injection moves the OH-6A cockpit stick, pedals and collective exactly to the received positions
- [x] Auto calibration: pitch 2001 inverted, roll 2002, rudder 2003, collective 2004 inverted (2087 ignored by the mod)
- [x] In the seat WITH the controls the injected input flies the aircraft (lifted off in single player) -> seat 1 lock
- [x] Remote switch command applied (intercom switch); repeated command does not invert it (toggle guard)
- [x] Local switch click reaches the crew; late joiner receives the full cockpit state (61 args)
- [x] Nothing injected after the stream stops

Automated private multiplayer run on 2026-11-15 (DCS as host of a password-protected, non-public server; fake
copilot on the UCS server through the LAN address; logs in `test-logs\2026-11-15-oh6a-mp-privado-automatizado`):

- [x] Pairing by DCS server / group / unit (no manual room); late joiner receives the full state (61 args)
- [x] Export not restricted on the host: `LoGetSelfData`, `get_argument_value`, `LoSetCommand` all work
- [x] Switch from the crew applied in the host cockpit; host stick input reaches the crew (`flight` messages)
- [x] Seat 1 lock: crew stick positions are NOT injected in the first seat
- [x] Leaving the slot: Standby, crew list cleared
- [x] DCS lists the second seat as slot `<unit>_2` (role "Instructor"); it cannot be taken without a pilot in seat 1
- Seen: on activation the client logs "Profile loaded" twice and sends STANDBY + profile again (harmless flap)

First two-player test, 2026-11-16 (OH-6A, DCS server on the internet, UCS-Server through the router;
logs in `test-logs\2026-11-16-Quik`):

- [x] UCS-Server reachable from the internet (UDP 5055 forwarded); remote client connected
- [x] `master` flag follows the C-key handover on both PCs; the mirror switches on/off by itself
- [x] Hook reports the seat ("Multicrew seat: 2" / "1")
- [x] Native flicker measured without UCS injecting: OH-6A cyclic args 214/215 bounce in seat 2
- [ ] FAILED: the crew member in seat 2 was never paired (no "Joined the cockpit", switches not synced).
      Cause: DCS.getPlayerUnit() returns "<unit>_2" in seat 2, the unit name lookup failed and the session
      key was empty. Fixed in 0.1.4 (strip suffix + unit id fallback) -> repeat the whole two-player list.

Second two-player test, 2026-11-17 (0.1.4, logs in `test-logs6-11-17-Quik`):

- [x] Crew member in seat 2 paired ("Joined the cockpit"), handovers with C both ways, axes mirrored
      (pitch within 0.017, one frame behind)
- [x] Flicker in the copilot seat only while UCS had no value / was paused (1 vs up to 9 flips per 1000
      frames) -> 0.1.5: full state every 0.4 s, no pause while DCS reports the controls
- [x] Calibration failed on roll/yaw with the pilot moving the stick -> 0.1.5: robust calibration
- [x] Collective after handover = the new pilot's physical collective (expected, accepted by the user)
- [x] Trim not shared (lives in the mod's FM) -> 0.1.5: trim hand-over; reset/release/set verified in single
      player on the OH-6A (trim captured exactly at the mirrored position, pedals included)
- [ ] 0.1.5 in two-player flight: trim hand-over feels seamless both ways; no flicker in the copilot seat;
      UH-60L trim hand-over (trimSet semantics unverified); arg 53 (ARC-54 tuning knob) flicker seen on the
      copilot PC

Third two-player test, 2026-11-17 (0.1.5 with Pedro, OH-6A / UH-60L / AH-6J / MH-6J, logs in
`test-logs6-11-17-Pedro`):

- [x] Trim hand-over in real handovers (OH-6A, AH-6J, MH-6J, both seats): trim captured at the pilot's position
- [x] Profiles regenerated with trim commands; MH-6J profile generated on first entry (same mod as AH-6J)
- [ ] FIXED in 0.1.6: spurious trim hand-over when entering another aircraft while not having the controls
- [ ] FIXED in 0.1.6: calibration and diagnostic columns carried from the OH-6A into the UH-60L (UH-60 mirror wrong)
- [ ] FIXED in 0.1.6: ~1.5 s roll/yaw wobble after the hand-over (the mod fades the new trim in)
- [ ] FIXED in 0.1.6: hand-over skipped when an axis near centre was not calibrated yet
- [ ] FIXED in 0.1.6: throttle not mirrored (OH-6A Keys.throttleAxis verified in game: moves args 4 and 211)
- [ ] 0.1.6 in flight: UH-60L calibration from scratch (collective 2087 from the mod bindings) and its
      trim set/reset hand-over; AH-6J arg 111 (Argus ENR button) flicker - button pressed or echo?

External states, automated in game 2026-11-17 (OH-6A single player + fake crew member on a test UCS-Server,
logs in `test-logs6-11-17-estado-externo-automatizado`):

- [x] Crew opens/closes the door -> this OH-6A follows (Keys.ToggleDoors, args 38/35) in < 7 s
- [x] Crew turns the landing light on -> follows (Keys.LandingLight, arg 115), first try
- [x] No cockpit switch moved (first version pressed acol_light_on, a switch shortcut -> now excluded and
      any switch moved by a try is restored)
- [x] A change made in this seat is sent to the crew and not undone by the crew's older state
- [ ] Two players: doors / landing light / searchlight both ways; late joiner gets the external states;
      UH-60L (cargo doors 401/402 from 0.1.7) and AH-6J/MH-6J
- [ ] Sling load / cargo hook in multicrew (excluded from UCS on purpose): does DCS show the same hooked cargo
      to both seats, and who can hook / release?

Still needs two players:

- [ ] Profile regenerated automatically ("N flight-control animations" in the log; OH-6A 7, UH-60L 4)
- [ ] Baseline (mirror OFF): what the other seat's stick does natively (frozen / own joystick / flicker)
- [ ] Log tab lists the flickering args reported by the diagnostic (UH-60L doors, cyclic)
- [ ] UH-60L doors stop flickering once both seats run UCS (doors are clickable and synced)
- [ ] Mirror ON in the seat NOT flying: stick / pedals / collective follow the pilot, no flicker
- [ ] Mirror in seat 2 without the controls does NOT fly the aircraft (critical)
- [x] OH-6A as multiplayer host, seat 1 (2026-11-16): `LoGetHelicopterFMData().master` and `LoGetFMData().master`
      = true, `LoGetPlayerSeat()` = 0, ownship export allowed; client logs "You have the controls", mirror inactive
- [ ] `master` flag works for the mod: log "A crew member has the controls" in seat 2 and "You have the
      controls" in seat 1 (otherwise "DCS does not report who has the controls")
- [ ] Control handover with C: mirror stops on the seat that takes the controls, starts on the other one,
      both ways; the aircraft does not jump when the controls change hands
- [ ] Hook reports seat 2 for the copilot ("Multicrew seat: 2" in the log)
- [ ] Moving the local stick with the mirror ON pauses that axis ("paused 3 s" in the log)
- [ ] UH-60L calibration (which commands its EFM reads; log lines "uses command ...")
- [ ] Latency over the internet acceptable (CSV: remote vs local columns)
- [ ] Server 0.1.1 + client 0.1.2: log says flight controls disabled, switches still sync

## In DCS (alpha test campaign)

- [ ] OH-6A, single player: profile generated automatically, `Active: OH-6A`, "sent" counter increases
- [ ] OH-6A, multiplayer, same unit (pilot + instructor seats): automatic pairing
- [ ] OH-6A baseline without UCS: which switches DCS native multicrew already syncs
- [ ] OH-6A with UCS: toggles, buttons, multi-position selectors, knobs (note failures)
- [ ] Player name detected from DCS in multiplayer
- [ ] Auto-Connect via UCS-ServerHook.lua (and whether the chat line is hidden)
- [ ] Installer on a PC with an existing `Export.lua` (SRS, Tacview...): other tools still work
