using BetterClipboard.Core.Settings;
using BetterClipboard.Core.Startup;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for <see cref="SignInStartup"/>: what "Start with Windows" shows and changes when two entries can start
/// BetterClipboard at sign-in — the user's own and the one an installation made for every user of the PC — and when a
/// process that the all-users entry started steps aside.
/// </summary>
public sealed class SignInStartupTests
{
    /// <summary>The running copy in every case.</summary>
    private const string Exe = @"C:\Program Files\BetterClipboard\BetterClipboard.exe";

    /// <summary>Another copy that exists (install.ps1's copy for one user, say).</summary>
    private const string OtherExe = @"C:\Users\me\AppData\Local\Programs\BetterClipboard\BetterClipboard.exe";

    /// <summary>The user's entries a case can start from, by name.</summary>
    public enum User
    {
        /// <summary>No entry.</summary>
        None,

        /// <summary>An entry that starts this copy.</summary>
        This,

        /// <summary>An entry that starts another copy, which exists.</summary>
        Other,

        /// <summary>An entry whose program is gone.</summary>
        Stale,
    }

    /// <summary>The all-users entries a case can start from, by name.</summary>
    public enum Machine
    {
        /// <summary>No entry.</summary>
        None,

        /// <summary>An entry that starts this copy.</summary>
        This,

        /// <summary>An entry that starts another copy.</summary>
        Other,
    }

    /// <summary>
    /// Plan cases: switch position, the user's entry and its mark, the all-users entry and its mark, the setting before,
    /// then the expected change.
    /// </summary>
    public static TheoryData<bool, User, bool, Machine, bool, bool, UserEntryChange, bool, bool> PlanCases => new()
    {
        // On, nothing there: the user's own entry, as the switch always did.
        { true, User.None, false, Machine.None, false, false, UserEntryChange.Write, false, false },

        // On, the value was deleted but Windows still keeps "off" under its name: written again, it must lose the mark.
        { true, User.None, true, Machine.None, false, false, UserEntryChange.Write, true, false },

        // On, the all-users entry starts this copy: nothing written, the user's earlier "off" undone.
        { true, User.None, false, Machine.This, false, true, UserEntryChange.Keep, false, false },

        // On, the all-users entry starts this copy and the user's entry is stale: the stale one goes.
        { true, User.Stale, false, Machine.This, false, false, UserEntryChange.Remove, false, false },

        // On, the user's entry for this copy is off in Windows: kept, its mark cleared.
        { true, User.This, true, Machine.This, false, false, UserEntryChange.Keep, true, false },

        // On, the user's entry for this copy is on already: nothing to do.
        { true, User.This, false, Machine.This, false, false, UserEntryChange.Keep, false, false },

        // On, the user's entry starts another copy, which would win over the all-users entry: written for this one.
        { true, User.Other, false, Machine.This, false, false, UserEntryChange.Write, false, false },

        // On, an administrator turned the all-users entry off: the user's own entry stands in.
        { true, User.None, false, Machine.This, true, true, UserEntryChange.Write, false, false },

        // On, the all-users entry starts another copy: this copy's own entry.
        { true, User.None, false, Machine.Other, false, false, UserEntryChange.Write, false, false },

        // Off, only the user's entry: removed, the setting untouched.
        { false, User.This, false, Machine.None, false, false, UserEntryChange.Remove, false, false },

        // Off, only the all-users entry: declined for this user.
        { false, User.None, false, Machine.This, false, false, UserEntryChange.Keep, false, true },

        // Off, both: the user's entry removed and the all-users one declined.
        { false, User.This, false, Machine.This, false, false, UserEntryChange.Remove, false, true },

        // Off, the user's entry starts another copy: that copy's entry is not this switch's to remove.
        { false, User.Other, false, Machine.None, false, false, UserEntryChange.Keep, false, false },

        // Off while an administrator has the all-users entry off: declined too, or turning it back on would restart it.
        { false, User.None, false, Machine.This, true, false, UserEntryChange.Keep, false, true },

        // Off with the setting already set and no all-users entry: the setting stays as it was.
        { false, User.None, false, Machine.None, false, true, UserEntryChange.Keep, false, true },
    };

