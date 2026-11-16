DCS-UCS PRIVACY POLICY
======================

DCS-UCS (Universal Control Sync) has no telemetry, no accounts and no central service. It does not
collect, sell or keep personal data. This page lists every network connection the programs make and how
to turn each one off.

WHAT IS SENT, AND TO WHOM
-------------------------

1. The UCS server you choose (UCS-Client)
   Sent: your pilot name (DCS nickname), the DCS server, group, unit and seat you fly, the aircraft
   type, the mod version and profile fingerprint, your UCS version, and cockpit switch and flight
   control positions.
   Why: synchronize the cockpit with your crew mates in the same aircraft.
   Who receives it: only the UCS server address you type in (or that the DCS server you join announces
   through Auto-Connect), and the crew members of the same aircraft through it.
   Turn off: do not connect, or uncheck "Accept Auto-Connect from the DCS server".

2. GitHub - api.github.com, github.com (UCS-Client and UCS-Server)
   Sent: a normal HTTPS request for the list of DCS-UCS releases, and the download of the new version
   when you accept an update. GitHub sees your IP address (GitHub privacy statement:
   https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
   Why: tell you when a new version exists and install it when you ask.
   Turn off: installer option "Check for DCS-UCS updates on GitHub", the Update tab of the client,
   or "CheckForUpdates": false in server_config.json.

3. Public IP services - api.ipify.org (fallbacks checkip.amazonaws.com, icanhazip.com)
   (UCS-Server and the optional UCS-ServerHook.lua, only on the PC that hosts a server)
   Sent: a plain request; the service sees your IP address and answers it.
   Why: show the address that players must use to reach your UCS server (as games do).
   Turn off: installer option "Detect this PC's public IP address", "DetectPublicIp": false in
   server_config.json, or type the address in UCS-AutoConnect.cfg.

Nothing else is contacted. The UCS server keeps the data of connected clients in memory only and writes
nothing but its console output.

FILES ON YOUR PC
----------------

- Settings: %APPDATA%\DCS-UCS (client_config.json and aircraft profiles generated from your own mods).
- Logs: Saved Games\DCS\Logs\UCS (client log and optional diagnostic recordings). They may contain player
  names and server addresses: review them before sharing.

CHANGES TO YOUR SYSTEM (all optional, chosen in the installer)
--------------------------------------------------------------

- DCS integration: copies UCS-Hook.lua and UCS-Export.lua to Saved Games\DCS (and DCS.openbeta) and adds
  ONE line at the end of Scripts\Export.lua. The rest of Export.lua (SRS, Tacview, DCS-BIOS...) is kept
  byte for byte, and a backup is saved as Export.lua.ucs-backup.
- DCS server hook: copies UCS-ServerHook.lua to the DCS server's Saved Games folder.
- Windows Firewall: adds an inbound rule "DCS-UCS Server" for UDP port 5055.
- Uninstalling DCS-UCS removes all of the above and restores Export.lua without the UCS line.

Questions: https://github.com/LR-Almeida/DCS-UCS/issues
