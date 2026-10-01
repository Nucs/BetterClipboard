# Third-party notices

BetterClipboard itself is MIT-licensed (see [LICENSE](LICENSE)). The release builds are self-contained and
redistribute the following components, each under its own license:

| Component | Use in BetterClipboard | License |
|---|---|---|
| [.NET](https://github.com/dotnet/runtime) runtime and libraries, incl. System.Security.Cryptography.ProtectedData | Runtime (self-contained builds), DPAPI key sealing | MIT |
| [C#/WinRT](https://github.com/microsoft/CsWinRT) (`WinRT.Runtime.dll`) | WinRT interop | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM source generators | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | SQLite access | MIT |
| [SQLite](https://sqlite.org/copyright.html) | Database engine (inside SQLite3 Multiple Ciphers) | Public domain |
| [SQLite3 Multiple Ciphers](https://github.com/utelle/SQLite3MultipleCiphers) and its [SQLitePCLRaw bundle](https://github.com/utelle/SQLite3MultipleCiphers-NuGet) | Encrypted SQLite engine (ChaCha20-Poly1305) | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite native bindings | Apache-2.0 |
| [WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/License) (`Microsoft.Web.WebView2.Core.dll`, `WebView2Loader.dll`) | A dependency of WinUI 3; BetterClipboard itself hosts no web content | BSD-3-Clause |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (Base, Foundation, InteractiveExperiences, WinUI, DWrite) | WinUI 3 user interface, DWriteCore text rendering | Windows App SDK license terms |
| [Windows SDK](https://learn.microsoft.com/windows/apps/windows-sdk/) projection for .NET (`Microsoft.Windows.SDK.NET.dll`) | WinRT API definitions (Windows clipboard history import, imaging) | Windows SDK license terms |

The full license texts are available at the links above. Apache-2.0 components are redistributed
unmodified. The Windows App SDK binaries come under Microsoft's license terms for the Windows App SDK (the
`license.txt` in each package, e.g. [Microsoft.WindowsAppSDK.WinUI 2.3.9](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.9/License));
the MIT license on GitHub covers their source. The Windows SDK projection comes under the
[Windows SDK license terms](https://aka.ms/WinSDKLicenseURL).

BetterClipboard also works with [ShareX](https://getsharex.com/), [Everything](https://www.voidtools.com/) by
voidtools and [PowerShell](https://learn.microsoft.com/powershell/)'s command history. They are separate apps by
their own makers, not part of the download, and BetterClipboard never changes their settings or data.
*Settings › Third party* in the app lists all of the above with their official links.
