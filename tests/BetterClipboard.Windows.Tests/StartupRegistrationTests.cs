using BetterClipboard.Core.Startup;
using BetterClipboard.Windows.Shell;
using BetterClipboard.Windows.Updates;
using Microsoft.Win32;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for <see cref="StartupRegistration"/>'s reading and writing of the sign-in entries, and for reading the PC's
/// Installed-apps entry (<see cref="InstalledCopyLocator.ReadMachineInstallLocation(RegistryKey, string)"/>).
/// </summary>
/// <remarks>
/// Everything runs under two scratch keys of <c>HKCU\Software\BetterClipboard-Tests</c> that stand for the user's and the
/// PC's registry, so neither the user's real <c>Run</c> key nor <c>HKLM</c> is ever read or written (writing <c>HKLM</c>
/// would need an administrator anyway). Each test has its own keys, deleted afterwards.
/// </remarks>
public sealed class StartupRegistrationTests : IDisposable
{
    /// <summary>The running copy in every case.</summary>
    private const string Exe = @"C:\BC-TEST\Program Files\BetterClipboard\BetterClipboard.exe";

    /// <summary>The <c>Run</c> key under each root.</summary>
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Windows' marks for that key's entries under each root.</summary>
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>An "on" mark as Task Manager writes it: 02, then eight bytes of time, then zeros.</summary>
    private static readonly byte[] OnMark = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>An "off" mark: an odd first byte, then the time it was turned off.</summary>
    private static readonly byte[] OffMark = [3, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01];

    /// <summary>The scratch key of this test; both roots live under it.</summary>
    private readonly string scratch = $@"Software\BetterClipboard-Tests\{Guid.NewGuid():N}";

    /// <summary>The key that stands for <c>HKEY_CURRENT_USER</c>.</summary>
    private readonly RegistryKey userRoot;

    /// <summary>The key that stands for <c>HKEY_LOCAL_MACHINE</c>.</summary>
    private readonly RegistryKey machineRoot;

    /// <summary>Creates the two scratch roots.</summary>
    public StartupRegistrationTests()
    {
        userRoot = Registry.CurrentUser.CreateSubKey($@"{scratch}\user", writable: true);
        machineRoot = Registry.CurrentUser.CreateSubKey($@"{scratch}\machine", writable: true);
    }

    /// <summary>Closes the roots and deletes the scratch keys.</summary>
    public void Dispose()
    {
        userRoot.Dispose();
        machineRoot.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
    }

    /// <summary>Empty roots hold no entry, and a missing PC root changes nothing about the user's part.</summary>
    [Fact]
    public void ReadEntries_EmptyRoots_HoldNothing()
    {
        Assert.Equal(SignInEntries.None, StartupRegistration.ReadEntries(userRoot, machineRoot, _ => true));
        Assert.Equal(SignInEntries.None, StartupRegistration.ReadEntries(userRoot, null, _ => true));
    }

    /// <summary>
    /// Both entries and both marks are read from their own roots; whether the user's program exists is asked with the
    /// path as the command quotes it.
    /// </summary>
    [Fact]
    public void ReadEntries_ReadsBothEntriesAndMarks()
    {
        Set(userRoot, Run, SignInStartup.UserCommandFor(Exe));
        Set(machineRoot, Run, SignInStartup.AllUsersCommandFor(Exe));
        Set(userRoot, Approved, OffMark);
        Set(machineRoot, Approved, OnMark);
        var asked = new List<string>();

        var entries = StartupRegistration.ReadEntries(userRoot, machineRoot, path =>
        {
            asked.Add(path);
            return true;
        });

        Assert.Equal(new SignInEntries(SignInStartup.UserCommandFor(Exe), true, true, SignInStartup.AllUsersCommandFor(Exe), false), entries);
        Assert.Equal(new[] { Exe }, asked);

        // The marks the other way round, and a program that is gone.
        Set(userRoot, Approved, OnMark);
        Set(machineRoot, Approved, OffMark);
        entries = StartupRegistration.ReadEntries(userRoot, machineRoot, _ => false);
        Assert.False(entries.UserTargetExists);
        Assert.False(entries.UserDisabledInWindows);
        Assert.True(entries.AllUsersDisabledInWindows);
    }

