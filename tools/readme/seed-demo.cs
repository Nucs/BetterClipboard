#:property TargetFramework=net10.0-windows10.0.26100.0
#:property PublishAot=false
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property GenerateDocumentationFile=true
#:project ../../src/BetterClipboard.Windows/BetterClipboard.Windows.csproj

// Loads BetterClipboard's demo data set (demo-data.zip, next to this file) into a disposable claude-desktops Windows
// desktop: the history store of one BetterClipboard data folder, the prompt histories of Claude Code and Codex, and
// PowerShell's history file. The Store listing's screenshots and the README's photos are taken with it (CLAUDE.md
// sections 3.4 and 3.5). Run it while BetterClipboard is not running, before its first start with this data folder.
//
//   dotnet publish tools/readme/seed-demo.cs -r win-x64 --self-contained -o <folder>   (on the PC: the desktop has no SDK)
//   seed-demo.exe --data Z:\demo-data.zip --package-family Nucs.BetterClipboard_mgez7y7xa0my4 --confirm-disposable-machine
//   seed-demo.exe --data Z:\demo-data.zip --data-dir %LOCALAPPDATA%\BetterClipboard --confirm-disposable-machine
//   dotnet run tools/readme/seed-demo.cs -- --data tools/readme/demo-data.zip --check    (anywhere: reads, writes nothing)
//
// Exit codes: 0 loaded (or checked); 2 bad arguments or a refused machine; 3 the target already holds data; 4 a failure.
using BetterClipboard.Core;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Integrations;
using BetterClipboard.Core.Model;
using BetterClipboard.Core.Security;
using BetterClipboard.Core.Services;
using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Storage;
using BetterClipboard.Windows.Imaging;
using BetterClipboard.Windows.Security;

return await DemoSeeder.RunAsync(args);

/// <summary>
/// Loads the demo data set into one BetterClipboard data folder and the source apps' history files.
/// </summary>
/// <remarks>
/// <para><b>Only on a throwaway machine.</b> It writes Claude Code's, Codex's and PowerShell's own history files, so on
/// a real PC it would replace that user's prompt and command history. Three guards: <c>--confirm-disposable-machine</c>
/// must be given; the claude-desktops agent's folder (<c>C:\ProgramData\claude-desktop</c>, which no ordinary PC has)
/// must exist; and no file or store it would write may exist already.</para>
/// <para><b>Times are relative.</b> Each item of the set says how many minutes before "now" it was copied, sent or run,
/// so the history always covers the seven weeks before the moment it is loaded.</para>
/// <para><b>Through the app's own code.</b> Entries go through <see cref="ClipHistoryService"/> like any copy (the
/// classifier, the picture analysis, the path count), so the store is the one the app itself would have written.
/// Prompts are not written into the store: the app's first start imports them from the agents' history files, as it
/// does on any PC.</para>
/// </remarks>
internal static class DemoSeeder
{
    /// <summary>The claude-desktops agent's folder: present in those desktops only, the machine guard.</summary>
    private const string DesktopAgentFolder = @"C:\ProgramData\claude-desktop";

