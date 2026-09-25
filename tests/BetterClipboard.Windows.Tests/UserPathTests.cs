using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// Tests for the user-PATH editing rules behind "Add bclip to PATH" — on the pure value functions, so the
/// user's real PATH is never touched.
/// </summary>
public sealed class UserPathTests
{
    /// <summary>
    /// Appending keeps existing entries verbatim (<c>%VAR%</c> unexpanded, trailing slashes as they were),
    /// drops empty entries, trims the new folder's trailing slash, and refuses duplicates in any spelling.
    /// </summary>
    [Fact]
    public void Append_KeepsEntriesVerbatim_AndRefusesDuplicates()
    {
        Assert.True(UserPath.TryAppend(@"%USERPROFILE%\bin;C:\Tools\;;", @"C:\BC-TEST\App\", out var updated));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools\;C:\BC-TEST\App", updated);

        Assert.False(UserPath.TryAppend(updated, @"c:\bc-test\app", out var same));
        Assert.Equal(updated, same);

        // A %VAR% entry is compared by what it expands to.
        var expanded = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\bin");
        Assert.False(UserPath.TryAppend(@"%USERPROFILE%\bin", expanded + @"\", out _));

        Assert.True(UserPath.TryAppend(string.Empty, @"C:\BC-TEST", out var fresh));
        Assert.Equal(@"C:\BC-TEST", fresh);
    }

    /// <summary>Removing drops every spelling of the folder and leaves the other entries in order.</summary>
    [Fact]
    public void Remove_DropsEverySpelling()
    {
        Assert.True(UserPath.TryRemove(@"A;C:\BC-TEST\;%USERPROFILE%\bin;c:\bc-test;B", @"C:\BC-TEST", out var updated));
        Assert.Equal(@"A;%USERPROFILE%\bin;B", updated);

        Assert.False(UserPath.TryRemove(@"A;B", @"C:\BC-TEST", out var unchanged));
        Assert.Equal(@"A;B", unchanged);
    }
}
