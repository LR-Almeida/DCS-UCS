# Pending tests

Items that can only be validated with a real release or in DCS. Check them off (and note the
result) when tested.

## Next release (first release with the in-app updater)

The updater needs a newer release to update **to**, so the first real test happens when the release
*after* the one that introduced it is published (e.g. installed 0.1.1-alpha → update to 0.1.2-alpha).

- [ ] Automatic check at startup finds the new version and shows the pop-up
- [ ] **Update now** (DCS closed): download, SHA-256 OK, UAC prompt, silent install, client reopens (not elevated)
- [ ] **Close DCS and update**: DCS comes to the front showing its exit confirmation; after confirming,
      DCS closes normally (never killed) and the update installs
- [ ] **Close DCS and update**, exit NOT confirmed within 2 minutes: switches to "update when DCS closes"
- [ ] Same in the main menu and inside a mission (confirmation box may differ)
- [ ] **Update when DCS closes**: waits while DCS runs, installs right after DCS exits
- [ ] **Cancel scheduled update** stops the wait
- [ ] **Skip this version**: no pop-up on the next automatic check; *Check now* still shows it
- [ ] **Remind me later**: pop-up returns on the next automatic check (startup / 6 h)
- [ ] UAC denied → "Update cancelled", client keeps running
- [ ] Silent update keeps previous installer choices (DCS scripts re-copied to Saved Games, server hook,
      firewall rule not duplicated) and `Export.lua` still has exactly one UCS line
- [ ] After updating with DCS open: client warns that DCS runs older scripts until restarted
- [ ] `SHA256SUMS.txt` present in the release; tampered/mismatched file is rejected
- [ ] UCS-Server prints the "new version available" notice

## Backward compatibility (new client ↔ older server)

- [ ] Client 0.1.1+ with UCS-Server 0.1.0-alpha: sync works; log lists the disabled features
- [ ] Client 0.1.0-alpha with UCS-Server 0.1.1+: sync works (old client treated as args format 1)
- [ ] Crew members on different UCS versions: note in the log, sync still works

## In DCS (alpha test campaign)

- [ ] OH-6A, single player: profile generated automatically, `Active: OH-6A`, "sent" counter increases
- [ ] OH-6A, multiplayer, same unit (pilot + instructor seats): automatic pairing
- [ ] OH-6A baseline without UCS: which switches DCS native multicrew already syncs
- [ ] OH-6A with UCS: toggles, buttons, multi-position selectors, knobs (note failures)
- [ ] Player name detected from DCS in multiplayer
- [ ] Auto-Connect via UCS-ServerHook.lua (and whether the chat line is hidden)
- [ ] Installer on a PC with an existing `Export.lua` (SRS, Tacview...): other tools still work
