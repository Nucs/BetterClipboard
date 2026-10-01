using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BetterClipboard.Core.Everything;
using BetterClipboard.Windows.Integrations;
using BetterClipboard.Windows.Interop;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="EverythingClient"/> against a fake Everything window in this process: how a found window is
/// trusted and asked about its state, how queries travel and replies are matched, and what happens when Everything
/// refuses, stays silent, hangs or answers garbage.
/// </summary>
public sealed class EverythingClientTests
{
    /// <summary>Owner verdict the tests' fake windows get (the fake runs in this unsigned test process).</summary>
    internal static readonly EverythingTrust Trusted = new(true, @"C:\BC-TEST\Everything.exe", "voidtools", "signed by voidtools (test)");

    /// <summary>No window with the instance's class: not running, and the owner check is never needed.</summary>
    [Fact]
    public void GetStatus_WithoutAWindow_IsNotRunning()
    {
        using var client = new EverythingClient([FakeEverything.NewInstanceName()], _ => throw new InvalidOperationException("No owner to check."));
        Assert.Same(EverythingStatus.NotRunning, client.GetStatus());
    }

    /// <summary>
    /// A window whose owner fails the check is reported as untrusted and is never sent anything: no state question,
    /// no query, no command line.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AnUntrustedOwner_IsNeverAskedAnything()
    {
        using var fake = new FakeEverything();
        uint? checkedProcess = null;
        var impostor = new EverythingTrust(false, @"C:\BC-TEST\impostor.exe", null, "its process is impostor.exe, not Everything.exe");
        using var client = new EverythingClient([fake.Instance], pid =>
        {
            checkedProcess = pid;
            return impostor;
        });

        var status = client.GetStatus();

        Assert.Equal((EverythingState.Untrusted, fake.Handle), (status.State, status.Window));
        Assert.Equal((uint)Environment.ProcessId, checkedProcess);
        Assert.Null(status.ExecutablePath);
        Assert.False(client.RunCommandLine(status, "-s \"x\""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(status, "runcount:", FakeEverything.PickFields, 1, 10, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, fake.StateQuestions);
        Assert.Empty(fake.CopyData);
    }

    /// <summary>The state questions give the version, whether the database is loaded, and whether it is busy.</summary>
    [Fact]
    public void GetStatus_ReadsVersionLoadingAndBusy()
    {
        using var fake = new FakeEverything { Loaded = 0, Busy = 1 };
        using var client = NewClient(fake);

        var loading = client.GetStatus();
        Assert.Equal((EverythingState.Loading, "1.4.1.1032", true), (loading.State, loading.Version, loading.IsBusy));
        Assert.Equal((fake.Instance, fake.Handle, (uint)Environment.ProcessId), (loading.Instance, loading.Window, loading.ProcessId));

        fake.Loaded = 1;
        fake.Busy = 0;
        var ready = client.GetStatus();
        Assert.Equal((EverythingState.Ready, false), (ready.State, ready.IsBusy));
        Assert.Equal(Trusted.ExecutablePath, ready.ExecutablePath);
    }

    /// <summary>A window that does not answer within the state deadline is "not responding", reported in about a second, not hung on.</summary>
    [Fact]
    public void GetStatus_AHungWindow_IsNotResponding()
    {
        using var fake = new FakeEverything();
        using var client = NewClient(fake);
        fake.Hang(TimeSpan.FromSeconds(2));

        var watch = Stopwatch.StartNew();
        var status = client.GetStatus();

        Assert.Equal(EverythingState.NotResponding, status.State);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.8), $"GetStatus took {watch.Elapsed.TotalMilliseconds:0} ms.");
    }

