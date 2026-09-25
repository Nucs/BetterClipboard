# Third-party notices

BetterClipboard itself is MIT-licensed (see [LICENSE](LICENSE)). The release builds are self-contained and
redistribute the following components, each under its own license:

| Component | Use in BetterClipboard | License |
|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | Runtime (self-contained builds) | MIT |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (Base, Foundation, InteractiveExperiences, WinUI, DWrite) | WinUI 3 user interface | MIT (Windows App SDK license terms) |
| [C#/WinRT & Windows SDK projection](https://github.com/microsoft/CsWinRT) | WinRT interop (clipboard history import, imaging) | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | SQLite access | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | SQLite native bindings | Apache-2.0 |
| [SQLite3 Multiple Ciphers](https://github.com/utelle/SQLite3MultipleCiphers) and its [SQLitePCLRaw bundle](https://github.com/utelle/SQLite3MultipleCiphers-NuGet) | Encrypted SQLite engine (ChaCha20-Poly1305) | MIT |
| [SQLite](https://sqlite.org/copyright.html) | Database engine (inside SQLite3 Multiple Ciphers) | Public domain |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM source generators | MIT |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | DPAPI key sealing | MIT |

The full license texts are available at the links above. Apache-2.0 components are redistributed
unmodified.
