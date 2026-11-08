using System.Security.Cryptography;
using System.Text;
using BetterClipboard.Core.Localization;
using BetterClipboard.Windows.Input;
using BetterClipboard.Windows.Integrations;
using BetterClipboard.Windows.Shell;

namespace BetterClipboard.Windows.Tests;

/// <summary>
/// The Windows components that word a reason or a status in English (for the log, the installer, the tests) and let the
/// interface translate it afterwards through a template list (<see cref="UiText.TranslateFormatted(string?, IReadOnlyList{string}, UiLanguage)"/>):
/// every list is in the catalogs, and every text a component actually produces matches one of its templates.
/// </summary>
/// <remarks>
/// This project runs its classes in parallel and pins the interface to English (<see cref="TestLanguage"/>), so these
/// tests never change <see cref="UiText.Language"/>: they translate into Chinese through the explicit-language
/// overload. A produced text that matches no template comes back unchanged, which is what the checks look for.
/// </remarks>
public sealed class TranslationTemplateTests
{
    /// <summary>Every template list of this project has an entry in the English catalog (the Core tests keep every other catalog complete).</summary>
    [Fact]
    public void TemplateLists_HaveCatalogEntries()
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>
        {
            [nameof(HotkeyGesture.ProblemTemplates)] = HotkeyGesture.ProblemTemplates,
            [nameof(HotkeyRegistration.StateTemplates)] = HotkeyRegistration.StateTemplates,
            [nameof(RunCommandResult.DescriptionTemplates)] = RunCommandResult.DescriptionTemplates,
            [nameof(EverythingOwnerVerifier.ReasonTemplates)] = EverythingOwnerVerifier.ReasonTemplates,
            ["Everything " + nameof(EverythingLocator.DetectedByTemplates)] = EverythingLocator.DetectedByTemplates,
            ["ShareX " + nameof(ShareXLocator.DetectedByTemplates)] = ShareXLocator.DetectedByTemplates,
            [nameof(EverythingIntegration.ProblemTemplates)] = EverythingIntegration.ProblemTemplates,
        };

        var missing = lists.SelectMany(list => list.Value.Where(text => !HasEntry(text)).Select(text => $"{list.Key}: {text}")).ToList();
        Assert.True(missing.Count == 0, "No catalog entry for:\n" + string.Join("\n", missing));

