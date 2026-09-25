#:property PublishAot=false
using System.Runtime.InteropServices;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe: which candidate global shortcuts are already owned by someone else (Explorer, PowerToys, …)?
// RegisterHotKey fails with ERROR_HOTKEY_ALREADY_REGISTERED (1409) when another thread owns the chord.
// Every successful registration is released immediately, so the probe leaves nothing behind.
// Run: dotnet run tools/probes/probe_hotkeys.cs
// Verified 2026-09-25: Win+V, Win+Shift+V (PowerToys), Win+Ctrl+V, Win+C taken; Win+Alt+V,
// Ctrl+Shift+V, Ctrl+Alt+V, Ctrl+` free.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────
const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
var candidates = new (string Name, uint Mods, uint Vk)[]
{
    ("Win+V", MOD_WIN, 'V'),
    ("Win+Shift+V", MOD_WIN | MOD_SHIFT, 'V'),
    ("Win+Alt+V", MOD_WIN | MOD_ALT, 'V'),
    ("Win+Ctrl+V", MOD_WIN | MOD_CONTROL, 'V'),
    ("Ctrl+Shift+V", MOD_CONTROL | MOD_SHIFT, 'V'),
    ("Ctrl+Alt+V", MOD_CONTROL | MOD_ALT, 'V'),
    ("Ctrl+`", MOD_CONTROL, 0xC0),
    ("Win+C", MOD_WIN, 'C'),
};
int id = 0x7000;
foreach (var (name, mods, vk) in candidates)
{
    bool ok = Native.RegisterHotKey(IntPtr.Zero, id, mods | MOD_NOREPEAT, vk);
    int error = ok ? 0 : Marshal.GetLastWin32Error();
    if (ok)
    {
        Native.UnregisterHotKey(IntPtr.Zero, id);
    }

    Console.WriteLine($"{name,-14} {(ok ? "FREE (registered+released)" : $"TAKEN error={error}")}");
    id++;
}

/// <summary>user32 hotkey imports used by the probe.</summary>
static class Native
{
    /// <summary>Registers a thread hotkey.</summary>
    /// <param name="hWnd">Window (zero = thread).</param>
    /// <param name="id">Hotkey id.</param>
    /// <param name="fsModifiers">MOD_* flags.</param>
    /// <param name="vk">Virtual key.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Unregisters a hotkey.</summary>
    /// <param name="hWnd">Window (zero = thread).</param>
    /// <param name="id">Hotkey id.</param>
    /// <returns>Success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
