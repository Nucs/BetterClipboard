using System.Runtime.InteropServices;

namespace BetterClipboard.Windows.Packaging;

/// <summary>
/// Whether this process runs with package identity, i.e. from BetterClipboard's Microsoft Store package (or the same
/// package registered for a test), and under which names. That one fact decides the edition's behavior: where its data
/// lives (<see cref="Core.AppPaths.PackagedDataDirectory"/>), that the Store updates it, that it starts with Windows
/// through a startup task, and that it leaves Explorer's settings alone (CLAUDE.md §2.47).
/// </summary>
/// <remarks>
/// <para>
/// Self-contained on purpose (its own two imports instead of <c>NativeMethods</c>): <c>bclip</c> compiles this very file
/// too (a linked source in <c>BetterClipboard.Cli.csproj</c>), because it must resolve the same data folder and it
/// references Core only.
/// </para>
/// <para>
/// The answer cannot change while a process lives, so it is read once. A process started through the package's alias,
/// its Start menu entry, its startup task, or by path from another process of the package has identity; an exe of a
/// loosely registered test package that is started by its plain path from outside has none, and then behaves like any
/// unpackaged copy.
/// </para>
/// </remarks>
public static partial class PackageIdentity
{
    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>: the sizing call's answer when the process has a package.</summary>
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The names, read once on first use; both <see langword="null"/> without package identity.</summary>
    private static readonly Lazy<(string? Family, string? Full)> Names = new(Read);

    /// <summary>Whether this process runs from a package (the Microsoft Store edition).</summary>
    public static bool IsPackaged => Names.Value.Family is not null;

    /// <summary>
    /// The package family name (<c>Name_PublisherId</c>), which names the package's data folder and its Store page;
    /// <see langword="null"/> without package identity.
    /// </summary>
    public static string? FamilyName => Names.Value.Family;

    /// <summary>
    /// The package full name (family plus version and architecture), for the log; <see langword="null"/> without
    /// package identity.
    /// </summary>
    public static string? FullName => Names.Value.Full;

    /// <summary>Asks Windows for both names.</summary>
    /// <returns>The names, or two <see langword="null"/>s when the process has no package identity or cannot be asked.</returns>
    private static (string? Family, string? Full) Read()
    {
        try
        {
            return (ReadName(full: false), ReadName(full: true));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Never on a supported Windows; a process that cannot ask is simply not packaged.
            return (null, null);
        }
    }

    /// <summary>Reads one of the two names with the usual size-then-fill pair of calls.</summary>
    /// <param name="full">The full name; otherwise the family name.</param>
    /// <returns>The name, or <see langword="null"/> without package identity (<c>APPMODEL_ERROR_NO_PACKAGE</c>).</returns>
    private static unsafe string? ReadName(bool full)
    {
        uint length = 0;
        int result = full ? GetCurrentPackageFullName(ref length, null) : GetCurrentPackageFamilyName(ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0 || length > 1024)
        {
            return null;
        }

        // The length counts the terminator; names are at most a few hundred characters.
        char* buffer = stackalloc char[(int)length];
        result = full ? GetCurrentPackageFullName(ref length, buffer) : GetCurrentPackageFamilyName(ref length, buffer);
        return result == 0 ? new string(buffer) : null;
    }

    /// <summary>The current process's package family name (kernel32).</summary>
    /// <param name="packageFamilyNameLength">In: the buffer's size in characters; out: the size needed, terminator included.</param>
    /// <param name="packageFamilyName">The buffer, or <see langword="null"/> to ask for the size.</param>
    /// <returns>0 on success, 122 when the buffer is too small, 15700 when the process has no package identity.</returns>
    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char* packageFamilyName);

    /// <summary>The current process's package full name (kernel32).</summary>
    /// <param name="packageFullNameLength">In: the buffer's size in characters; out: the size needed, terminator included.</param>
    /// <param name="packageFullName">The buffer, or <see langword="null"/> to ask for the size.</param>
    /// <returns>0 on success, 122 when the buffer is too small, 15700 when the process has no package identity.</returns>
    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint packageFullNameLength, char* packageFullName);
}