        // A template made only of a placeholder would match any text and translate the wrong ones.
        Assert.DoesNotContain(lists.SelectMany(list => list.Value), text => text.Trim() is "{0}" or "{1}");
    }

    /// <summary>
    /// Every shortcut problem — from parsing typed text and from the safety rules — matches one of the templates, so
    /// Settings never shows one in English in another language.
    /// </summary>
    [Fact]
    public void EveryShortcutProblem_MatchesATemplate()
    {
        var problems = new List<string?>();
        foreach (string typed in new[] { "", "+", "Foo+V", "Ctrl+Ctrl+V", "Ctrl+Shift", "Ctrl+Bogus" })
        {
            Assert.False(HotkeyGesture.TryParse(typed, out _, out var problem));
            problems.Add(problem);
        }

        const HotkeyModifiers Ctrl = HotkeyModifiers.Control, Alt = HotkeyModifiers.Alt, Shift = HotkeyModifiers.Shift, Win = HotkeyModifiers.Win;
        (HotkeyModifiers Modifiers, uint Key)[] refused =
        [
            ((HotkeyModifiers)0x4000, 'V'), // a modifier bit that is none of the four
            (Ctrl | Alt, 0x2E), // Ctrl+Alt+Delete
            (Win, 'L'),
            (Alt, 0x09), // Alt+Tab
            (Ctrl, 0x1B), // Ctrl+Esc
            (Ctrl | Shift, 0x1B), // Ctrl+Shift+Esc
            (Alt, 0x1B), // Alt+Esc
            (Alt, 0x73), // Alt+F4
            (Ctrl, 'C'),
            (Ctrl, 0x2D), // Ctrl+Insert
            (Ctrl, 'X'),
            (Ctrl, 'V'),
            (HotkeyModifiers.None, 'V'),
            (Shift, 'V'),
            (Ctrl, 0x10), // Shift as the key
            (Ctrl, 0x01), // a mouse button
            (Ctrl, 0xE8),
            (Ctrl, 0xE7),
            (Ctrl, 0xE5),
            (Ctrl, 0xFC), // VK_NONAME
        ];
        foreach (var (modifiers, key) in refused)
        {
            problems.Add(HotkeyGesture.Problem(modifiers, key));
        }

        var chinese = Chinese();
        var untranslated = problems.Where(problem => problem is null || UiText.TranslateFormatted(problem, HotkeyGesture.ProblemTemplates, chinese) == problem).ToList();
        Assert.True(untranslated.Count == 0, "Matches no template:\n" + string.Join("\n", untranslated.Select(p => p ?? "(no problem)")));
    }

    /// <summary>Every state of a shortcut's row in Settings matches a template, the error code carried into the translation.</summary>
    [Fact]
    public void EveryRegistrationState_MatchesATemplate()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+F9", out var gesture));
        var chinese = Chinese();
        foreach (var registration in new[]
        {
            new HotkeyRegistration(gesture, HotkeyMode.RegisteredHotKey, 0),
            new HotkeyRegistration(gesture, HotkeyMode.KeyboardHook, 1409),
            new HotkeyRegistration(gesture, HotkeyMode.None, 1409),
            new HotkeyRegistration(gesture, HotkeyMode.None, 87),
        })
        {
            string state = registration.DescribeState();
            Assert.NotEqual(state, UiText.TranslateFormatted(state, HotkeyRegistration.StateTemplates, chinese));
        }

        Assert.Equal("无法注册（错误 87）。", UiText.TranslateFormatted(new HotkeyRegistration(gesture, HotkeyMode.None, 87).DescribeState(), HotkeyRegistration.StateTemplates, chinese));
    }

    /// <summary>Every outcome of running a Win+R command again matches a template (the panel's footer shows refusals).</summary>
    [Fact]
    public void EveryRunOutcome_MatchesATemplate()
    {
        var chinese = Chinese();
        foreach (var result in new[]
        {
            RunCommandResult.Started,
            new RunCommandResult(RunCommandOutcome.Cancelled, 1223),
            new RunCommandResult(RunCommandOutcome.Failed, 2),
            new RunCommandResult(RunCommandOutcome.Failed, 3),
            new RunCommandResult(RunCommandOutcome.Failed, 1155),
            new RunCommandResult(RunCommandOutcome.Failed, 5),
            new RunCommandResult(RunCommandOutcome.Failed, 1234),
        })
        {
            string text = result.Describe();
            Assert.NotEqual(text, UiText.TranslateFormatted(text, RunCommandResult.DescriptionTemplates, chinese));
        }
    }

    /// <summary>
    /// The trust check's and the locator's reasons for Everything translate, a nested "unknown organization" included,
    /// and a reason's name parts (a signer, a key name) stay as they were.
    /// </summary>
    [Fact]
    public void EverythingReasons_Translate()
    {
        var chinese = Chinese();
        string notEverything = EverythingOwnerVerifier.VerifyFile(@"C:\BC-TEST\missing\impostor.exe").Reason;
        Assert.Equal("其进程为 impostor.exe，而不是 Everything.exe", UiText.TranslateFormatted(notEverything, EverythingOwnerVerifier.ReasonTemplates, chinese));
        Assert.Equal("其签名者为 未知组织，而不是 voidtools",
            UiText.TranslateFormatted("it is signed by an unknown organization, not voidtools", EverythingOwnerVerifier.ReasonTemplates, chinese));
        Assert.Equal("签名者：voidtools PTY LTD", UiText.TranslateFormatted("signed by voidtools PTY LTD", EverythingOwnerVerifier.ReasonTemplates, chinese));

        Assert.Equal("未安装", UiText.TranslateFormatted(EverythingInstallation.NotFound.DetectedBy, EverythingLocator.DetectedByTemplates, chinese));
        var found = EverythingLocator.Resolve(
            [new EverythingLocator.UninstallEntry("Everything", "Everything 1.4.1.1032 (x64)", "1.4.1.1032", @"C:\BC-TEST\Everything", null, null)],
            _ => true,
            _ => true);
        Assert.Equal("已安装（Everything）", UiText.TranslateFormatted(found.DetectedBy, EverythingLocator.DetectedByTemplates, chinese));
        Assert.Equal("未找到", UiText.TranslateFormatted(ShareXInstallation.NotFound.DetectedBy, ShareXLocator.DetectedByTemplates, chinese));
    }

    /// <summary>The client's own "did not accept the query" message is the integration's problem plus a period, so it translates once trimmed.</summary>
    [Fact]
    public void EverythingClientMessage_IsTheIntegrationsProblem()
    {
        string message = new EverythingUnavailableException(EverythingIntegration.NotAcceptedProblem + ".").Message.TrimEnd('.');
        Assert.Equal("Everything 没有接受查询（可能已退出或无响应）", UiText.TranslateFormatted(message, EverythingIntegration.ProblemTemplates, Chinese()));
    }

    /// <summary>The Simplified Chinese catalog, the one every check translates into.</summary>
    /// <returns>The language.</returns>
    private static UiLanguage Chinese()
    {
        Assert.True(UiText.TryFind("zh-Hans", out var chinese));
        return chinese;
    }

    /// <summary>
    /// Whether the English catalog has exactly this text: under the text itself (the usual key), or under its hash key
    /// (allowed for a text XAML cannot quote).
    /// </summary>
    /// <param name="english">An English text.</param>
    /// <returns><see langword="true"/> when the catalog holds it.</returns>
    private static bool HasEntry(string english) =>
        (UiText.English.TryGet(english, out string? text) && text == english)
        || (UiText.English.TryGet(UiText.HashKey(english), out text) && text == english);
}
