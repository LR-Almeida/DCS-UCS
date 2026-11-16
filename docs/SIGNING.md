# Code signing (SignPath Foundation)

Status: **application not yet approved** — releases are built unsigned. Everything below is already in the
repository and switches on by itself once step 3 is done.

## What the repository already has

| Requirement (signpath.org/terms) | Where |
|---|---|
| OSI license, no proprietary code | `LICENSE` (MIT); only MoonSharp (MIT) and the .NET runtime are bundled |
| Code signing policy on the home page, with the required sentence, team roles and privacy statement | `README.md` → *Code signing policy* (linked from the top of the README and from every release page) |
| Privacy policy, shown during installation, with options to turn the connections off | `PRIVACY.md`; installer page *Information* + tasks *Network connections*; `install-options.ini` read by client and server |
| Announce system changes / provide uninstallation | Installer tasks describe every change; uninstaller removes them and restores `Export.lua` |
| Product name and version metadata, same in every file | `Directory.Build.props` (Product `DCS-UCS`, version without commit suffix); installer `VersionInfo*` |
| Built from source in a verifiable way | `.github/workflows/build.yml` (GitHub-hosted runner, pinned Inno Setup with checksum) |
| Manual approval of every release | SignPath signing policy (see step 2) |

## After approval

1. **SignPath project**
   - Project slug: `dcs-ucs` (any; it goes in a repository variable).
   - Add the trusted build system *GitHub.com* and link this repository.
   - Artifact configurations: create `executables` and `installer` with the content of
     `.signpath/artifact-configurations/*.xml`.
   - Signing policy: `release-signing`, with **manual approval** by the repository owner.
2. **API token**: create a CI user / API token in SignPath with permission to submit signing requests.
3. **GitHub repository settings** (Settings → Secrets and variables → Actions):
   - Secret `SIGNPATH_API_TOKEN`
   - Variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` (`dcs-ucs`),
     `SIGNPATH_SIGNING_POLICY_SLUG` (`release-signing`)
4. Push a release tag. The workflow uploads the executables, waits for your approval in SignPath (up to
   2 hours), puts the signed executables in the installer, then submits the installer for a second
   approval, and only then publishes the release and `SHA256SUMS.txt`.

Builds of `main` and pull requests are never signed.

## Things to verify on the first signed release

- SignPath accepts the `parameters` input of `signpath/github-action-submit-signing-request@v1` and the
  `${version}` metadata restriction (otherwise set the version restriction in the SignPath UI).
- Inno Setup pads the installer's version strings with spaces; adjust `installer.xml` if the exact
  product-name match fails.
- `Get-AuthenticodeSignature` in the workflow log shows `Valid` for every file.
- The in-app updater still installs the signed installer silently.