    /// <summary>
    /// A mark without a value is still read (Windows keeps it under the name); a value that is not text, or blank,
    /// counts as no entry; variables in the program's path are expanded before asking whether it exists.
    /// </summary>
    [Fact]
    public void ReadEntries_ToleratesOddValues()
    {
        Set(userRoot, Approved, OffMark);
        using (var run = userRoot.CreateSubKey(Run, writable: true))
        {
            run.SetValue("BetterClipboard", 42, RegistryValueKind.DWord);
        }

        var entries = StartupRegistration.ReadEntries(userRoot, machineRoot, _ => true);
        Assert.Null(entries.UserCommand);
        Assert.True(entries.UserDisabledInWindows);

        Set(userRoot, Run, "   ");
        Assert.Null(StartupRegistration.ReadEntries(userRoot, machineRoot, _ => true).UserCommand);

        Set(userRoot, Run, @"%SystemRoot%\BC-TEST\BetterClipboard.exe --background");
        string? asked = null;
        StartupRegistration.ReadEntries(userRoot, machineRoot, path =>
        {
            asked = path;
            return false;
        });
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, @"BC-TEST\BetterClipboard.exe"), asked);
    }

    /// <summary>Windows' marks: even first byte "on", odd "off" (3, 7, and 9 for a locked one); anything else is "on".</summary>
    [Fact]
    public void IsOffMark_ReadsTheFirstByte()
    {
        Assert.False(StartupRegistration.IsOffMark(new byte[] { 2, 0 }));
        Assert.False(StartupRegistration.IsOffMark(new byte[] { 6, 0 }));
        Assert.True(StartupRegistration.IsOffMark(new byte[] { 3, 0 }));
        Assert.True(StartupRegistration.IsOffMark(new byte[] { 7, 0 }));
        Assert.True(StartupRegistration.IsOffMark(new byte[] { 9, 0 }));
        Assert.False(StartupRegistration.IsOffMark(Array.Empty<byte>()));
        Assert.False(StartupRegistration.IsOffMark(null));
        Assert.False(StartupRegistration.IsOffMark("03"));
    }

    /// <summary>
    /// Writing, clearing and removing touch only BetterClipboard's value and mark: every other app's entry and mark stays,
    /// and a missing key is created without disturbing anything.
    /// </summary>
    [Fact]
    public void Apply_ChangesOnlyBetterClipboardsValues()
    {
        Set(userRoot, Run, "BC-TEST neighbor", @"""C:\BC-TEST\other.exe""");
        Set(userRoot, Approved, "BC-TEST neighbor", OffMark);
        Set(userRoot, Approved, OffMark);

        StartupRegistration.Apply(new SignInChange(UserEntryChange.Write, ClearUserDisabled: true, SkipAllUsers: false), Exe, userRoot);
        Assert.Equal(SignInStartup.UserCommandFor(Exe), Get(userRoot, Run));
        Assert.Null(Get(userRoot, Approved));
        Assert.Equal(@"""C:\BC-TEST\other.exe""", Get(userRoot, Run, "BC-TEST neighbor"));
        Assert.Equal(OffMark, (byte[]?)Get(userRoot, Approved, "BC-TEST neighbor"));

        StartupRegistration.Apply(new SignInChange(UserEntryChange.Remove, ClearUserDisabled: false, SkipAllUsers: true), Exe, userRoot);
        Assert.Null(Get(userRoot, Run));
        Assert.Equal(@"""C:\BC-TEST\other.exe""", Get(userRoot, Run, "BC-TEST neighbor"));

        // Keep with nothing to clear changes nothing; a removal without the key or the value is no error.
        StartupRegistration.Apply(new SignInChange(UserEntryChange.Keep, ClearUserDisabled: false, SkipAllUsers: false), Exe, userRoot);
        StartupRegistration.Apply(new SignInChange(UserEntryChange.Remove, ClearUserDisabled: true, SkipAllUsers: false), Exe, machineRoot);
        Assert.Null(machineRoot.OpenSubKey(Run));
    }

    /// <summary>
    /// The plan and the registry together: with an all-users entry for this copy, "off" removes the user's entry and
    /// declines the all-users one, and "on" brings it back without writing a second entry.
    /// </summary>
    [Fact]
    public void PlanAndApply_RoundTrip()
    {
        Set(machineRoot, Run, SignInStartup.AllUsersCommandFor(Exe));
        Set(userRoot, Run, SignInStartup.UserCommandFor(Exe));
        bool skip = false;

        var off = SignInStartup.Plan(false, StartupRegistration.ReadEntries(userRoot, machineRoot, _ => true), Exe, skip);
        StartupRegistration.Apply(off, Exe, userRoot);
        skip = off.SkipAllUsers;
        var entries = StartupRegistration.ReadEntries(userRoot, machineRoot, _ => true);
        Assert.True(skip);
        Assert.Null(entries.UserCommand);
        Assert.False(SignInStartup.StartsThisCopy(entries, Exe, skip));
        Assert.False(SignInStartup.ShouldContinueFromAllUsersEntry(entries, skip));

        var on = SignInStartup.Plan(true, entries, Exe, skip);
        StartupRegistration.Apply(on, Exe, userRoot);
        skip = on.SkipAllUsers;
        entries = StartupRegistration.ReadEntries(userRoot, machineRoot, _ => true);
        Assert.False(skip);
        Assert.Null(entries.UserCommand);
        Assert.True(SignInStartup.StartsThisCopy(entries, Exe, skip));
        Assert.True(SignInStartup.ShouldContinueFromAllUsersEntry(entries, skip));
    }

    /// <summary>The PC's Installed-apps entry is read like the user's: trimmed text, and nothing for anything else.</summary>
    [Fact]
    public void MachineInstallLocation_ReadsTheEntry_AndToleratesEverythingElse()
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard";
        Assert.Null(InstalledCopyLocator.ReadMachineInstallLocation(machineRoot, Key));
        using var entry = machineRoot.CreateSubKey(Key, writable: true);
        Assert.Null(InstalledCopyLocator.ReadMachineInstallLocation(machineRoot, Key));

        entry.SetValue(InstalledCopyLocator.InstallLocationValue, @"  C:\BC-TEST\Program Files\BetterClipboard  ", RegistryValueKind.String);
        Assert.Equal(@"C:\BC-TEST\Program Files\BetterClipboard", InstalledCopyLocator.ReadMachineInstallLocation(machineRoot, Key));

        entry.SetValue(InstalledCopyLocator.InstallLocationValue, " ", RegistryValueKind.String);
        Assert.Null(InstalledCopyLocator.ReadMachineInstallLocation(machineRoot, Key));

        entry.SetValue(InstalledCopyLocator.InstallLocationValue, 7, RegistryValueKind.DWord);
        Assert.Null(InstalledCopyLocator.ReadMachineInstallLocation(machineRoot, Key));
    }

    /// <summary>Writes BetterClipboard's value under a root.</summary>
    /// <param name="root">The scratch root.</param>
    /// <param name="keyPath">The key under it.</param>
    /// <param name="value">Text for a <c>Run</c> value, bytes for a mark.</param>
    private static void Set(RegistryKey root, string keyPath, object value) => Set(root, keyPath, "BetterClipboard", value);

    /// <summary>Writes a named value under a root, creating the key when missing.</summary>
    /// <param name="root">The scratch root.</param>
    /// <param name="keyPath">The key under it.</param>
    /// <param name="name">The value's name.</param>
    /// <param name="value">Text or bytes.</param>
    private static void Set(RegistryKey root, string keyPath, string name, object value)
    {
        using var key = root.CreateSubKey(keyPath, writable: true);
        key.SetValue(name, value, value is byte[] ? RegistryValueKind.Binary : RegistryValueKind.String);
    }

    /// <summary>Reads a value under a root.</summary>
    /// <param name="root">The scratch root.</param>
    /// <param name="keyPath">The key under it.</param>
    /// <param name="name">The value's name; BetterClipboard's by default.</param>
    /// <returns>The data, or <see langword="null"/> when the key or the value is missing.</returns>
    private static object? Get(RegistryKey root, string keyPath, string name = "BetterClipboard")
    {
        using var key = root.OpenSubKey(keyPath);
        return key?.GetValue(name);
    }
}
