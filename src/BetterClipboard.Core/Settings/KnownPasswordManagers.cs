using BetterClipboard.Core.Services;

namespace BetterClipboard.Core.Settings;

/// <summary>What a catalogued app does — only used to describe the list (docs, Settings), never for matching.</summary>
public enum CredentialAppKind
{
    /// <summary>Stores passwords and copies them on request.</summary>
    PasswordManager = 0,

    /// <summary>Shows one-time (2FA) codes and copies them on request.</summary>
    Authenticator = 1,
}

/// <summary>
/// One product in <see cref="KnownPasswordManagers.All"/>: the process names its copies are attributed to.
/// </summary>
/// <param name="Product">Product name as users know it (docs and Settings).</param>
/// <param name="Kind">Password manager or authenticator.</param>
/// <param name="ProcessNames">
/// Executable names without <c>.exe</c>, exactly as <c>SourceAppResolver</c> reports them, so matching stays
/// a plain case-insensitive comparison (<see cref="CaptureRules.IsIgnored"/>). Besides the main app this
/// lists helper processes that can own the clipboard (tray agents, browser-integration hosts, older major
/// versions that ran under another name) — a copy attributed to a helper would otherwise be recorded.
/// </param>
public sealed record KnownCredentialApp(string Product, CredentialAppKind Kind, IReadOnlyList<string> ProcessNames);

/// <summary>
/// Built-in catalog of Windows password managers and authenticator apps whose copies BetterClipboard never
/// records unless the user removes them from <see cref="AppSettings.IgnoredApps"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a list at all:</b> the privacy markers (<c>ExcludeClipboardContentFromMonitorProcessing</c>,
/// <c>CanIncludeInClipboardHistory = 0</c>) are honored for every app, but many managers — Electron-based
/// ones especially — never set them. The list is the second layer for those.
/// </para>
/// <para>
/// <b>Evidence:</b> every name was checked on 2026-09-25 against the vendor's build files (open-source apps:
/// Electron <c>productName</c>/<c>executableName</c>, Flutter <c>BINARY_NAME</c>, Qt <c>TARGET</c>, MSBuild
/// <c>AssemblyName</c>) or process databases and vendor docs; sources per entry are in
/// <c>docs/password-managers.md</c>. Guessed names are deliberately absent.
/// </para>
/// <para>
/// <b>What it cannot cover:</b> managers that live only in a browser extension (the copy is attributed to
/// the browser — ignoring it would drop every browser copy), command-line tools whose copies are
/// attributed to the terminal or <c>clip.exe</c>, and names too generic to ignore safely (Ente Auth runs as
/// <c>auth.exe</c>). Users can add such names by hand.
/// </para>
/// <para>
/// <b>Adding entries later</b> needs no migration code: <see cref="Seed"/> offers every catalog name that a
/// settings file has not seen yet exactly once (tracked in <see cref="AppSettings.SeededIgnoredApps"/>), so
/// existing users receive new names on update while names they deleted stay deleted.
/// </para>
/// </remarks>
public static class KnownPasswordManagers
{
    /// <summary>
    /// The catalog, alphabetical by product within each kind (the order new names are appended to the
    /// user's list). Keep names normalized (no <c>.exe</c>, no path) — a test enforces it.
    /// </summary>
    public static IReadOnlyList<KnownCredentialApp> All { get; } =
    [
        App("1Password", "1Password", "1Password-BrowserSupport", "AgileBits.OnePassword.Desktop", "Agile1pAgent"),
        App("AuthPass", "authpass"),
        App("Bitdefender Wallet", "bdapppassmgr", "bdwtxag", "pmbxag"),
        App("Bitwarden", "Bitwarden"),
        App("Buttercup", "Buttercup"),
        App("Cyclonis Password Manager", "CyclonisPasswordManager"),
        App("Dashlane", "Dashlane", "DashlanePlugin"),
        App("Enpass", "Enpass", "EnpassHelper"),
        App("ESET Password Manager", "pwm"),
        App("F-Secure KEY", "fskey"),
        App("iCloud Passwords", "iCloudPasswords"),
        App("Kaspersky Password Manager", "kpm", "kpm_tray"),
        App("KeePass", "KeePass"),
        App("KeePassX", "KeePassX"),
        App("KeePassXC", "KeePassXC"),
        App("Keeper", "keeperpasswordmanager"),
        App("KeeWeb", "KeeWeb"),
        App("Keyguard", "Keyguard"),
        App("LastPass", "LastPass", "lastapp", "lastapp_x64", "nplastpass"),
        App("McAfee True Key", "TrueKey"),
        App("mSecure", "mSecure"),
        App("Myki", "Myki"),
        App("NordPass", "NordPass", "nordpass-background-app"),
        App("Padloc", "Padloc"),
        App("pass-winmenu", "pass-winmenu"),
        App("Passbolt", "passbolt"),
        App("Password Boss", "PasswordBoss"),
        App("Password Depot", "PasswordDepot"),
        App("Password Manager XP", "PwdManager"),
        App("Password Safe", "pwsafe"),
        App("Pleasant Password Server (KeePass client)", "Pleasant KeePass Client"),
        App("Proton Pass", "ProtonPass"),
        App("Psono", "Psono"),
        App("QtPass", "qtpass"),
        App("RememBear", "RememBear.App", "RememBear"),
        App("RoboForm", "RoboTaskBarIcon", "robotaskbaricon-x64", "identities", "rf-chrome-nm-host"),
        App("SafeInCloud", "SafeInCloud"),
        App("Samsung Pass", "SamsungPass"),
        App("SplashID", "SplashID Desktop"),
        App("Steganos Password Manager", "PasswordManager"),
        App("Sticky Password", "stpass"),
        App("Trend Micro Password Manager", "PwmTower", "PwmConsole"),
        Authenticator("Authy Desktop", "Authy Desktop"),

        // Tauri names the binary after either the product or the Cargo package depending on its
        // version and configuration; both spellings are specific, so both are listed.
        Authenticator("Proton Authenticator", "Proton Authenticator", "proton-authenticator"),
        Authenticator("WinAuth", "WinAuth"),

        // 6.x+ (Flutter) runs as authenticator.exe, 5.x (Qt) as yubioath-desktop.exe.
        Authenticator("Yubico Authenticator", "authenticator", "yubioath-desktop"),
    ];

