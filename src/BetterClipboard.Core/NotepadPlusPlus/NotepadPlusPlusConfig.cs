using System.Xml;

namespace BetterClipboard.Core.NotepadPlusPlus;

/// <summary>
/// The few Notepad++ settings the integration's status line explains, read from its <c>config.xml</c> (Notepad++ writes it at
/// exit): whether it remembers unsaved tabs at all, how often it saves them, and whether its title bar shows full paths.
/// </summary>
/// <param name="RememberSession">"Remember current session for next launch" (<c>GUIConfig name="RememberLastSession"</c>, default on).</param>
/// <param name="SnapshotMode">"Enable session snapshot and periodic backup" (<c>Backup isSnapshotMode</c>, default on): without it, unsaved tabs have no backup files.</param>
/// <param name="BackupInterval">How often Notepad++ saves a changed tab's backup (<c>snapshotBackupTiming</c>, default 7 s).</param>
public sealed record NotepadPlusPlusConfig(bool RememberSession, bool SnapshotMode, TimeSpan BackupInterval)
{
    /// <summary>Notepad++'s own defaults (8.9.1), used when the file is missing or unreadable.</summary>
    public static NotepadPlusPlusConfig Defaults { get; } = new(true, true, TimeSpan.FromSeconds(7));

    /// <summary>Whether Notepad++ keeps unsaved tabs between runs at all (both switches on).</summary>
    public bool KeepsUnsavedTabs => RememberSession && SnapshotMode;

    /// <summary>
    /// Reads the settings from <c>config.xml</c>; anything missing keeps Notepad++'s default.
    /// </summary>
    /// <param name="stream">The file's bytes (not closed).</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The file is not well-formed XML.</exception>
    /// <exception cref="IOException">Reading the stream failed.</exception>
    public static NotepadPlusPlusConfig Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersInDocument = NotepadPlusPlusSession.MaxBytes,
        };

        var result = Defaults;
        try
        {
            using var reader = XmlReader.Create(stream, settings);

            // ReadElementContentAsString leaves the reader on the node after the element; that node must be looked at
            // without another Read, or a GUIConfig right after it would be skipped.
            bool positioned = false;
            while (positioned || reader.Read())
            {
                positioned = false;
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "GUIConfig")
                {
                    continue;
                }

                switch (reader.GetAttribute("name"))
                {
                    case "RememberLastSession" when !reader.IsEmptyElement:
                        // <GUIConfig name="RememberLastSession">yes</GUIConfig>: the value is the element's text.
                        result = result with { RememberSession = !string.Equals(reader.ReadElementContentAsString().Trim(), "no", StringComparison.OrdinalIgnoreCase) };
                        positioned = true;
                        break;
                    case "Backup":
                        bool snapshot = !string.Equals(reader.GetAttribute("isSnapshotMode"), "no", StringComparison.OrdinalIgnoreCase);
                        var interval = int.TryParse(reader.GetAttribute("snapshotBackupTiming"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ms) && ms > 0
                            ? TimeSpan.FromMilliseconds(Math.Max(ms, 1000))
                            : Defaults.BackupInterval;
                        result = result with { SnapshotMode = snapshot, BackupInterval = interval };
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FormatException("Notepad++'s config.xml is not well-formed.", ex);
        }

        return result;
    }
}
