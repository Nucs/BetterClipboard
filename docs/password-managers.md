# Password managers BetterClipboard ignores by default

BetterClipboard never records copies made by these apps. The list lives in
[`KnownPasswordManagers.cs`](../src/BetterClipboard.Core/Settings/KnownPasswordManagers.cs) and fills
*Settings › Ignored apps* on first run. Existing installs receive it on update, and names added to the
catalog later arrive exactly once. If you delete an entry, it stays deleted.
*Add known password managers* puts back everything that is missing.

**How matching works.** When something is copied, BetterClipboard finds the process that owns the
clipboard (if none does, the app in the foreground). It then compares that process's executable name,
without `.exe` and ignoring case, with the list. Many password managers also mark their copies as private
(`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`), and those copies are
skipped whether or not the app is listed. The list covers the managers that don't set these markers,
Electron-based ones in particular.

Names were checked on 2026-09-25. Sources are the vendor's own build files for open-source apps (Electron
`productName`/`executableName`, Flutter `BINARY_NAME`, Qt `TARGET`, MSBuild `AssemblyName`), otherwise
process databases (file.net and similar) and vendor documentation. Some listed names are helper processes
that can own the clipboard: tray agents, browser-integration hosts, and older major versions that ran
under another name. Guessed names are left out on purpose.

## Password managers