    /// <summary>
    /// A query carries the search, fields, sort and limit, names the client's own reply window, and its reply (sent
    /// after the send returned, like Everything does) is decoded.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Query_SendsTheQuery_AndDecodesTheReply()
    {
        var opened = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        EverythingPick[] picks =
        [
            new(@"C:\BC-TEST\a.txt", false, 12, opened.AddDays(-1), 2, opened),
            new(@"C:\BC-TEST\folder", true, null, null, 1, opened.AddMinutes(-5)),
        ];
        using var fake = new FakeEverything { Answer = _ => (FakeEverything.Reply(picks), TimeSpan.Zero) };
        using var client = NewClient(fake);

        var list = await client.QueryAsync(client.GetStatus(), "runcount: path:\"BC-TEST\"", FakeEverything.PickFields,
            EverythingIpc.SortDateRunDescending, 200, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var query = Assert.Single(fake.Queries);
        Assert.Equal("runcount: path:\"BC-TEST\"", query.Search);
        Assert.Equal((FakeEverything.PickFields, EverythingIpc.SortDateRunDescending, 200u, 0u, 0u),
            (query.RequestFlags, query.Sort, query.MaxResults, query.Offset, query.SearchFlags));
        Assert.NotEqual(0, query.ReplyWindow);
        Assert.NotEqual(fake.Handle, query.ReplyWindow);
        Assert.Equal(
            [new EverythingItem(@"C:\BC-TEST\a.txt", false, 12, opened.AddDays(-1), null, 2, opened),
             new EverythingItem(@"C:\BC-TEST\folder", true, null, null, null, 1, opened.AddMinutes(-5))],
            list.Items);
    }

    /// <summary>
    /// Everything runs one query per reply window, so a newer query cancels the older one at once; the older reply
    /// arriving late carries the old id and is dropped — it never completes a query pending then.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Query_ANewerQueryCancelsTheOlder_AndALateReplyIsDropped()
    {
        var slowPick = new EverythingPick(@"C:\BC-TEST\slow.txt", false, 1, null, 1, null);
        var fastPick = new EverythingPick(@"C:\BC-TEST\fast.txt", false, 1, null, 1, null);
        using var fake = new FakeEverything
        {
            Answer = q => q.Search switch
            {
                "slow" => (FakeEverything.Reply([slowPick]), TimeSpan.FromMilliseconds(300)),
                "fast" => (FakeEverything.Reply([fastPick]), TimeSpan.Zero),
                _ => (null, TimeSpan.Zero),
            },
        };
        using var client = NewClient(fake);
        var status = client.GetStatus();
        var token = TestContext.Current.CancellationToken;

        var slow = client.QueryAsync(status, "slow", FakeEverything.PickFields, 1, 10, TimeSpan.FromSeconds(10), token);
        var fast = await client.QueryAsync(status, "fast", FakeEverything.PickFields, 1, 10, TimeSpan.FromSeconds(10), token);

        Assert.Equal(fastPick.FullPath, Assert.Single(fast.Items).FullPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

        // The slow reply lands while this query waits: a wrong id must not complete it.
        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(status, "silent", FakeEverything.PickFields, 1, 10, TimeSpan.FromSeconds(1.5), token));
        Assert.Contains(fake.Queries.Single(q => q.Search == "slow").ReplyId, fake.SentReplies);
    }

    /// <summary>No reply before the deadline (Everything loading or updating its index) is a timeout, not a hang.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Query_WithoutAReply_TimesOut()
    {
        using var fake = new FakeEverything { Answer = _ => (null, TimeSpan.Zero) };
        using var client = NewClient(fake);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(client.GetStatus(), "runcount:", FakeEverything.PickFields, 1, 10, TimeSpan.FromMilliseconds(300)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"The timeout took {watch.Elapsed.TotalMilliseconds:0} ms.");
    }

    /// <summary>
    /// A refused query is "unavailable", a status that is not ready (not running, loading) is refused before
    /// anything is sent, and a reply that cannot be decoded is a <see cref="FormatException"/>.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Query_RefusedNotReadyOrGarbled_Fails()
    {
        using var fake = new FakeEverything { AcceptQueries = false };
        using var client = NewClient(fake);
        var timeout = TimeSpan.FromSeconds(2);

        await Assert.ThrowsAsync<EverythingUnavailableException>(() => client.QueryAsync(client.GetStatus(), "runcount:", FakeEverything.PickFields, 1, 10, timeout));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(EverythingStatus.NotRunning, "runcount:", FakeEverything.PickFields, 1, 10, timeout));
        fake.Loaded = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync(client.GetStatus(), "runcount:", FakeEverything.PickFields, 1, 10, timeout));
        Assert.Single(fake.Queries); // only the refused one reached the window

        fake.Loaded = 1;
        fake.AcceptQueries = true;
        fake.Answer = _ => (new byte[7], TimeSpan.Zero);
        await Assert.ThrowsAsync<FormatException>(() => client.QueryAsync(client.GetStatus(), "runcount:", FakeEverything.PickFields, 1, 10, timeout));
    }

    /// <summary>A command line goes to a trusted window as the show command plus NUL-terminated UTF-8.</summary>
    [Fact]
    public void RunCommandLine_SendsUtf8ToATrustedWindow()
    {
        using var fake = new FakeEverything();
        using var client = NewClient(fake);
        const string line = "-s \"\\\"C:\\BC-TEST\\שלום.txt\\\"\"";

        Assert.True(client.RunCommandLine(client.GetStatus(), line));

        var (command, data) = Assert.Single(fake.CopyData);
        Assert.Equal((nuint)EverythingIpc.CopyDataCommandLineUtf8, command);
        Assert.Equal(EverythingIpc.EncodeCommandLine(1, line), data);
        Assert.False(client.RunCommandLine(EverythingStatus.NotRunning, line));
    }

    /// <summary>Disposing the client cancels a pending query, and a disposed client refuses new ones.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Dispose_CancelsThePendingQuery()
    {
        using var fake = new FakeEverything { Answer = _ => (null, TimeSpan.Zero) };
        var client = NewClient(fake);
        var status = client.GetStatus();
        var pending = client.QueryAsync(status, "runcount:", FakeEverything.PickFields, 1, 10, TimeSpan.FromSeconds(30));

        client.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.QueryAsync(status, "runcount:", FakeEverything.PickFields, 1, 10, TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(() => client.RunCommandLine(status, "-s x"));
    }

    /// <summary>A client for a fake, trusting its owner.</summary>
    /// <param name="fake">The fake Everything.</param>
    /// <returns>The client (dispose it).</returns>
    internal static EverythingClient NewClient(FakeEverything fake) => new([fake.Instance], _ => Trusted);
}

