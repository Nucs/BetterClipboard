# Chocolatey package source

This folder is the source of the [`betterclipboard`](https://community.chocolatey.org/packages/betterclipboard)
Chocolatey package. It is a template: [`tools/release/package-chocolatey.ps1`](../../tools/release/package-chocolatey.ps1)
turns it into `betterclipboard.<version>.nupkg` for every release.

| File | In the package | Role |
|---|---|---|
| `betterclipboard.nuspec` | yes, filled in | Metadata. The build fills `{{PACKAGE_VERSION}}` and `{{RELEASE_TAG}}` and strips the comments. |
| `tools/chocolateyinstall.ps1` | yes | Extracts the archive for the PC's architecture into `tools\app`, marks the shims, adds the Start menu shortcut and "Start with Windows", starts the app. |
| `tools/chocolateybeforemodify.ps1` | yes | Closes the app before an upgrade or uninstall, and records whether it was running. |
| `tools/chocolateyuninstall.ps1` | yes | Removes the shortcut, the "Start with Windows" entry and the state file; gives Win+V back to Windows; keeps the history. |
| `tools/helpers.ps1` | yes | Names and functions the three scripts share. |
| `legal/VERIFICATION.txt` | yes, filled in | How to check the embedded archives against the GitHub release. The build fills `{{ARCHIVES}}` and `{{RELEASE_TAG}}`. |
| `legal/LICENSE.txt` | yes, generated | The build writes it from the repository's `LICENSE`. |
| `README.md` | no | This file. |

What the build adds to `tools\`: the release's `BetterClipboard-<version>-win-x64.7z` and `-arm64.7z`, made by
[`tools/release/package.ps1`](../../tools/release/package.ps1) next to the zips and listed in `SHA256SUMS.txt`.

Build, test and publish:

```powershell
pwsh tools/release/package.ps1 -Version 1.2.3                  # zips, .7z archives, SHA256SUMS.txt
pwsh tools/release/package-chocolatey.ps1 -Version 1.2.3       # artifacts/release/betterclipboard.1.2.3.nupkg
pwsh tools/release/test-chocolatey.ps1 -Version 1.2.3          # real install/upgrade/uninstall: CI or a VM only
choco push artifacts/release/betterclipboard.1.2.3.nupkg --source https://push.chocolatey.org/
```

The release workflow runs all four for every stable tag (`v1.2.3`; the push needs the `CHOCOLATEY_API_KEY`
secret). Why each script does what it does - the Community Repository's rules and the Chocolatey behaviors they
were tested against - is in [`docs/chocolatey.md`](../../docs/chocolatey.md).

Scripts must stay ASCII: the build saves them as UTF-8 with a byte order mark, which Windows PowerShell needs to
read anything else correctly, and checks that no placeholder is left.
