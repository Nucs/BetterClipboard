# msix_lab

A console program that is registered as a full-trust MSIX package, and a script that runs it and then looks at the
PC from outside the package. Together they measure how Windows treats a packaged desktop process: the facts
BetterClipboard's Microsoft Store edition is built on (CLAUDE.md §2.47, [`docs/microsoft-store.md`](../../../docs/microsoft-store.md) §2).

**Run it only inside a claude-desktops Windows desktop**, never on a PC in use: it switches Developer Mode on,
registers a package, and writes test keys and files into the signed-in user's profile (all named `BC-TEST-MSIX…`).
`run-lab.ps1` refuses to run elsewhere unless `-ConfirmDisposableMachine`. It never prints clipboard content: the
clipboard-history check prints a status and a count.

## What it measures

| Section (`-Only`) | Question |
|---|---|
| `identity` | Does the process have a package identity, and which data folders does Windows give it? |
| `apps` | Does a packaged process read the real registry (Installed-apps entries other programs wrote)? |
| `registry` | Where do its own `HKCU` writes and deletes land? |
| `children` | Do child processes that are other programs (cmd, reg) leave the package's environment, for each way of starting them? |
| `selfchild` | Does a child that is the package's own program keep the package identity? |
| `files` | Which AppData writes are redirected, and which are real? |
| `shared` | Can the package use a folder every edition shares (`%LOCALAPPDATA%\BetterClipboard`'s shape)? A folder made by cmd, then written by the package (subfolders, a journal that comes and goes, a memory-mapped file, an atomic replace); a private copy beside the real folder (which one the package reads and writes); and a private copy moved to the real path by cmd. Added 2026-11-14 for the shared data folder (CLAUDE.md §2.47). |
| `filewatch` | Do file change notifications arrive for real folders? |
| `dpapi` | Does a DPAPI blob sealed outside open inside, and the reverse? |
| `objects` | Are named mutexes and events shared with unpackaged processes? |
| `notify` | Does `RegNotifyChangeKeyValue` fire for another process's write to the real registry? Does polling see it? |
| `languages` | Which languages does the process get for a given manifest? |
| `cliphistory` | Does WinRT's clipboard history answer a packaged process without a window? |
| `startup` | Can the package's startup task be switched on and off, and is the user asked? |

The answers of 2026-11-12 and of the `shared` section on 2026-11-14 (Windows 11 25H2, build 26200) are in
`docs/microsoft-store.md` §2. Run the lab again after a Windows feature update or a Windows App SDK upgrade: the Store
edition's design depends on them (the app also proves its shared folder itself once per Windows build).

## Files

- `MsixLab.cs`, `msix_lab.csproj`: the probe (`msixlab.exe`, self-contained, one file). `msixlab` without arguments
  lists its commands.
- `AppxManifest.xml`: the package (a console alias and a startup task, the same shape as BetterClipboard's own).
- `child.cmd`: the child the `children` section starts; it writes one registry value and one file.
- `run-lab.ps1`: registers the package, runs the sections, prints the outside view; `-Unregister` removes everything.
- `Assets`: three placeholder logos the manifest needs.

## Run it

On the development PC (nothing is registered there):

```powershell
dotnet publish tools/probes/msix_lab -c Release -o <stage>
Copy-Item tools/probes/msix_lab/AppxManifest.xml, tools/probes/msix_lab/child.cmd, tools/probes/msix_lab/run-lab.ps1 <stage>
Copy-Item tools/probes/msix_lab/Assets <stage> -Recurse
```

Copy `<stage>` into the test desktop (for example to `C:\lab`), then there:

```powershell
powershell -ExecutionPolicy Bypass -File C:\lab\run-lab.ps1 -LabDirectory C:\lab            # everything
powershell -ExecutionPolicy Bypass -File C:\lab\run-lab.ps1 -Only languages -Languages en-US,ru-RU
powershell -ExecutionPolicy Bypass -File C:\lab\run-lab.ps1 -Unregister                      # remove it, list what stayed
```

The Appx cmdlets can take minutes on a slow test desktop: start the script in the background with its output
redirected to a file, and read the file.

### As an installed package

A loosely registered package runs from the folder it was registered in. A Store app runs from
`C:\Program Files\WindowsApps`, with other access rules. To measure there, pack the stage folder with `MakeAppx`
(without `run-lab.ps1`), sign it with a test certificate whose subject is the manifest's `Publisher`
([`tools/release/package-msix.ps1`](../../release/package-msix.ps1) `-NewTestCertificate` makes one), trust the
`.cer` in the test desktop (`Import-Certificate … Cert:\LocalMachine\TrustedPeople`), and pass the package:

```powershell
powershell -ExecutionPolicy Bypass -File C:\lab\run-lab.ps1 -PackagePath C:\lab\msixlab-signed.msix -Only identity,selfchild
```

`MakeAppx.exe` and `SignTool.exe` are in the NuGet package `microsoft.windows.sdk.buildtools`, which the app's build
restores (`~/.nuget/packages/microsoft.windows.sdk.buildtools/<version>/bin/<sdk>/x64`).