/// <summary>
/// Tests for <see cref="EverythingIntegration"/>: live picks while Everything is ready, the saved run history while
/// it is gone, loading or garbled (with the reason), and never anything from an impostor.
/// </summary>
public sealed class EverythingIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly string folder = Directory.CreateTempSubdirectory("BC-TEST-everything-").FullName;

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose() => Directory.Delete(folder, recursive: true);

    /// <summary>
    /// While Everything is ready the picks come live: one <c>runcount:</c> query with the search words as path
    /// terms, newest run first, limited to the asked count; a run count Everything leaves at 0 shows as 1.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Picks_AreLive_WhileEverythingIsReady()
    {
        EverythingPick[] picks = [new(@"C:\BC-TEST\report 2026.txt", false, 5, Opened.AddDays(-2), 0, Opened)];
        using var fake = new FakeEverything { Answer = _ => (FakeEverything.Reply(picks), TimeSpan.Zero) };
        using var integration = NewIntegration(fake, EverythingClientTests.Trusted);
        int changes = 0;
        integration.StatusChanged += (_, _) => Interlocked.Increment(ref changes);

        var result = await integration.GetPicksAsync("report 2026", 50, TestContext.Current.CancellationToken);

        Assert.Equal((EverythingPicksSource.Live, (string?)null), (result.Source, result.Problem));
        Assert.Equal([picks[0] with { RunCount = 1 }], result.Picks);
        var query = Assert.Single(fake.Queries);
        Assert.Equal(("runcount: path:\"report\" path:\"2026\"", EverythingIpc.SortDateRunDescending, 50u), (query.Search, query.Sort, query.MaxResults));
        Assert.Equal(EverythingIpc.RequestRunCount | EverythingIpc.RequestDateRun, query.RequestFlags & (EverythingIpc.RequestRunCount | EverythingIpc.RequestDateRun));
        Assert.True(integration.IsAvailable);
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// Once Everything is gone the run history it saved stands in: on a local disk a vanished file is dropped and a
    /// present one gets its size, a folder is recognized, a network path is shown without being touched, search
    /// words and the limit apply — and Everything can be started again from where it ran.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Picks_FallBackToTheSavedFile_WhenEverythingIsGone()
    {
        var fake = new FakeEverything();
        var exe = Path.Combine(folder, "Everything.exe");
        File.WriteAllText(exe, "BC-TEST stand-in");
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "BC-TEST");
        var sub = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
        const string remote = @"\\BC-TEST-nohost\share\remote.txt";
        WriteRunHistory(fake.Instance, (file, 3, Opened), (sub, 2, Opened.AddMinutes(-1)), (remote, 1, Opened.AddMinutes(-2)), (Path.Combine(folder, "gone.txt"), 9, Opened.AddMinutes(-3)));
        using var integration = NewIntegration(fake, EverythingClientTests.Trusted with { ExecutablePath = exe });
        await integration.RefreshAsync();
        fake.Dispose();

        var token = TestContext.Current.CancellationToken;
        var result = await integration.GetPicksAsync(null, 10, token);

        Assert.Equal((EverythingPicksSource.SavedFile, (string?)null), (result.Source, result.Problem));
        Assert.Equal([file, sub, remote], result.Picks.Select(p => p.FullPath));
        Assert.Equal((7L, false, 3u), (result.Picks[0].Size!.Value, result.Picks[0].IsFolder, result.Picks[0].RunCount));
        Assert.True(result.Picks[1].IsFolder);
        Assert.Null(result.Picks[2].Size);
        Assert.Null(result.Picks[2].Modified);
        Assert.Equal([remote], (await integration.GetPicksAsync("REMOTE", 10, token)).Picks.Select(p => p.FullPath));
        Assert.Equal([file, sub], (await integration.GetPicksAsync(null, 2, token)).Picks.Select(p => p.FullPath));
        Assert.Equal(EverythingState.NotRunning, integration.Status.State);
        Assert.False(integration.IsAvailable);
        Assert.True(integration.CanStart);
    }

    /// <summary>
    /// While Everything loads its index (queries would wait), or answers garbage, the saved run history stands in
    /// and the tab learns why; nothing is queried while loading.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Picks_WhileLoadingOrGarbled_ComeFromTheSavedFile_WithTheReason()
    {
        var exe = Path.Combine(folder, "Everything.exe");
        File.WriteAllText(exe, "BC-TEST stand-in");
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "BC-TEST");
        using var fake = new FakeEverything { Loaded = 0 };
        WriteRunHistory(fake.Instance, (file, 1, Opened));
        using var integration = NewIntegration(fake, EverythingClientTests.Trusted with { ExecutablePath = exe });
        var token = TestContext.Current.CancellationToken;

        var loading = await integration.GetPicksAsync(null, 10, token);
        Assert.Equal((EverythingPicksSource.SavedFile, "Everything is still loading its index"), (loading.Source, loading.Problem));
        Assert.Equal([file], loading.Picks.Select(p => p.FullPath));
        Assert.Empty(fake.Queries);

        fake.Loaded = 1;
        fake.Answer = _ => (new byte[3], TimeSpan.Zero);
        var garbled = await integration.GetPicksAsync(null, 10, token);
        Assert.Equal((EverythingPicksSource.SavedFile, "Everything's answer could not be read"), (garbled.Source, garbled.Problem));
        Assert.Single(fake.Queries);
    }

    /// <summary>
    /// A window whose owner is not voidtools Everything is never queried, its claimed folder is never read, and the
    /// tab is not offered for it.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AnImpostor_IsNeverQueried_AndItsFilesAreNeverRead()
    {
        using var fake = new FakeEverything();
        WriteRunHistory(fake.Instance, (Path.Combine(folder, "bait.txt"), 1, Opened));
        var impostor = new EverythingTrust(false, Path.Combine(folder, "Everything.exe"), "BC-TEST impostor", "it is signed by BC-TEST impostor, not voidtools");
        using var integration = NewIntegration(fake, impostor);

        var result = await integration.GetPicksAsync(null, 10, TestContext.Current.CancellationToken);

        Assert.Equal((EverythingPicksSource.None, "the program answering as Everything is not voidtools Everything"), (result.Source, result.Problem));
        Assert.Empty(result.Picks);
        Assert.Empty(fake.Queries);
        Assert.Equal(EverythingState.Untrusted, integration.Status.State);
        Assert.False(integration.IsAvailable);
        Assert.False(integration.CanStart);
    }

    /// <summary>An integration over a fake, with no installation (the registry is never read in tests).</summary>
    /// <param name="fake">The fake Everything.</param>
    /// <param name="trust">The owner verdict the fake gets.</param>
    /// <returns>The integration (dispose it).</returns>
    private static EverythingIntegration NewIntegration(FakeEverything fake, EverythingTrust trust) =>
        new(new EverythingClient([fake.Instance], _ => trust), () => EverythingInstallation.NotFound);

    /// <summary>Writes the run history a portable Everything of that instance saves next to its executable.</summary>
    /// <param name="instance">The instance name (it names the file).</param>
    /// <param name="rows">Path, run count and last run of each row.</param>
    private void WriteRunHistory(string instance, params (string Path, int Count, DateTimeOffset LastRun)[] rows)
    {
        var text = new StringBuilder("Filename,Run Count,Last Run Date\r\n");
        foreach (var (path, count, lastRun) in rows)
        {
            text.Append('"').Append(path.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\",")
                .Append(count.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(lastRun.ToFileTime().ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        File.WriteAllText(Path.Combine(folder, $"Run History-{instance}.csv"), text.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>
/// Tests for <see cref="EverythingOwnerVerifier"/>: only a voidtools-signed <c>Everything*.exe</c> passes — another
/// name, an unsigned copy, another publisher's signature and an unreadable file do not.
/// </summary>
public sealed class EverythingOwnerVerifierTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("BC-TEST-everything-trust-").FullName;

    /// <summary>Deletes the temp folder.</summary>
    public void Dispose() => Directory.Delete(folder, recursive: true);

    /// <summary>Any other name is refused before the file is even opened; so is this test process.</summary>
    [Fact]
    public void AnotherName_IsRefused()
    {
        Assert.Contains("not Everything.exe", EverythingOwnerVerifier.VerifyFile(@"C:\BC-TEST\missing\impostor.exe").Reason, StringComparison.Ordinal);
        Assert.False(EverythingOwnerVerifier.VerifyFile(@"C:\BC-TEST\missing\Everything.dll").IsTrusted);
        Assert.False(EverythingOwnerVerifier.VerifyProcess((uint)Environment.ProcessId).IsTrusted);
        Assert.Throws<ArgumentException>(() => EverythingOwnerVerifier.VerifyFile(" "));
    }

    /// <summary>A file named Everything.exe without a valid signature (this test's own executable), or missing, is refused.</summary>
    [Fact]
    public void AnUnsignedOrMissingEverythingExe_IsRefused()
    {
        var copy = Path.Combine(folder, "Everything.exe");
        File.Copy(Environment.ProcessPath!, copy);

        var unsigned = EverythingOwnerVerifier.VerifyFile(copy);

        Assert.False(unsigned.IsTrusted);
        Assert.Contains("signature", unsigned.Reason, StringComparison.Ordinal);
        Assert.Equal("its executable could not be read", EverythingOwnerVerifier.VerifyFile(Path.Combine(folder, "sub", "Everything.exe")).Reason);
    }

    /// <summary>
    /// A validly signed file from another publisher (the .NET runtime, signed by Microsoft) renamed to Everything.exe
    /// is refused by its signer.
    /// </summary>
    [Fact]
    public void AnotherPublishersSignature_IsRefused()
    {
        var copy = Path.Combine(folder, "Everything64.exe");
        File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll"), copy);

        var trust = EverythingOwnerVerifier.VerifyFile(copy);

        Assert.False(trust.IsTrusted);
        Assert.SkipWhen(trust.Organization is null, $"This runtime's coreclr.dll is not Authenticode-signed here ({trust.Reason}).");
        Assert.Equal("Microsoft Corporation", trust.Organization);
        Assert.Contains("not voidtools", trust.Reason, StringComparison.Ordinal);
    }
}

/// <summary>
/// Tests for <see cref="EverythingLocator"/>'s choice among uninstall entries: every hint to the executable is
/// used, and other products or untrusted files never count.
/// </summary>
public sealed class EverythingLocatorTests
{
    /// <summary>The executable is found through the install folder (quoted), the display icon (with index) or the uninstaller's folder.</summary>
    [Fact]
    public void Resolve_UsesEveryHint()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Everything\Everything.exe",
            @"D:\Apps\Everything 1.5a\Everything.exe",
            @"E:\Tools\Everything\Everything.exe",
            @"F:\Everything\Everything.exe",
        };
        (EverythingLocator.UninstallEntry Entry, string Expected)[] cases =
        [
            (new("Everything", "Everything 1.4.1.1032 (x64)", "1.4.1.1032", "\"C:\\Program Files\\Everything\\\"", null, null), @"C:\Program Files\Everything\Everything.exe"),
            (new("{BC-TEST-MSI}", "Everything 1.5.0.1423b (x64)", "1.5.0.1423b", null, "\"D:\\Apps\\Everything 1.5a\\Everything.exe\",0", null), @"D:\Apps\Everything 1.5a\Everything.exe"),
            (new("{BC-TEST-MSI-2}", "Everything 1.4.1.1026 (x86)", "1.4.1.1026", null, @"F:\Everything\Everything.exe, -101", null), @"F:\Everything\Everything.exe"),
            (new("Everything", "Everything", "1.4", null, null, "\"E:\\Tools\\Everything\\Uninstall.exe\" /S"), @"E:\Tools\Everything\Everything.exe"),
        ];

        foreach (var (entry, expected) in cases)
        {
            var found = EverythingLocator.Resolve([entry], present.Contains, _ => true);
            Assert.Equal(new EverythingInstallation(true, expected, entry.DisplayVersion, $"installed ({entry.KeyName})"), found);
        }
    }

    /// <summary>
    /// Entries of other products (even with an Everything.exe in reach), missing files and untrusted executables are
    /// skipped; the first entry that passes wins.
    /// </summary>
    [Fact]
    public void Resolve_SkipsOtherProductsAndUntrustedFiles()
    {
        const string real = @"C:\Program Files\Everything\Everything.exe";
        const string fake = @"C:\BC-TEST\Lookalike\Everything.exe";
        EverythingLocator.UninstallEntry[] entries =
        [
            new("Notepad++", "Notepad++ (64-bit x64)", "8.6", @"C:\Program Files\Everything", null, null),
            new("EverythingToolbar", "EverythingToolbar", "1.3", @"C:\Program Files\EverythingToolbar", null, null),
            new("Lookalike", "Everything Lookalike", "1.0", @"C:\BC-TEST\Lookalike", null, null),
            new("Everything", "Everything 1.4.1.1032 (x64)", "1.4.1.1032", @"C:\Program Files\Everything", null, null),
            new("Everything 1.5a", "Everything 1.5.0.1383a (x64)", "1.5.0.1383a", @"C:\Program Files\Everything 1.5a", null, null),
        ];
        var present = new HashSet<string>([real, fake, @"C:\Program Files\Everything 1.5a\Everything.exe"], StringComparer.OrdinalIgnoreCase);

        var found = EverythingLocator.Resolve(entries, present.Contains, path => !path.Equals(fake, StringComparison.OrdinalIgnoreCase));

        Assert.Equal((true, real, "1.4.1.1032"), (found.IsInstalled, found.ExecutablePath, found.Version));
        Assert.Same(EverythingInstallation.NotFound, EverythingLocator.Resolve(entries, present.Contains, _ => false));
        Assert.Same(EverythingInstallation.NotFound, EverythingLocator.Resolve([], present.Contains, _ => true));
    }
}

/// <summary>
/// "Show in Everything" hands Everything a command line; Everything splits it with the Windows rules, so the quoted
/// search must come back as exactly one argument (checked with <c>CommandLineToArgvW</c>, the reference parser).
/// </summary>
public sealed class EverythingCommandLineTests
{
    /// <summary>Paths with spaces, drive roots, trailing separators, UNC and non-ASCII names survive the round trip.</summary>
    /// <param name="path">A path to show.</param>
    [Theory]
    [InlineData(@"C:\BC-TEST\a b.txt")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\BC-TEST\dir\")]
    [InlineData(@"\\BC-TEST-server\share\x y\")]
    [InlineData(@"C:\BC-TEST\שלום עולם (1).txt")]
    [InlineData(@"C:\BC-TEST\a\\b c")]
    public void ShowCommandLine_IsOneArgument(string path)
    {
        var search = EverythingQuery.ForPath(path);
        var arguments = Split("Everything.exe " + EverythingQuery.ShowCommandLine(search));
        Assert.Equal(["Everything.exe", "-s", search], arguments);
    }

    /// <summary>Splits a command line like Windows programs do.</summary>
    /// <param name="commandLine">The command line.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The line could not be split.</exception>
    private static string[] Split(string commandLine)
    {
        nint argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary><c>CommandLineToArgvW</c>: the shell's reference command-line splitter.</summary>
    /// <param name="lpCmdLine">The command line.</param>
    /// <param name="pNumArgs">Receives the argument count.</param>
    /// <returns>An array of string pointers, freed with <see cref="LocalFree"/>; 0 on failure.</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    /// <summary>Frees the array <see cref="CommandLineToArgvW"/> returned.</summary>
    /// <param name="hMem">The array.</param>
    /// <returns>0 on success.</returns>
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint hMem);
}

/// <summary>
/// End to end against a real Everything (opt-in: set <see cref="ExecutableVariable"/> to a portable
/// <c>Everything.exe</c>, 1.4 or 1.5): a private named instance indexes only a BC-TEST folder, two files get run
/// counts through the same IPC the result list uses, and the integration reads them live, then — after the instance
/// exited — from the run history it saved. The user's own Everything is never contacted; the instance shows no
/// window or tray icon and keeps its files in the temp folder.
/// </summary>
public sealed class RealEverythingTests
{
    /// <summary>Environment variable with the path of a portable Everything.exe to test against.</summary>
    public const string ExecutableVariable = "BETTERCLIPBOARD_EVERYTHING_EXE";

    /// <summary><c>EVERYTHING_IPC_EXIT</c>: asks the instance to exit.</summary>
    private const uint ExitCommand = 4;

    /// <summary><c>EVERYTHING_IPC_SAVE_RUN_HISTORY</c>: writes the run history file now (answers 1).</summary>
    private const uint SaveRunHistoryCommand = 408;

    /// <summary><c>EVERYTHING_IPC_COPYDATA_INC_RUN_COUNTW</c>: what a pick in the result list does to a path's run count.</summary>
    private const uint IncrementRunCount = 24;

    /// <summary>Picks are read live with run counts and dates, filtered by words, then from the saved file after exit.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task PrivateInstance_PicksLive_ThenFromTheSavedFile()
    {
        var exe = Environment.GetEnvironmentVariable(ExecutableVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(exe) || !File.Exists(exe), $"Set {ExecutableVariable} to a portable Everything.exe to run this.");

        var work = Directory.CreateTempSubdirectory("BC-TEST-everything-real-").FullName;
        var instance = FakeEverything.NewInstanceName();
        var token = TestContext.Current.CancellationToken;
        Process? everything = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(work, "bin")).FullName;
            var tree = Directory.CreateDirectory(Path.Combine(work, "tree")).FullName;
            var copy = Path.Combine(bin, "Everything.exe");
            File.Copy(exe!, copy);
            var a = Path.Combine(tree, "a.txt");
            var b = Path.Combine(tree, "b note.txt");
            File.WriteAllText(a, "BC-TEST a");
            File.WriteAllText(b, "BC-TEST b, longer");
            var config = WriteConfig(work, bin, tree);
            Assert.True(EverythingOwnerVerifier.VerifyFile(copy).IsTrusted, EverythingOwnerVerifier.VerifyFile(copy).Reason);

            everything = Process.Start(new ProcessStartInfo(copy)
            {
                // ShellExecute: the instance must not inherit this test host's handles (CLAUDE.md §4).
                UseShellExecute = true,
                ArgumentList = { "-instance", instance, "-startup", "-config", config, "-db", Path.Combine(work, "test.db") },
                WorkingDirectory = bin,
            });
            Assert.NotNull(everything);
            using var client = new EverythingClient([instance]);
            using var integration = new EverythingIntegration(client, () => EverythingInstallation.NotFound);
            var status = await WaitAsync(() => Task.FromResult(client.GetStatus()), s => s.State == EverythingState.Ready, TimeSpan.FromSeconds(30));
            Assert.True(status.Trust?.IsTrusted, status.Trust?.Reason);

            // A folder index can report ready a moment before its files are searchable; 1.4 ignores run counts of
            // paths it has not indexed.
            await WaitAsync(
                async () => (await client.QueryAsync(status, $"path:wfn:\"{b}\"", EverythingIpc.RequestFullPath, EverythingIpc.SortNameAscending, 10, TimeSpan.FromSeconds(2), token)).Total,
                total => total == 1,
                TimeSpan.FromSeconds(20));

            using (var sender = new MessageWindowThread("BCTEST.RunCount"))
            {
                Assert.Equal(1, SendRunCount(status.Window, sender.Handle, a));
                Assert.Equal(2, SendRunCount(status.Window, sender.Handle, a));
                await Task.Delay(50, token); // b's run date must be later than a's
                Assert.Equal(1, SendRunCount(status.Window, sender.Handle, b));
            }

            var live = await integration.GetPicksAsync(null, 50, token);
            Assert.Equal((EverythingPicksSource.Live, (string?)null), (live.Source, live.Problem));
            Assert.Equal([b, a], live.Picks.Select(p => p.FullPath));
            Assert.Equal([1u, 2u], live.Picks.Select(p => p.RunCount));
            Assert.All(live.Picks, p => Assert.NotNull(p.LastOpened));
            Assert.Equal([a], (await integration.GetPicksAsync("A.TXT", 50, token)).Picks.Select(p => p.FullPath));
            Assert.Equal([b], (await integration.GetPicksAsync("b note", 50, token)).Picks.Select(p => p.FullPath));

            // Everything saves the run history when a search window closes or it exits [docs: Everything_SaveRunHistory].
            // 1.5 did at every exit (verified); 1.4.1.1032 without a search window ever open (this instance) saved only
            // on request — exit, -exit, WM_CLOSE and 150 s of waiting wrote nothing — so here the request stands in for
            // the window a real pick happens in.
            if (status.Version?.StartsWith("1.4.", StringComparison.Ordinal) == true)
            {
                Assert.Equal(1, NativeMethods.SendMessageTimeout(status.Window, EverythingIpc.WmIpc, (nint)SaveRunHistoryCommand, 0, NativeMethods.SMTO_ABORTIFHUNG, 2000, out nint wrote) == 0 ? 0 : wrote);
            }

            NativeMethods.SendMessageTimeout(status.Window, EverythingIpc.WmIpc, (nint)ExitCommand, 0, NativeMethods.SMTO_ABORTIFHUNG, 2000, out _);
            Assert.True(everything.WaitForExit(15_000), "The private instance did not exit.");

            var saved = await integration.GetPicksAsync(null, 50, token);
            Assert.True(saved.Source == EverythingPicksSource.SavedFile,
                $"No saved run history was read ({saved.Source}); files after exit: " +
                string.Join(", ", Directory.GetFiles(work, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(work, f))));
            Assert.Equal(EverythingState.NotRunning, integration.Status.State);
            Assert.Equal([b, a], saved.Picks.Select(p => p.FullPath));
            Assert.Equal([1u, 2u], saved.Picks.Select(p => p.RunCount));
            Assert.Equal([new FileInfo(b).Length, new FileInfo(a).Length], saved.Picks.Select(p => p.Size!.Value));
            Assert.Equal(live.Picks.Select(p => p.LastOpened), saved.Picks.Select(p => p.LastOpened));
        }
        finally
        {
            if (everything is { HasExited: false })
            {
                everything.Kill();
                everything.WaitForExit(5000);
            }

            everything?.Dispose();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A file still held by an exiting instance: the temp folder is cleaned up by the system later.
            }
        }
    }

    /// <summary>
    /// Writes the instance's settings: settings next to the copy (<c>app_data=0</c>, so nothing reaches
    /// <c>%APPDATA%\Everything</c>), no tray icon, no update check, no volumes — only the BC-TEST folder.
    /// </summary>
    /// <param name="work">The work folder (gets the config).</param>
    /// <param name="bin">The copy's folder (gets Everything.ini).</param>
    /// <param name="tree">The folder to index.</param>
    /// <returns>The config path for <c>-config</c>.</returns>
    private static string WriteConfig(string work, string bin, string tree)
    {
        var utf8 = new UTF8Encoding(false);

        // app_data is read only from the Everything.ini next to the executable.
        File.WriteAllText(Path.Combine(bin, "Everything.ini"), "[Everything]\r\napp_data=0\r\n", utf8);
        var config = Path.Combine(work, "test.ini");
        File.WriteAllText(config, string.Join("\r\n",
        [
            "[Everything]",
            "run_as_admin=0",
            "show_tray_icon=0",
            "check_for_updates_on_startup=0",
            "language=1033",
            "auto_include_fixed_volumes=0",
            "auto_include_removable_volumes=0",
            "auto_include_fixed_refs_volumes=0",
            "auto_include_removable_refs_volumes=0",
            "ntfs_volume_paths=",
            "ntfs_volume_includes=",

            // Inside quotes a backslash escapes (Everything's ini format), so every backslash is doubled.
            "folders=\"" + tree.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"",
            "folder_monitor_changes=1",
            "index_size=1",
            "index_date_modified=1",
            "index_attributes=1",
            string.Empty,
        ]), utf8);
        return config;
    }

    /// <summary>Increments a path's run count the way a pick in Everything's result list does.</summary>
    /// <param name="everything">The instance's IPC window.</param>
    /// <param name="sender">A window of ours (everything_ipc.h asks for one as <c>wParam</c>).</param>
    /// <param name="path">The indexed path.</param>
    /// <returns>The new run count (0 when Everything refused).</returns>
    private static long SendRunCount(nint everything, nint sender, string path)
    {
        var bytes = Encoding.Unicode.GetBytes(path + "\0");
        nint data = Marshal.AllocHGlobal(bytes.Length);
        nint copy = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.COPYDATASTRUCT>());
        try
        {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            Marshal.StructureToPtr(new NativeMethods.COPYDATASTRUCT { dwData = IncrementRunCount, cbData = (uint)bytes.Length, lpData = data }, copy, false);
            return NativeMethods.SendMessageTimeout(everything, NativeMethods.WM_COPYDATA, sender, copy, NativeMethods.SMTO_ABORTIFHUNG, 2000, out nint result) == 0 ? 0 : result;
        }
        finally
        {
            Marshal.FreeHGlobal(copy);
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Polls until a value satisfies a condition.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="read">Reads the value (Everything's "not ready" failures count as "not yet").</param>
    /// <param name="done">The condition.</param>
    /// <param name="limit">Longest wait.</param>
    /// <returns>The first value that satisfied it.</returns>
    /// <exception cref="TimeoutException">It never did within <paramref name="limit"/>.</exception>
    private static async Task<T> WaitAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var value = await read();
                if (done(value))
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or EverythingUnavailableException)
            {
                // Not ready yet: keep polling until the limit.
            }

            if (watch.Elapsed > limit)
            {
                throw new TimeoutException($"Not done after {limit.TotalSeconds:0} s.");
            }

            await Task.Delay(100);
        }
    }
}

/// <summary>
/// A stand-in for voidtools Everything's IPC window, inside the test process: a hidden top-level window with the
/// class of a private instance name, answering state questions and queries the way Everything does — replies go to
/// the query's reply window as a separate <c>WM_COPYDATA</c>, after the query's own send returned. Nothing outside
/// the test ever sees it, and no real Everything is involved.
/// </summary>
internal sealed class FakeEverything : IDisposable
{
    /// <summary>The fields the Everything tab asks for (and <see cref="Reply"/> lays out).</summary>
    public const uint PickFields = EverythingIpc.RequestFullPath | EverythingIpc.RequestSize | EverythingIpc.RequestDateModified
                                   | EverythingIpc.RequestRunCount | EverythingIpc.RequestDateRun;

    private readonly MessageWindowThread window;
    private volatile int loaded = 1;
    private volatile int busy;
    private volatile bool acceptQueries = true;
    private volatile Func<ReceivedQuery, (byte[]? Reply, TimeSpan Delay)> answer = _ => (Reply([]), TimeSpan.Zero);
    private int stateQuestions;

    /// <summary>Creates the window for a fresh private instance name.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The window could not be created.</exception>
    public FakeEverything()
    {
        Instance = NewInstanceName();
        window = new MessageWindowThread("FakeEverything", messageOnly: false, className: EverythingIpc.WindowClassFor(Instance)) { MessageHandler = OnMessage };
    }

    /// <summary>The private instance name (its window class is <c>EVERYTHING_TASKBAR_NOTIFICATION_(name)</c>).</summary>
    public string Instance { get; }

    /// <summary>The fake IPC window.</summary>
    public nint Handle => window.Handle;

    /// <summary>The answer to <c>IS_DB_LOADED</c> (1 = loaded).</summary>
    public int Loaded { get => loaded; set => loaded = value; }

    /// <summary>The answer to <c>IS_DB_BUSY</c>.</summary>
    public int Busy { get => busy; set => busy = value; }

    /// <summary>Whether queries are accepted (message result 1); refused queries are never answered.</summary>
    public bool AcceptQueries { get => acceptQueries; set => acceptQueries = value; }

    /// <summary>The reply bytes for a query (<see langword="null"/> = never reply) and how long to wait before sending them.</summary>
    public Func<ReceivedQuery, (byte[]? Reply, TimeSpan Delay)> Answer { get => answer; set => answer = value; }

    /// <summary>Every query received, in order.</summary>
    public ConcurrentQueue<ReceivedQuery> Queries { get; } = new();

    /// <summary>Every <c>WM_COPYDATA</c> received: its command and bytes.</summary>
    public ConcurrentQueue<(nuint Command, byte[] Data)> CopyData { get; } = new();

    /// <summary>Reply ids of the replies delivered so far.</summary>
    public ConcurrentQueue<uint> SentReplies { get; } = new();

    /// <summary>How many state questions (<c>EVERYTHING_WM_IPC</c>) were asked.</summary>
    public int StateQuestions => Volatile.Read(ref stateQuestions);

    /// <summary>A private instance name no real Everything uses.</summary>
    /// <returns>The name.</returns>
    public static string NewInstanceName() => $"BCTEST-{Guid.NewGuid():N}";

    /// <summary>Lays out a <c>LIST2</c> reply with <see cref="PickFields"/> for the given picks (an unknown size as −1, unknown dates as 0).</summary>
    /// <param name="picks">The results.</param>
    /// <returns>The reply bytes.</returns>
    public static byte[] Reply(IReadOnlyList<EverythingPick> picks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)picks.Count);
        writer.Write((uint)picks.Count);
        writer.Write(0u);
        writer.Write(PickFields);
        writer.Write(EverythingIpc.SortDateRunDescending);
        var fields = picks.Select(Fields).ToList();
        uint dataAt = (uint)(20 + picks.Count * 8);
        for (int i = 0; i < picks.Count; i++)
        {
            writer.Write(picks[i].IsFolder ? EverythingIpc.ItemFolder : 0u);
            writer.Write(dataAt);
            dataAt += (uint)fields[i].Length;
        }

        fields.ForEach(writer.Write);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Blocks the window's thread (a hung Everything) and returns once it is blocked.</summary>
    /// <param name="duration">How long it stays blocked.</param>
    public void Hang(TimeSpan duration)
    {
        // A task, not a disposable event: the window thread may still be inside Set when the wait returns.
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Post(() =>
        {
            blocked.TrySetResult();
            Thread.Sleep(duration);
        });
        blocked.Task.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Destroys the window (Everything exited).</summary>
    public void Dispose() => window.Dispose();

    /// <summary>One pick's fields in request-bit order.</summary>
    /// <param name="pick">The pick.</param>
    /// <returns>The field bytes.</returns>
    private static byte[] Fields(EverythingPick pick)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)pick.FullPath.Length);
        writer.Write(Encoding.Unicode.GetBytes(pick.FullPath));
        writer.Write((ushort)0);
        writer.Write(pick.Size ?? -1L);
        writer.Write(pick.Modified?.ToFileTime() ?? 0L);
        writer.Write(pick.RunCount);
        writer.Write(pick.LastOpened?.ToFileTime() ?? 0L);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The window's message hook: answers state questions, records copy data, schedules query replies.</summary>
    /// <param name="msg">Message.</param>
    /// <param name="wParam">State question, or the sender's window.</param>
    /// <param name="lParam">A <c>COPYDATASTRUCT</c> pointer for <c>WM_COPYDATA</c>.</param>
    /// <returns>The answer; <see langword="null"/> for messages Everything would not handle.</returns>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == EverythingIpc.WmIpc)
        {
            Interlocked.Increment(ref stateQuestions);
            return (uint)wParam switch
            {
                EverythingIpc.GetMajorVersion => 1,
                EverythingIpc.GetMinorVersion => 4,
                EverythingIpc.GetRevision => 1,
                EverythingIpc.GetBuildNumber => 1032,
                EverythingIpc.IsDbLoaded => Loaded,
                EverythingIpc.IsDbBusy => Busy,
                _ => 0,
            };
        }

        if (msg != NativeMethods.WM_COPYDATA || lParam == 0)
        {
            return null;
        }

        // The payload is only valid during this call: copy it now.
        var header = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
        var data = new byte[header.cbData];
        if (data.Length > 0)
        {
            Marshal.Copy(header.lpData, data, 0, data.Length);
        }

        CopyData.Enqueue((header.dwData, data));
        if (header.dwData != EverythingIpc.CopyDataQuery2W)
        {
            return 1;
        }

        var query = ReceivedQuery.Parse(data);
        Queries.Enqueue(query);
        if (!AcceptQueries)
        {
            return 0;
        }

        var (reply, delay) = Answer(query);
        if (reply is not null)
        {
            // Posted, so the reply goes out after this send returned — never inside it, like Everything.
            if (delay <= TimeSpan.Zero)
            {
                window.Post(() => SendReply(query, reply));
            }
            else
            {
                _ = Task.Delay(delay).ContinueWith(_ => window.Post(() => SendReply(query, reply)), TaskScheduler.Default);
            }
        }

        return 1;
    }

    /// <summary>Sends a reply to the query's reply window with its reply id, then records it.</summary>
    /// <param name="query">The query being answered.</param>
    /// <param name="reply">The reply bytes.</param>
    private void SendReply(ReceivedQuery query, byte[] reply)
    {
        nint data = Marshal.AllocHGlobal(Math.Max(1, reply.Length));
        nint copy = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.COPYDATASTRUCT>());
        try
        {
            Marshal.Copy(reply, 0, data, reply.Length);
            Marshal.StructureToPtr(new NativeMethods.COPYDATASTRUCT { dwData = query.ReplyId, cbData = (uint)reply.Length, lpData = data }, copy, false);
            NativeMethods.SendMessageTimeout(query.ReplyWindow, NativeMethods.WM_COPYDATA, Handle, copy, NativeMethods.SMTO_ABORTIFHUNG, 2000, out _);
            SentReplies.Enqueue(query.ReplyId);
        }
        finally
        {
            Marshal.FreeHGlobal(copy);
            Marshal.FreeHGlobal(data);
        }
    }
}

/// <summary>A query as the fake Everything received it.</summary>
/// <param name="ReplyWindow">Where the reply goes.</param>
/// <param name="ReplyId">The id the reply echoes.</param>
/// <param name="SearchFlags">Match flags.</param>
/// <param name="Offset">Results skipped.</param>
/// <param name="MaxResults">Most results asked for.</param>
/// <param name="RequestFlags">Fields asked for.</param>
/// <param name="Sort">Sort asked for.</param>
/// <param name="Search">The search text.</param>
internal sealed record ReceivedQuery(nint ReplyWindow, uint ReplyId, uint SearchFlags, uint Offset, uint MaxResults, uint RequestFlags, uint Sort, string Search)
{
    /// <summary>Reads a <c>QUERY2</c>: seven DWORDs, then NUL-terminated UTF-16.</summary>
    /// <param name="data">The copied payload.</param>
    /// <returns>The query.</returns>
    public static ReceivedQuery Parse(byte[] data)
    {
        uint Dword(int index) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4));
        var search = data.Length >= 30 ? Encoding.Unicode.GetString(data, 28, data.Length - 30) : string.Empty;
        return new((nint)Dword(0), Dword(1), Dword(2), Dword(3), Dword(4), Dword(5), Dword(6), search);
    }
}