    /// <summary>
    /// Parses the arguments, checks the guards, loads the set and prints what was loaded.
    /// </summary>
    /// <param name="args">The command line (see the file's header).</param>
    /// <returns>
    /// The exit code: 0 loaded (or, with <c>--check</c>, read whole), 2 bad arguments or refused machine, 3 data already
    /// there, 4 a failure.
    /// </returns>
    /// <remarks>Never throws: every failure is printed and turned into its exit code.</remarks>
    public static async Task<int> RunAsync(string[] args)
    {
        string? data = null, dataDir = null, family = null;
        bool confirmed = false, checkOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length: data = args[++i]; break;
                case "--data-dir" when i + 1 < args.Length: dataDir = Environment.ExpandEnvironmentVariables(args[++i]); break;
                case "--package-family" when i + 1 < args.Length: family = args[++i]; break;
                case "--confirm-disposable-machine": confirmed = true; break;
                case "--check": checkOnly = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (checkOnly && data is not null)
        {
            // Reads the whole set, pictures included, and writes nothing: safe on any PC, so a broken zip is found
            // before it is copied into a desktop.
            try
            {
                var set = DemoSet.Load(data);
                var bytes = set.Entries.Where(e => e.Image is not null).Sum(e => (long)set.ReadPicture(e.Image!).Length);
                Console.WriteLine($"The set reads: {set.Entries.Count} entries ({set.Entries.Count(e => e.Image is not null)} pictures, " +
                                  $"{bytes:N0} bytes), {set.Groups.Count} groups, {set.Claude.Count} Claude Code and {set.Codex.Count} " +
                                  $"Codex prompts, {set.Pwsh.Count} PowerShell commands.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"The set does not read: {ex.GetType().Name}: {ex.Message}");
                return 4;
            }
        }

        if (data is null || (dataDir is null) == (family is null))
        {
            Console.Error.WriteLine("Usage: seed-demo --data <demo-data.zip|folder> (--check | (--data-dir <folder> | --package-family <name>) --confirm-disposable-machine)");
            return 2;
        }

        // Both guards, before anything is read: the flag says the caller means it, the folder proves the machine.
        if (!confirmed || !Directory.Exists(DesktopAgentFolder))
        {
            Console.Error.WriteLine("Refused: this writes the prompt and command histories of the account. It runs only in a claude-desktops desktop, with --confirm-disposable-machine.");
            return 2;
        }

        var paths = family is null ? new AppPaths(dataDir!) : AppPaths.ResolveDefault(family);
        try
        {
            var set = DemoSet.Load(data);
            var targets = SourceFiles.ForCurrentUser();
            var occupied = targets.Existing().ToList();
            if (Directory.Exists(paths.StoresDirectory))
            {
                occupied.Add(paths.StoresDirectory);
            }

            if (occupied.Count > 0)
            {
                Console.Error.WriteLine("Refused: these already exist, and loading would mix the set into them:");
                occupied.ForEach(path => Console.Error.WriteLine("  " + path));
                return 3;
            }

            var now = DateTimeOffset.UtcNow;
            var counts = await StoreLoader.LoadAsync(paths, set, now).ConfigureAwait(false);
            targets.Write(set, now);
            SettingsWriter.Write(paths);
            Console.WriteLine($"Loaded into {paths.DataDirectory}: {counts.Entries} entries ({counts.Pictures} pictures, " +
                              $"{counts.Runs} Win+R runs, {counts.Pinned} pinned, {counts.Groups} groups); " +
                              $"{set.Claude.Count} Claude Code and {set.Codex.Count} Codex prompts and {set.Pwsh.Count} PowerShell commands written for the app to import.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Loading failed: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }
}

/// <summary>The demo data set as read from <c>demo-data.json</c> (and its pictures).</summary>
/// <param name="Groups">The groups to create, in column order.</param>
/// <param name="Entries">The history entries, oldest first.</param>
/// <param name="Claude">Claude Code prompts, oldest first.</param>
/// <param name="Codex">Codex prompts, oldest first.</param>
/// <param name="Pwsh">PowerShell commands, oldest first.</param>
/// <param name="ReadPicture">Reads one picture of the set by its relative path ("images/x.png").</param>
internal sealed record DemoSet(
    IReadOnlyList<DemoGroup> Groups,
    IReadOnlyList<DemoEntry> Entries,
    IReadOnlyList<DemoPrompt> Claude,
    IReadOnlyList<DemoPrompt> Codex,
    IReadOnlyList<string> Pwsh,
    Func<string, byte[]> ReadPicture)
{
    /// <summary>JSON names are camelCase in the file.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads the set from the zip (or an unpacked folder).</summary>
    /// <param name="path">demo-data.zip, or a folder holding demo-data.json and images\.</param>
    /// <returns>The set; pictures are read from the same zip or folder when they are needed.</returns>
    /// <exception cref="FileNotFoundException">Neither a zip nor a folder with demo-data.json is there.</exception>
    /// <exception cref="InvalidDataException">The JSON is not the set's (version 1).</exception>
    public static DemoSet Load(string path)
    {
        Func<string, byte[]> read;
        string json;
        if (File.Exists(path))
        {
            // The zip is read whole into memory (a few MB): pictures are then served without keeping the file open.
            var bytes = File.ReadAllBytes(path);
            read = name =>
            {
                using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
                var item = archive.GetEntry("demo-data/" + name) ?? throw new FileNotFoundException($"{name} is not in the set.");
                using var stream = item.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            };
            json = System.Text.Encoding.UTF8.GetString(read("demo-data.json"));
        }
        else if (File.Exists(Path.Combine(path, "demo-data.json")))
        {
            read = name => File.ReadAllBytes(Path.Combine(path, name.Replace('/', Path.DirectorySeparatorChar)));
            json = File.ReadAllText(Path.Combine(path, "demo-data.json"));
        }
        else
        {
            throw new FileNotFoundException($"No demo data set at {path}.");
        }

        var file = System.Text.Json.JsonSerializer.Deserialize<DemoFile>(json, Json)
                   ?? throw new InvalidDataException("demo-data.json is empty.");
        if (file.Version != 1)
        {
            throw new InvalidDataException($"demo-data.json is version {file.Version}; this loader reads version 1.");
        }

        return new DemoSet(file.Groups, file.Entries.OrderByDescending(e => e.MinutesAgo).ToList(),
                           file.Claude.OrderByDescending(p => p.MinutesAgo).ToList(),
                           file.Codex.OrderByDescending(p => p.MinutesAgo).ToList(), file.Pwsh, read);
    }

    /// <summary>The file's shape.</summary>
    /// <param name="Version">Format version (1).</param>
    /// <param name="Groups">See <see cref="DemoSet.Groups"/>.</param>
    /// <param name="Entries">See <see cref="DemoSet.Entries"/>.</param>
    /// <param name="Claude">See <see cref="DemoSet.Claude"/>.</param>
    /// <param name="Codex">See <see cref="DemoSet.Codex"/>.</param>
    /// <param name="Pwsh">See <see cref="DemoSet.Pwsh"/>.</param>
    private sealed record DemoFile(int Version, List<DemoGroup> Groups, List<DemoEntry> Entries, List<DemoPrompt> Claude,
                                   List<DemoPrompt> Codex, List<string> Pwsh);
}

/// <summary>One group of the set.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Glyph">Its icon: one Segoe Fluent Icons character from the app's catalog.</param>
internal sealed record DemoGroup(string Name, string Glyph);

/// <summary>One history entry of the set.</summary>
/// <param name="Text">The copied text, or null for a picture.</param>
/// <param name="Image">The picture's path inside the set ("images/x.png"), or null for a text.</param>
/// <param name="App">The app it was copied in, as the cards name it ("Google Chrome", "Win+R", "Snipping Tool").</param>
/// <param name="MinutesAgo">Minutes before "now" it was copied, run or saved.</param>
/// <param name="Role">text, run, picture, screenshot, windows-history or notepad-tab.</param>
/// <param name="Pinned">Whether it is pinned.</param>
/// <param name="Groups">The names of the groups it belongs to.</param>
/// <param name="Title">A Notepad++ tab's name ("new 3"), or null.</param>
internal sealed record DemoEntry(string? Text, string? Image, string? App, double MinutesAgo, string Role, bool Pinned,
                                 List<string> Groups, string? Title);

/// <summary>One prompt of the set.</summary>
/// <param name="Text">What was sent.</param>
/// <param name="Project">The folder it was sent in (forward slashes), or null.</param>
/// <param name="MinutesAgo">Minutes before "now" it was sent.</param>
internal sealed record DemoPrompt(string Text, string? Project, double MinutesAgo);

/// <summary>What <see cref="StoreLoader"/> loaded.</summary>
/// <param name="Entries">Entries stored.</param>
/// <param name="Pictures">Pictures among them.</param>
/// <param name="Runs">Win+R runs among them.</param>
/// <param name="Pinned">Pinned among them.</param>
/// <param name="Groups">Groups created.</param>
internal sealed record LoadCounts(int Entries, int Pictures, int Runs, int Pinned, int Groups);

/// <summary>Writes the set's entries into a new history store through the app's own history service.</summary>
internal static class StoreLoader
{
    /// <summary>
    /// Creates the store of <paramref name="paths"/> (bound to this machine and account, like the app's), and adds
    /// every entry, then the groups.
    /// </summary>
    /// <param name="paths">The BetterClipboard data folder to fill.</param>
    /// <param name="set">The set.</param>
    /// <param name="now">"Now": every entry's time is this minus its minutes.</param>
    /// <returns>What was loaded.</returns>
    /// <exception cref="InvalidOperationException">An entry was not stored (a rule of the app dropped it).</exception>
    /// <exception cref="Exception">Anything <see cref="MachineBoundHistory.Open"/> or the service throws.</exception>
    public static async Task<LoadCounts> LoadAsync(AppPaths paths, DemoSet set, DateTimeOffset now)
    {
        var identity = MachineIdentity.Current();
        var binding = MachineBinding.Derive(identity.MachineGuid, identity.UserSid);
        var opened = MachineBoundHistory.Open(paths, new DpapiKeyProtector(), binding, now);
        var ids = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        int pictures = 0, runs = 0, pinned = 0, stored = 0;
        var screenshotNumber = 1;
        await using (var history = new ClipHistoryService(opened.Store, new WinRtImageAnalyzer(), () => new CaptureRules()))
        {
            history.Start();

            // One entry at a time, oldest first: each is awaited, so the store gives them ids in time order and a
            // failure names the entry that failed.
            foreach (var entry in set.Entries)
            {
                var at = now - TimeSpan.FromMinutes(entry.MinutesAgo);
                var capture = entry.Image is null
                    ? TextCapture(entry, at)
                    : PictureCapture(entry, at, set.ReadPicture(entry.Image), ref screenshotNumber);
                var saved = await history.AddAsync(capture).ConfigureAwait(false)
                            ?? throw new InvalidOperationException($"An entry of {entry.App ?? "Windows' history"} was not stored.");
                stored++;
                pictures += entry.Image is null ? 0 : 1;
                runs += entry.Role == "run" ? 1 : 0;
                pinned += entry.Pinned ? 1 : 0;
                foreach (var group in entry.Groups)
                {
                    (ids.TryGetValue(group, out var list) ? list : ids[group] = []).Add(saved.Id);
                }
            }

            // Groups last, in the set's column order, so their sort order is the set's.
            foreach (var group in set.Groups)
            {
                var created = await history.CreateGroupAsync(group.Name, group.Glyph).ConfigureAwait(false);
                if (ids.TryGetValue(group.Name, out var members))
                {
                    await history.AddToGroupAsync(members, created.Id).ConfigureAwait(false);
                }
            }
        }

        // Closing every pooled connection checkpoints the write-ahead log into the database file, so the app opens one
        // finished file and this process holds nothing once it ends.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return new LoadCounts(stored, pictures, runs, pinned, set.Groups.Count);
    }

    /// <summary>A text entry as the app would have captured it.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="at">When it was copied.</param>
    /// <returns>
    /// The capture: a Win+R run (as the first import of Windows' list records it, so the store stamps its run time), a
    /// copy imported from Windows' own history (no app known), a Notepad++ tab's unsaved text (with the tab's name), or a
    /// plain copy.
    /// </returns>
    /// <exception cref="InvalidDataException">The entry has no text.</exception>
    private static ClipCapture TextCapture(DemoEntry entry, DateTimeOffset at)
    {
        var text = entry.Text ?? throw new InvalidDataException("A text entry without text.");
        var (origin, source) = entry.Role switch
        {
            "run" => (ClipOrigin.RunDialogHistory, RunMru.Source),
            "windows-history" => (ClipOrigin.WindowsHistory, (SourceAppInfo?)null),
            "notepad-tab" => (ClipOrigin.NotepadPlusPlusTab, Apps.For(entry.App)),
            _ => (ClipOrigin.Captured, Apps.For(entry.App)),
        };

        // Built here rather than by RunMru.CreateCapture: a capture is a class with init-only members, and a run of the
        // set may be pinned, which that factory cannot say.
        return new ClipCapture
        {
            Formats = [new ClipFormatData(ClipFormatNames.UnicodeText, UnicodeTextCodec.Encode(text))],
            CapturedAtUtc = at,
            Source = source,
            Origin = origin,
            SourceTitle = entry.Role == "notepad-tab" ? entry.Title : null,
            Pin = entry.Pinned,
        };
    }

    /// <summary>
    /// A picture entry: a screenshot saved into the Screenshots folder (with its file, origin "screenshot"), or a picture
    /// copied in an app (no file).
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="at">When it was copied or saved.</param>
    /// <param name="png">The picture (PNG).</param>
    /// <param name="screenshotNumber">The next Win+PrtScn number ("Screenshot (N).png"); advanced when used.</param>
    /// <returns>The capture.</returns>
    /// <exception cref="IOException">The screenshot file could not be written.</exception>
    private static ClipCapture PictureCapture(DemoEntry entry, DateTimeOffset at, byte[] png, ref int screenshotNumber)
    {
        ClipFormatData[] formats = [new ClipFormatData(ClipFormatNames.Png, png)];
        if (entry.Role != "screenshot")
        {
            return new ClipCapture { Formats = formats, CapturedAtUtc = at, Source = Apps.For(entry.App), Pin = entry.Pinned };
        }

        // Saved where Windows' tools save, under the name each tool gives, with the file's times at the save.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(folder);
        var local = at.ToLocalTime();
        var winPrtScn = entry.App == "Win+PrtScn";
        var name = winPrtScn ? $"Screenshot ({screenshotNumber++}).png" : $"Screenshot {local:yyyy-MM-dd HHmmss}.png";
        var file = Path.Combine(folder, name);
        File.WriteAllBytes(file, png);
        File.SetCreationTimeUtc(file, at.UtcDateTime);
        File.SetLastWriteTimeUtc(file, at.UtcDateTime);
        return new ClipCapture
        {
            Formats = formats,
            CapturedAtUtc = at,
            Source = winPrtScn ? WindowsScreenshots.WinPrtScnSource : WindowsScreenshots.SnippingToolSource(null),
            Origin = ClipOrigin.WindowsScreenshot,
            File = new SourceFile(file, png.LongLength, at, at, "PNG"),
            Pin = entry.Pinned,
        };
    }
}

/// <summary>The source apps the set names, as the capture pipeline would have recorded them.</summary>
internal static class Apps
{
    /// <summary>Process names of the apps the set names; the rest use their name without spaces.</summary>
    private static readonly Dictionary<string, string> Processes = new(StringComparer.Ordinal)
    {
        ["Google Chrome"] = "chrome", ["Firefox"] = "firefox", ["Visual Studio Code"] = "Code", ["Windows PowerShell"] = "powershell",
        ["Windows Terminal Host"] = "WindowsTerminal", ["Agentmaster"] = "Agentmaster", ["Codex"] = "Codex", ["Notepad++"] = "notepad++",
        ["JetBrains Rider (GUI launcher)"] = "rider64", ["GitHub Desktop"] = "GitHubDesktop", ["BetterClipboard"] = "BetterClipboard",
        ["ShareX"] = "ShareX", ["Windows Explorer"] = "explorer",
    };

    /// <summary>The source of a copy made in <paramref name="displayName"/>.</summary>
    /// <param name="displayName">The app's name as cards show it.</param>
    /// <returns>The source, without an executable path: the desktop may not have the app, and the name is what shows.</returns>
    public static SourceAppInfo? For(string? displayName) => displayName is null
        ? null
        : new SourceAppInfo(Processes.TryGetValue(displayName, out var process) ? process : displayName.Replace(" ", ""), null, displayName);
}

/// <summary>The history files of the account that the app's integrations read: Claude Code's, Codex's, PowerShell's.</summary>
/// <param name="Claude">Claude Code's prompt history (~\.claude\history.jsonl).</param>
/// <param name="Codex">Codex's prompt history (~\.codex\history.jsonl).</param>
/// <param name="Pwsh">PowerShell's (PSReadLine's) history of the console host.</param>
internal sealed record SourceFiles(string Claude, string Codex, string Pwsh)
{
    /// <summary>The files of the account this runs as.</summary>
    /// <returns>Their paths (not created).</returns>
    public static SourceFiles ForCurrentUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new SourceFiles(Path.Combine(home, ".claude", "history.jsonl"), Path.Combine(home, ".codex", "history.jsonl"),
                               Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"));
    }

    /// <summary>Which of the files exist already (loading would replace someone's history).</summary>
    /// <returns>The existing paths.</returns>
    public IEnumerable<string> Existing() => new[] { Claude, Codex, Pwsh }.Where(File.Exists);

    /// <summary>
    /// Writes the three files in each tool's own format, with the set's times relative to <paramref name="now"/>.
    /// </summary>
    /// <param name="set">The set.</param>
    /// <param name="now">"Now".</param>
    /// <exception cref="IOException">A file could not be written.</exception>
    /// <remarks>
    /// Session ids are made up here, one per project and day, so that the archive groups sends the way the agents do;
    /// the set itself holds none (they are the kind of identifier it must not carry).
    /// </remarks>
    public void Write(DemoSet set, DateTimeOffset now)
    {
        var claude = new System.Text.StringBuilder();
        foreach (var prompt in set.Claude)
        {
            var at = now - TimeSpan.FromMinutes(prompt.MinutesAgo);
            var project = prompt.Project?.Replace('/', '\\');
            var line = new Dictionary<string, object?>
            {
                ["display"] = prompt.Text, ["pastedContents"] = new Dictionary<string, object>(), ["timestamp"] = at.ToUnixTimeMilliseconds(),
                ["project"] = project, ["sessionId"] = SessionId(project, at),
            };
            claude.Append(System.Text.Json.JsonSerializer.Serialize(line)).Append('\n');
        }

        var codex = new System.Text.StringBuilder();
        foreach (var prompt in set.Codex)
        {
            var at = now - TimeSpan.FromMinutes(prompt.MinutesAgo);
            var line = new Dictionary<string, object?> { ["session_id"] = SessionId(prompt.Project, at), ["ts"] = at.ToUnixTimeSeconds(), ["text"] = prompt.Text };
            codex.Append(System.Text.Json.JsonSerializer.Serialize(line)).Append('\n');
        }

        Write(Claude, claude.ToString());
        Write(Codex, codex.ToString());
        // PSReadLine: one command per line, CRLF, a backtick before each inner line break.
        Write(Pwsh, string.Concat(set.Pwsh.Select(c => c.Replace("\r\n", "\n").Replace("\n", "`\n") + "\r\n")));
    }

    /// <summary>A made-up session id, the same for one project on one day.</summary>
    /// <param name="project">The project folder, or null.</param>
    /// <param name="at">When the prompt was sent.</param>
    /// <returns>A GUID derived from the project and the day.</returns>
    private static string SessionId(string? project, DateTimeOffset at) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{project}|{at.UtcDateTime:yyyy-MM-dd}"))).ToString();

    /// <summary>Writes one file as UTF-8 without a byte order mark, creating its folder.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">Its content.</param>
    /// <exception cref="IOException">It could not be written.</exception>
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>The settings the demo starts with.</summary>
internal static class SettingsWriter
{
    /// <summary>
    /// Writes the data folder's settings: English, dark, the welcome already answered, and the panel's size of the photos.
    /// </summary>
    /// <param name="paths">The data folder.</param>
    /// <remarks>
    /// The welcome counts as answered so that the app starts straight into its tray; its own screenshot is taken before
    /// the set is loaded. A settings file the language picker or the welcome's "Not now" left is read first, so that
    /// everything else keeps what is there (or the app's defaults).
    /// </remarks>
    /// <exception cref="IOException">The settings file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The data folder is not writable.</exception>
    public static void Write(AppPaths paths)
    {
        var settings = new SettingsStore(paths.SettingsPath);
        settings.Load();
        settings.Update(s => s with
        {
            StoreWelcomeAccepted = true, LanguageChosen = true, InterfaceLanguage = "en", Theme = AppTheme.Dark,
            FlyoutWidth = 432, FlyoutHeight = 640,
        });
    }
}
