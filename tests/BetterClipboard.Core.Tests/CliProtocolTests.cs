using System.Text;
using BetterClipboard.Core.Cli;
using BetterClipboard.Core.Content;
using BetterClipboard.Core.Settings;

namespace BetterClipboard.Core.Tests;

/// <summary>
/// Tests for the wire format, the pipe naming, CF_HTML reading, bounded line framing and the
/// side-effect-free settings read the command line relies on.
/// </summary>
public sealed class CliProtocolTests : IDisposable
{
    private readonly TempDirectory temp = TestData.NewTempDirectory();

    /// <summary>Deletes the temp directory.</summary>
    public void Dispose() => temp.Dispose();

    /// <summary>Requests and responses round-trip; camelCase names; nulls are omitted on the wire.</summary>
    [Fact]
    public void Json_RoundTripsCompactCamelCase()
    {
        var request = new CliRequest { Command = CliCommands.Grep, Query = "a\"b\nc", IgnoreCase = true, Since = new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero) };
        var json = CliJson.Serialize(request);
        Assert.DoesNotContain('\n', json);
        Assert.Contains("\"command\":\"grep\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", json, StringComparison.Ordinal);
        Assert.Equal(request, CliJson.DeserializeRequest(json));

        var response = new CliResponse
        {
            Ok = true,
            Items = [new CliItem { Id = 7, Kind = "text", Preview = "שלום", Formats = ["CF_UNICODETEXT"], Matches = [new CliMatch(3, "line")] }],
        };
        var back = CliJson.DeserializeResponse(CliJson.Serialize(response));
        Assert.True(back.Ok);
        Assert.Equal("שלום", back.Items!.Single().Preview);
        Assert.Equal(3, back.Items!.Single().Matches!.Single().Line);
        Assert.Contains("\n", CliJson.Serialize(response, indented: true), StringComparison.Ordinal);
    }

    /// <summary>The pipe name is stable per user and session, case-insensitive in the SID, and hides the SID.</summary>
    [Fact]
    public void PipeName_IsPerUserAndSession()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var name = CliEndpoint.PipeName(sid, 1);
        Assert.Equal(name, CliEndpoint.PipeName(sid.ToLowerInvariant(), 1));
        Assert.NotEqual(name, CliEndpoint.PipeName(sid, 2));
        Assert.NotEqual(name, CliEndpoint.PipeName(sid.Replace("1001", "1002", StringComparison.Ordinal), 1));
        Assert.DoesNotContain("1111111111", name, StringComparison.Ordinal);
        Assert.StartsWith("BetterClipboard.Cli.", name, StringComparison.Ordinal);
        Assert.Equal(name + ".dir-abc", CliEndpoint.PipeName(sid, 1, "dir-abc"));
    }

    /// <summary>
    /// The installed app's data directory keeps the unscoped instance name (so versions find each other);
    /// any other directory gets its own stable scope, so an isolated run never collides with it.
    /// </summary>
    [Fact]
    public void AppPaths_InstanceScope()
    {
        var installed = new AppPaths(AppPaths.DefaultDataDirectory);
        Assert.Null(installed.InstanceScope);
        Assert.Equal(AppPaths.DefaultInstanceName, installed.InstanceName);
        Assert.Null(new AppPaths(AppPaths.DefaultDataDirectory + Path.DirectorySeparatorChar).InstanceScope);

        var isolated = new AppPaths(temp.Path);
        Assert.StartsWith("dir-", isolated.InstanceScope, StringComparison.Ordinal);
        Assert.Equal(isolated.InstanceScope, new AppPaths(temp.Path.ToUpperInvariant()).InstanceScope);
        Assert.NotEqual(isolated.InstanceScope, new AppPaths(temp.Path + "-other").InstanceScope);
        Assert.Equal($"{AppPaths.DefaultInstanceName}.{isolated.InstanceScope}", isolated.InstanceName);
    }

    /// <summary>CF_HTML: the fragment by byte offsets, falling back to the whole HTML and then the body.</summary>
    [Fact]
    public void CfHtml_ExtractsFragment()
    {
        Assert.Equal("<b>שלום</b>", HtmlClipboardFormat.ExtractFragment(BuildCfHtml("<b>שלום</b>")));

        var noFragment = Encoding.UTF8.GetBytes("Version:0.9\r\nStartHTML:-1\r\nEndHTML:-1\r\nStartFragment:-1\r\nEndFragment:-1\r\n<p>x</p>");
        Assert.Equal("<p>x</p>", HtmlClipboardFormat.ExtractFragment(noFragment));
        Assert.Equal("<i>bare</i>", HtmlClipboardFormat.ExtractFragment(Encoding.UTF8.GetBytes("<i>bare</i>\0")));
    }

    /// <summary>Line framing reads one line and refuses oversized ones instead of buffering forever.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Wire_ReadsOneBoundedLine()
    {
        using var ok = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n"));
        Assert.Equal("{\"a\":1}", await CliWire.ReadLineAsync(ok, 100, TestContext.Current.CancellationToken));

        using var empty = new MemoryStream();
        Assert.Null(await CliWire.ReadLineAsync(empty, 100, TestContext.Current.CancellationToken));

        using var huge = new MemoryStream(new byte[1000]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CliWire.ReadLineAsync(huge, 100, TestContext.Current.CancellationToken));

        using var cut = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":"));
        await Assert.ThrowsAsync<IOException>(() => CliWire.ReadLineAsync(cut, 100, TestContext.Current.CancellationToken));
    }

    /// <summary>The command line's settings read never renames or rewrites the app's file.</summary>
    [Fact]
    public void Settings_TryReadSnapshot_HasNoSideEffects()
    {
        var path = Path.Combine(temp.Path, "settings.json");
        Assert.False(SettingsStore.TryReadSnapshot(path)!.EnableCommandLine);

        File.WriteAllText(path, "{ \"EnableCommandLine\": true }");
        Assert.True(SettingsStore.TryReadSnapshot(path)!.EnableCommandLine);

        File.WriteAllText(path, "{ corrupt");
        Assert.Null(SettingsStore.TryReadSnapshot(path));
        Assert.Equal("{ corrupt", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    /// <summary>Builds a CF_HTML payload with correct byte offsets around <paramref name="fragment"/>.</summary>
    /// <param name="fragment">The fragment.</param>
    /// <returns>The payload.</returns>
    private static byte[] BuildCfHtml(string fragment)
    {
        const string headerTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        int headerLength = string.Format(System.Globalization.CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0).Length;
        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture, headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return Encoding.UTF8.GetBytes(header + prefix + fragment + suffix);
    }
}