    /// <summary>Both entry formats name the program quoted, then the switches, exactly as the installers write them.</summary>
    [Fact]
    public void Commands_HaveTheInstallersFormat()
    {
        Assert.Equal($"\"{Exe}\" --background", SignInStartup.UserCommandFor(Exe));
        Assert.Equal($"\"{Exe}\" --background --all-users-startup", SignInStartup.AllUsersCommandFor(Exe));
        Assert.Throws<ArgumentException>(() => SignInStartup.UserCommandFor(" "));
        Assert.Throws<ArgumentException>(() => SignInStartup.AllUsersCommandFor(""));
    }

    /// <summary>An entry names this copy when its path appears in the command, in any letter case; nothing names it otherwise.</summary>
    [Fact]
    public void Names_IsTheSwitchesOldRule()
    {
        Assert.True(SignInStartup.Names(SignInStartup.UserCommandFor(Exe), Exe));
        Assert.True(SignInStartup.Names($"\"{Exe.ToUpperInvariant()}\" --background", Exe));
        Assert.True(SignInStartup.Names($"{Exe} --background", Exe));
        Assert.False(SignInStartup.Names(SignInStartup.UserCommandFor(OtherExe), Exe));
        Assert.False(SignInStartup.Names(null, Exe));
        Assert.False(SignInStartup.Names("  ", Exe));
        Assert.False(SignInStartup.Names(SignInStartup.UserCommandFor(Exe), ""));
    }

    /// <summary>The program of a command: quoted, unquoted with spaces up to ".exe", or the first word; nothing for nothing.</summary>
    [Fact]
    public void TargetOf_ReadsTheProgram()
    {
        Assert.Equal(Exe, SignInStartup.TargetOf(SignInStartup.AllUsersCommandFor(Exe)));
        Assert.Equal(Exe, SignInStartup.TargetOf($"  {Exe} --background"));
        Assert.Equal(@"%LOCALAPPDATA%\BC\BetterClipboard.EXE", SignInStartup.TargetOf(@"%LOCALAPPDATA%\BC\BetterClipboard.EXE --background"));
        Assert.Equal("notepad", SignInStartup.TargetOf("notepad file.txt"));
        Assert.Equal("app", SignInStartup.TargetOf("app"));
        Assert.Null(SignInStartup.TargetOf("\"C:\\unterminated --background"));
        Assert.Null(SignInStartup.TargetOf("\"\" --background"));
        Assert.Null(SignInStartup.TargetOf(null));
        Assert.Null(SignInStartup.TargetOf("   "));
    }