    /// <summary>Every catalogued process name, in catalog order.</summary>
    public static IReadOnlyList<string> ProcessNames { get; } = All.SelectMany(app => app.ProcessNames).ToArray();

    /// <summary>
    /// Adds every catalogued process name that is not on <paramref name="ignoredApps"/> yet — the explicit
    /// "Add known password managers" action, which (unlike <see cref="Seed"/>) also restores names the user
    /// deleted earlier.
    /// </summary>
    /// <param name="ignoredApps">The user's list (entries may still carry <c>.exe</c> or a path).</param>
    /// <param name="added">How many names were appended.</param>
    /// <returns>The list with the missing names appended in catalog order; existing entries keep their order and spelling.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ignoredApps"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> AddMissing(IReadOnlyList<string> ignoredApps, out int added)
    {
        ArgumentNullException.ThrowIfNull(ignoredApps);
        var result = new List<string>(ignoredApps);
        var present = NormalizedSet(ignoredApps);
        added = 0;
        foreach (var name in ProcessNames)
        {
            if (present.Add(CaptureRules.NormalizeProcessName(name)))
            {
                result.Add(name);
                added++;
            }
        }

        return result;
    }

    /// <summary>
    /// Offers every catalogued name the settings have not seen yet: it is appended to the ignore list
    /// (unless an entry for that process already exists) and recorded as seen, once.
    /// </summary>
    /// <remarks>
    /// Called from <see cref="AppSettings.Normalize"/>, so it runs on every load and save and must be
    /// idempotent: once a name is in <paramref name="seeded"/> it is never added again — deleting it from
    /// the list is the user's decision and survives restarts and updates.
    /// </remarks>
    /// <param name="ignoredApps">The user's list.</param>
    /// <param name="seeded">Names offered before (<see cref="AppSettings.SeededIgnoredApps"/>).</param>
    /// <returns>The new list and the new seen set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static (IReadOnlyList<string> IgnoredApps, IReadOnlyList<string> Seeded) Seed(IReadOnlyList<string> ignoredApps, IReadOnlyList<string> seeded) =>
        Seed(ignoredApps, seeded, ProcessNames);

    /// <summary>
    /// <see cref="Seed(IReadOnlyList{string}, IReadOnlyList{string})"/> over an explicit catalog, so tests can
    /// simulate a later release that adds names.
    /// </summary>
    /// <param name="ignoredApps">The user's list.</param>
    /// <param name="seeded">Names offered before.</param>
    /// <param name="catalog">Process names to offer, in order.</param>
    /// <returns>The new list and the new seen set.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal static (IReadOnlyList<string> IgnoredApps, IReadOnlyList<string> Seeded) Seed(IReadOnlyList<string> ignoredApps, IReadOnlyList<string> seeded, IReadOnlyList<string> catalog)
    {
        ArgumentNullException.ThrowIfNull(ignoredApps);
        ArgumentNullException.ThrowIfNull(seeded);
        ArgumentNullException.ThrowIfNull(catalog);

        var seen = NormalizedSet(seeded);
        var present = NormalizedSet(ignoredApps);
        List<string>? list = null;
        List<string>? seenList = null;
        foreach (var name in catalog)
        {
            var normalized = CaptureRules.NormalizeProcessName(name);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            (seenList ??= [.. seeded]).Add(normalized);

            // A user entry for the same process ("keepass.exe", a full path) already covers it.
            if (present.Add(normalized))
            {
                (list ??= [.. ignoredApps]).Add(name);
            }
        }

        return (list ?? ignoredApps, seenList ?? seeded);
    }

    /// <summary>Normalized, case-insensitive set of process names.</summary>
    /// <param name="names">Names as typed.</param>
    /// <returns>The set.</returns>
    private static HashSet<string> NormalizedSet(IEnumerable<string> names) =>
        new(names.Select(CaptureRules.NormalizeProcessName).Where(name => name.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>Catalog entry for a password manager.</summary>
    /// <param name="product">Product name.</param>
    /// <param name="processNames">Process names.</param>
    /// <returns>The entry.</returns>
    private static KnownCredentialApp App(string product, params string[] processNames) =>
        new(product, CredentialAppKind.PasswordManager, processNames);

    /// <summary>Catalog entry for an authenticator (2FA code) app.</summary>
    /// <param name="product">Product name.</param>
    /// <param name="processNames">Process names.</param>
    /// <returns>The entry.</returns>
    private static KnownCredentialApp Authenticator(string product, params string[] processNames) =>
        new(product, CredentialAppKind.Authenticator, processNames);
}