| Product | Process names | Evidence |
|---|---|---|
| 1Password | `1Password` (8), `1Password-BrowserSupport`, `AgileBits.OnePassword.Desktop` (7), `Agile1pAgent` (4) | [file.net 1password.exe](https://www.file.net/process/1password.exe.html), [agilebits.onepassword.desktop.exe](https://www.file.net/process/agilebits.onepassword.desktop.exe.html), [agile1pagent.exe](https://www.file.net/process/agile1pagent.exe.html), [1password-browsersupport.exe](https://spyshelter.com/exe/agilebits-1password-browsersupport-exe) |
| AuthPass | `authpass` | [authpass/authpass](https://github.com/authpass/authpass) `authpass/windows/CMakeLists.txt`: `BINARY_NAME "authpass"` |
| Bitdefender Wallet | `bdapppassmgr`, `bdwtxag`, `pmbxag` | [ghacks](https://www.ghacks.net/2014/01/16/prevent-bitdefender-pmbxag-exe-bdapppassmgr-exe-running-automatically/), [file.net pmbxag.exe](https://www.file.net/process/pmbxag.exe.html) |
| Bitwarden | `Bitwarden` | [bitwarden/clients](https://github.com/bitwarden/clients) `apps/desktop/electron-builder.json`: `productName "Bitwarden"`; [file.net](https://www.file.net/process/bitwarden.exe.html) |
| Buttercup | `Buttercup` | [buttercup/buttercup-desktop](https://github.com/buttercup/buttercup-desktop) `package.json`: `productName "Buttercup"` |
| Cyclonis Password Manager | `CyclonisPasswordManager` | [software.informer](https://cyclonis-password-manager.software.informer.com/) |
| Dashlane (desktop app, discontinued) | `Dashlane`, `DashlanePlugin` | [file.net dashlane.exe](https://www.file.net/process/dashlane.exe.html), [dashlaneplugin.exe](https://www.file.net/process/dashlaneplugin.exe.html) |
| Enpass | `Enpass`, `EnpassHelper` | [file.net enpass.exe](https://www.file.net/process/enpass.exe.html), [enpasshelper.exe](https://www.file.net/process/enpasshelper.exe.html) |
| ESET Password Manager | `pwm` | [file.net pwm.exe](https://www.file.net/process/pwm.exe.html), [glarysoft](https://www.glarysoft.com/startups/esetpasswordmanager/pwmexe/388814) |
| F-Secure KEY | `fskey` | [spyshelter](https://www.spyshelter.com/exe/f-secure-corporation-fskey-exe/) |
| iCloud Passwords | `iCloudPasswords` | [Apple Community](https://discussions.apple.com/thread/256020032), [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/2197289/icloud-crashing-during-login-on-windows-11) |
| Kaspersky Password Manager | `kpm`, `kpm_tray` | [file.net kpm.exe](https://www.file.net/process/kpm.exe.html), [kpm_tray.exe](https://www.file.net/process/kpm_tray.exe.html) |
| KeePass | `KeePass` | [file.net](https://www.file.net/process/keepass.exe.html) |
| KeePassX | `KeePassX` | [pcmatic](https://www.pcmatic.com/company/libraries/process/detail.asp?fn=KeePassX.exe.html) |
| KeePassXC | `KeePassXC` | [file.net](https://www.file.net/process/keepassxc.exe.html) |
| Keeper | `keeperpasswordmanager` | [file.net](https://www.file.net/process/keeperpasswordmanager.exe.html), [Keeper docs](https://docs.keeper.io/enterprise-guide/deploying-keeper-to-end-users/desktop-application) |
| KeeWeb | `KeeWeb` | [keeweb/keeweb](https://github.com/keeweb/keeweb) `Gruntfile.js`: packager `name: 'KeeWeb'` |
| Keyguard | `Keyguard` | [AChep/keyguard-app](https://github.com/AChep/keyguard-app) `desktopApp/build.gradle.kts`: `packageName = "Keyguard"` |
| LastPass | `LastPass`, `lastapp`, `lastapp_x64`, `nplastpass` | [LastPass support](https://support.lastpass.com/help/about-the-lastpass-for-windows-desktop-application), [file.net lastapp.exe](https://www.file.net/process/lastapp.exe.html), [lastapp_x64.exe](https://www.file.net/process/lastapp_x64.exe.html), [nplastpass.exe](https://www.file.net/process/nplastpass.exe.html) |
| McAfee True Key | `TrueKey` | [file.net](https://www.file.net/process/truekey.exe.html) |
| mSecure | `mSecure` | [informer](https://msecure.informer.com/6.0/) |
| Myki (discontinued) | `Myki` | [file.net](https://www.file.net/process/myki.exe.html) |
| NordPass | `NordPass`, `nordpass-background-app` | [file.net nordpass.exe](https://www.file.net/process/nordpass.exe.html), [nordpass-background-app.exe](https://www.file.net/process/nordpass-background-app.exe.html) |
| Padloc | `Padloc` | [padloc/padloc](https://github.com/padloc/padloc) `packages/tauri/src-tauri/tauri.conf.json`: `productName "Padloc"` |
| pass-winmenu | `pass-winmenu` | [geluk/pass-winmenu](https://github.com/geluk/pass-winmenu) |
| Passbolt | `passbolt` | [passbolt/passbolt-windows](https://github.com/passbolt/passbolt-windows) `passbolt/passbolt-windows.csproj`: `AssemblyName passbolt` |
| Password Boss | `PasswordBoss` | [shouldiremoveit](https://www.shouldiremoveit.com/PasswordBoss-166122-program.aspx) ("main program executable is PasswordBoss.exe") |
| Password Depot | `PasswordDepot` | [file.net](https://www.file.net/process/passworddepot.exe.html) |
| Password Manager XP | `PwdManager` | [file.net](https://www.file.net/process/pwdmanager.exe.html), [CP-Lab manual](https://www.cp-lab.com/Files/PwdManager.pdf) |
| Password Safe | `pwsafe` | [file.net](https://www.file.net/process/pwsafe.exe.html) |
| Pleasant Password Server (KeePass client) | `Pleasant KeePass Client` | [windowsbulletin](https://windowsbulletin.com/files/exe/pleasant-solutions/keepass-for-pleasant-password-server/pleasant-keepass-client-exe) |
| Proton Pass | `ProtonPass` | [ProtonMail/WebClients](https://github.com/ProtonMail/WebClients) `applications/pass-desktop/forge.config.ts`: `executableName: 'ProtonPass'` on Windows; [file.net](https://www.file.net/process/protonpass.exe.html) |
| Psono | `Psono` | [psono/psono-client](https://github.com/psono/psono-client) `src/electron/package.json`: `productName "Psono"` |
| QtPass | `qtpass` | [IJHack/QtPass](https://github.com/IJHack/QtPass) `main/main.pro`: `TARGET = qtpass` (outside macOS) |
| RememBear (discontinued) | `RememBear.App`, `RememBear` | [processchecker](https://www.processchecker.com/file/RememBear.App.exe.html) |
| RoboForm | `RoboTaskBarIcon`, `robotaskbaricon-x64`, `identities`, `rf-chrome-nm-host` | [file.net robotaskbaricon.exe](https://www.file.net/process/robotaskbaricon.exe.html), [robotaskbaricon-x64.exe](https://www.file.net/process/robotaskbaricon-x64.exe.html), [rf-chrome-nm-host.exe](https://www.file.net/process/rf-chrome-nm-host.exe.html), [identities.exe](https://windowsbulletin.com/files/exe/siber-systems/roboform-7-2-2/identities-exe) |
| SafeInCloud | `SafeInCloud` | [file.net](https://www.file.net/process/safeincloud.exe.html) |
| Samsung Pass | `SamsungPass` | [spyshelter](https://spyshelter.com/exe/samsung-electronics-co-ltd-samsungpass-exe/) |
| SplashID | `SplashID Desktop` | [windowsbulletin](https://windowsbulletin.com/files/exe/splashdata-inc/splashid-standalone-installer/splashid-desktop-exe) |
| Steganos Password Manager | `PasswordManager` | [file.net](https://www.file.net/process/passwordmanager.exe.html) |
| Sticky Password | `stpass` | [file.net](https://www.file.net/process/stpass.exe.html) |
| Trend Micro Password Manager | `PwmTower`, `PwmConsole` | [file.net pwmtower.exe](https://www.file.net/process/pwmtower.exe.html) |

## Authenticator (2FA code) apps

One-time codes are credentials too, so the authenticators with distinctive process names are included.

| Product | Process names | Evidence |
|---|---|---|
| Authy Desktop (discontinued) | `Authy Desktop` | [file.net](https://www.file.net/process/authy%20desktop.exe.html) |
| Proton Authenticator | `Proton Authenticator`, `proton-authenticator` | [ProtonMail/WebClients](https://github.com/ProtonMail/WebClients) `applications/authenticator/src-tauri`: `productName "Proton Authenticator"`, Cargo package `proton-authenticator`. Tauri names the binary after one or the other depending on version and configuration, so both are listed. |
| WinAuth | `WinAuth` | [winauth/winauth](https://github.com/winauth/winauth) |
| Yubico Authenticator | `authenticator` (6.x+), `yubioath-desktop` (5.x) | [Yubico/yubioath-flutter](https://github.com/Yubico/yubioath-flutter) `windows/CMakeLists.txt`: `BINARY_NAME "authenticator"` |

## Not covered, and why

| What | Why | What you can do |
|---|---|---|
| Browser-only managers: Chrome/Edge/Firefox built-in, Norton, Avira, Bitdefender Password Manager, LogMeOnce, Aura, and any browser extension | The copy comes from the browser process. Ignoring it would drop every copy you make in the browser. | Pause capture from the tray while you use it, or delete the item afterwards. An extension clearing the clipboard does not remove what was already recorded. |
| Command-line tools (`gopass show -c`, `keepassxc-cli clip`, `pass -c`, `bw`/`op` piped to `clip`) | The copy is made by `clip.exe` or without an owner window, so it is attributed to `clip` or the terminal. Ignoring the terminal would drop every copy made in it. | Pause capture, or delete the item afterwards. |
| Ente Auth | Runs as `auth.exe`, a name too generic to ignore for everyone. | Add `auth` yourself if you use it. |
| Password Gorilla | The executable name contains the version (`gorilla15363.exe`). | Add your exact name. |
| Zoho Vault, Devolutions Workspace, Passwarden, Total Password, Securden desktop apps | The executable name could not be verified from a reliable source. | Add the name shown in Task Manager › Details. |
| Avast Passwords | Built into `AvastUI.exe` together with the rest of the antivirus UI. | Add `AvastUI` if you want it ignored. |

Missing one? Find its name in Task Manager › Details (the *Name* column, without `.exe`). Add it in
Settings, or open a pull request against the catalog with a source for the name.