    /// <summary>Without any entry this copy does not start; with only the user's entry for it, it does unless Windows has it off.</summary>
    [Fact]
    public void StartsThisCopy_UserEntryOnly()
    {
        Assert.False(SignInStartup.StartsThisCopy(SignInEntries.None, Exe, skipAllUsers: false));
        Assert.True(SignInStartup.StartsThisCopy(Entries(User.This, false, Machine.None, false), Exe, skipAllUsers: false));
        Assert.True(SignInStartup.StartsThisCopy(Entries(User.This, false, Machine.None, false), Exe, skipAllUsers: true));
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.This, true, Machine.None, false), Exe, skipAllUsers: false));
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.Other, false, Machine.None, false), Exe, skipAllUsers: false));
    }

    /// <summary>
    /// The all-users entry starts this copy unless the user turned it off here, an administrator turned it off, or the
    /// user has a live entry of their own, which then decides alone.
    /// </summary>
    [Fact]
    public void StartsThisCopy_AllUsersEntry()
    {
        Assert.True(SignInStartup.StartsThisCopy(Entries(User.None, false, Machine.This, false), Exe, skipAllUsers: false));
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.None, false, Machine.This, false), Exe, skipAllUsers: true));
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.None, false, Machine.This, true), Exe, skipAllUsers: false));
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.None, false, Machine.Other, false), Exe, skipAllUsers: false));

        // A stale entry of the user's starts nothing, so it does not stand in the way; a mark without a value neither.
        Assert.True(SignInStartup.StartsThisCopy(Entries(User.Stale, false, Machine.This, false), Exe, skipAllUsers: false));
        Assert.True(SignInStartup.StartsThisCopy(Entries(User.None, true, Machine.This, false), Exe, skipAllUsers: false));

        // A live entry of the user's for another copy, or turned off in Windows, wins over the all-users entry.
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.Other, false, Machine.This, false), Exe, skipAllUsers: false));
        Assert.False(SignInStartup.StartsThisCopy(Entries(User.This, true, Machine.This, false), Exe, skipAllUsers: false));
    }

    /// <summary>
    /// A process the all-users entry started goes on only when this user neither turned it off here nor has a live
    /// entry of their own, enabled or not; a stale entry or a lone mark does not count.
    /// </summary>
    [Fact]
    public void ShouldContinueFromAllUsersEntry_StepsAsideForTheUser()
    {
        Assert.True(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.None, false, Machine.This, false), skipAllUsers: false));
        Assert.False(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.None, false, Machine.This, false), skipAllUsers: true));
        Assert.False(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.This, false, Machine.This, false), skipAllUsers: false));
        Assert.False(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.This, true, Machine.This, false), skipAllUsers: false));
        Assert.False(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.Other, false, Machine.This, false), skipAllUsers: false));
        Assert.True(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.Stale, false, Machine.This, false), skipAllUsers: false));
        Assert.True(SignInStartup.ShouldContinueFromAllUsersEntry(Entries(User.None, true, Machine.This, false), skipAllUsers: false));
        Assert.True(SignInStartup.ShouldContinueFromAllUsersEntry(SignInEntries.None, skipAllUsers: false));
    }

    /// <summary>Each flip of the switch changes exactly what its case says.</summary>
    /// <param name="enable">The switch's new position.</param>
    /// <param name="user">The user's entry.</param>
    /// <param name="userOff">Whether Windows keeps "off" for the user's entry.</param>
    /// <param name="machine">The all-users entry.</param>
    /// <param name="machineOff">Whether an administrator turned the all-users entry off.</param>
    /// <param name="skip">The setting before.</param>
    /// <param name="expectedUser">What must happen to the user's entry.</param>
    /// <param name="expectedClear">Whether Windows' mark must be cleared.</param>
    /// <param name="expectedSkip">The setting after.</param>
    [Theory]
    [MemberData(nameof(PlanCases))]
    public void Plan_ChangesWhatEachCaseNeeds(bool enable, User user, bool userOff, Machine machine, bool machineOff, bool skip, UserEntryChange expectedUser, bool expectedClear, bool expectedSkip)
    {
        var change = SignInStartup.Plan(enable, Entries(user, userOff, machine, machineOff), Exe, skip);
        Assert.Equal(new SignInChange(expectedUser, expectedClear, expectedSkip), change);
    }

    /// <summary>
    /// Whatever Windows holds, applying the plan for a position makes the switch show that position afterwards: 4 user
    /// entries × 2 marks × 3 all-users entries × 2 marks × 2 settings × 2 positions.
    /// </summary>
    [Fact]
    public void Plan_AlwaysReachesTheRequestedPosition()
    {
        int cases = 0;
        foreach (var user in Enum.GetValues<User>())
        {
            foreach (bool userOff in new[] { false, true })
            {
                foreach (var machine in Enum.GetValues<Machine>())
                {
                    foreach (bool machineOff in new[] { false, true })
                    {
                        foreach (bool skip in new[] { false, true })
                        {
                            foreach (bool enable in new[] { false, true })
                            {
                                var before = Entries(user, userOff, machine, machineOff);
                                var change = SignInStartup.Plan(enable, before, Exe, skip);
                                var after = Simulate(before, change);
                                Assert.True(
                                    SignInStartup.StartsThisCopy(after, Exe, change.SkipAllUsers) == enable,
                                    $"switch {(enable ? "on" : "off")} from {before} with skip={skip}: {change} leaves {after}");
                                cases++;
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(192, cases);
    }

    /// <summary>The card says when the PC's installation set it up for everyone, and when an administrator turned that off.</summary>
    [Fact]
    public void Describe_NamesTheAllUsersEntry()
    {
        const string Plain = "Start quietly in the notification area when you sign in.";
        Assert.Equal(Plain, SignInStartup.Describe(SignInEntries.None, Exe));
        Assert.Equal(Plain, SignInStartup.Describe(Entries(User.This, false, Machine.Other, false), Exe));
        Assert.Equal(
            Plain + " It is set up to start for every user of this PC; turning this off changes that only for you.",
            SignInStartup.Describe(Entries(User.None, false, Machine.This, false), Exe));
        Assert.Equal(
            Plain + " Its entry for every user of this PC is turned off in Windows' startup apps, so turning this on adds one just for you.",
            SignInStartup.Describe(Entries(User.None, false, Machine.This, true), Exe));
    }

    /// <summary>The setting is off by default, survives a save and load, and a file from before it reads as off.</summary>
    [Fact]
    public void Setting_DefaultsAndRoundTrip()
    {
        Assert.False(new AppSettings().Normalize().SkipAllUsersStartup);

        using var temp = TestData.NewTempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Update(s => s with { SkipAllUsersStartup = true });
        Assert.True(new SettingsStore(path).Load().SkipAllUsersStartup);
        Assert.True(SettingsStore.TryReadSnapshot(path)!.SkipAllUsersStartup);

        File.WriteAllText(path, "{ \"OpenHotkey\": \"Win+V\" }");
        Assert.False(new SettingsStore(path).Load().SkipAllUsersStartup);
    }

    /// <summary>Builds the facts of a case.</summary>
    /// <param name="user">The user's entry.</param>
    /// <param name="userOff">Windows' "off" mark for it.</param>
    /// <param name="machine">The all-users entry.</param>
    /// <param name="machineOff">An administrator's "off" mark for it.</param>
    /// <returns>The entries.</returns>
    private static SignInEntries Entries(User user, bool userOff, Machine machine, bool machineOff) => new(
        user switch
        {
            User.This => SignInStartup.UserCommandFor(Exe),
            User.Other => SignInStartup.UserCommandFor(OtherExe),
            User.Stale => SignInStartup.UserCommandFor(@"D:\Gone\BetterClipboard.exe"),
            _ => null,
        },
        UserTargetExists: user is User.This or User.Other,
        UserDisabledInWindows: userOff,
        machine switch
        {
            Machine.This => SignInStartup.AllUsersCommandFor(Exe),
            Machine.Other => SignInStartup.AllUsersCommandFor(@"C:\ProgramData\chocolatey\lib\betterclipboard\tools\app\BetterClipboard.exe"),
            _ => null,
        },
        AllUsersDisabledInWindows: machineOff);

    /// <summary>What the registry holds after a change, as <c>StartupRegistration.Apply</c> applies it.</summary>
    /// <param name="before">The entries before.</param>
    /// <param name="change">The change.</param>
    /// <returns>The entries after.</returns>
    private static SignInEntries Simulate(SignInEntries before, SignInChange change)
    {
        var after = change.UserEntry switch
        {
            UserEntryChange.Write => before with { UserCommand = SignInStartup.UserCommandFor(Exe), UserTargetExists = true },
            UserEntryChange.Remove => before with { UserCommand = null, UserTargetExists = false },
            _ => before,
        };

        // A removed value's mark stays in Windows' list under its name; only a clear takes it away.
        return change.ClearUserDisabled ? after with { UserDisabledInWindows = false } : after;
    }
}
