using System.Text;

namespace BetterClipboard.Core.NotepadPlusPlus;

/// <summary>
/// The files a Notepad++ process was started to open, read from its command line — so a file double-clicked in Explorer while
/// Notepad++ was not running counts as opened even when Notepad++'s restored session has it too (CLAUDE.md §2.41).
/// </summary>
/// <remarks>
/// <para>
/// <b>Notepad++'s own splitting</b> (8.9.1, <c>parseCommandLine</c> and <c>stripIgnoredParams</c> in <c>winmain.cpp</c>; the
/// rules re-implemented here, nothing copied): spaces and tabs separate arguments; a double quote starts or ends a quoted file
/// name; a quote right after '=' belongs to an option's value (<c>-settingsDir="C:\a b"</c>); <c>-z X</c> drops X, and an
/// argument quoted right after <c>-z X</c> keeps the rest of the line as one argument (the "Notepad replacement" syntax).
/// Options all start with '-'; everything else is a file to open.
/// </para>
/// <para>
/// <b>Kept out:</b> relative paths (the process's working folder is not known here), options, and every file when the process
/// opens session files (<c>-openSession</c>) or only prints or exports (<c>-quickPrint</c>, <c>/p</c> in Notepad style,
/// <c>-export=functionList</c>).
/// </para>
/// </remarks>
public static class NotepadPlusPlusCommandLine
{
    /// <summary>
    /// The files to open named by a process's full command line (its program name first, as Windows reports it).
    /// </summary>
    /// <param name="commandLine">The command line, e.g. <c>"C:\Program Files\Notepad++\notepad++.exe" "C:\a b\c.txt"</c>.</param>
    /// <returns>The fully qualified file paths, in order, without duplicates (case-insensitive); empty when there are none.</returns>
    public static IReadOnlyList<string> FilesToOpen(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var parameters = Split(SkipProgramName(commandLine));
        StripIgnored(parameters);
        bool notepadStyle = parameters.RemoveAll(p => p == "-notepadStyleCmdline") > 0;
        if (notepadStyle && parameters.Exists(p => p is "/p" or "/P"))
        {
            // Notepad's print switch: Notepad++ prints and quits; nothing is opened for the user.
            return [];
        }

        if (parameters.Exists(p => p is "-openSession" or "-quickPrint" or "-export=functionList"))
        {
            return [];
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var candidate = parameter.Trim();
            if (candidate.Length == 0 || candidate[0] == '-' || !Path.IsPathFullyQualified(candidate))
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                files.Add(candidate);
            }
        }

        return files;
    }

    /// <summary>Skips the program name at the start of a command line (Windows' rule: quoted up to the next quote, else up to the first space or tab).</summary>
    /// <param name="commandLine">The full command line.</param>
    /// <returns>The arguments part, without leading whitespace.</returns>
    private static string SkipProgramName(string commandLine)
    {
        var line = commandLine.TrimStart();
        int end;
        if (line.StartsWith('"'))
        {
            end = line.IndexOf('"', 1);
            end = end < 0 ? line.Length : end + 1;
        }
        else
        {
            end = line.IndexOfAny([' ', '\t']);
            end = end < 0 ? line.Length : end;
        }

        return line[end..].TrimStart();
    }

    /// <summary>Splits the arguments the way Notepad++ does (see the class remarks).</summary>
    /// <param name="arguments">The command line without its program name.</param>
    /// <returns>The arguments, quotes of file names removed.</returns>
    private static List<string> Split(string arguments)
    {
        var parameters = new List<string>();
        var current = new StringBuilder();
        bool inToken = false, inFileQuotes = false, inValueQuotes = false;
        int zArgument = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            char ch = arguments[i];
            if (ch == '"')
            {
                if (!inValueQuotes && !inFileQuotes && i > 0 && arguments[i - 1] == '=')
                {
                    // An option's quoted value: the quotes stay part of the argument.
                    inValueQuotes = true;
                    current.Append(ch);
                }
                else if (inValueQuotes)
                {
                    inValueQuotes = false;
                    current.Append(ch);
                }
                else if (!inFileQuotes)
                {
                    // A quoted file name starts a new argument (Notepad++ ends the previous one here).
                    Flush(parameters, current, ref inToken);
                    inFileQuotes = true;
                    inToken = true;
                    if (zArgument == 1)
                    {
                        zArgument = 2;
                    }
                }
                else
                {
                    inFileQuotes = false;
                    Flush(parameters, current, ref inToken);
                }

                continue;
            }

            if ((ch == ' ' || ch == '\t') && !inFileQuotes && !inValueQuotes)
            {
                Flush(parameters, current, ref inToken);
                if (parameters.Count > 0 && parameters[^1] == "-z" && zArgument == 0)
                {
                    zArgument = 1;
                }

                continue;
            }

            if (!inToken)
            {
                inToken = true;
                if (zArgument == 2)
                {
                    // After "-z X", the rest of the line is one argument, spaces and all (an unquoted Notepad-style path).
                    current.Append(arguments.AsSpan(i));
                    Flush(parameters, current, ref inToken);
                    return parameters;
                }
            }

            current.Append(ch);
        }

        Flush(parameters, current, ref inToken);
        return parameters;
    }

    /// <summary>Ends the current argument (empty quoted arguments included, as Notepad++ keeps them).</summary>
    /// <param name="parameters">The arguments so far.</param>
    /// <param name="current">The argument being built (cleared).</param>
    /// <param name="inToken">Whether an argument is being built (reset).</param>
    private static void Flush(List<string> parameters, StringBuilder current, ref bool inToken)
    {
        if (inToken)
        {
            parameters.Add(current.ToString());
        }

        current.Clear();
        inToken = false;
    }

    /// <summary>Drops each <c>-z</c> and the argument after it (Notepad++'s <c>stripIgnoredParams</c>).</summary>
    /// <param name="parameters">The arguments (changed in place).</param>
    private static void StripIgnored(List<string> parameters)
    {
        for (int i = 0; i < parameters.Count;)
        {
            if (parameters[i] == "-z")
            {
                parameters.RemoveRange(i, Math.Min(2, parameters.Count - i));
            }
            else
            {
                i++;
            }
        }
    }
}
