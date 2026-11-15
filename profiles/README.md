# profiles/

This folder is intentionally empty. **DCS-UCS does not ship aircraft profiles.**

Profiles are generated locally from the mods installed on each machine:

- automatically by `UCS-Client.exe` the first time you enter a mod aircraft (and again whenever the mod
  is updated), saved to `%APPDATA%\DCS-UCS\profiles\`;
- or manually with `UCS-ProfileGen.exe "<aircraft type>"`.

`*.json` files in this folder are ignored by git. See [CONTRIBUTING.md](../CONTRIBUTING.md) for the policy.
