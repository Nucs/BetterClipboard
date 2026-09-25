"""Probe: list clipboard-related WinRT classes and where they are hosted.

Enumerates HKLM\\SOFTWARE\\Microsoft\\WindowsRuntime\\ActivatableClassId for names containing
"lipboard" or under WindowsUdk.ApplicationModel.DataTransfer, printing the in-proc DLL or the
out-of-proc server name, then resolves the out-of-proc servers (e.g. CBDHSvc = the Clipboard User
Service). Read-only registry access; no clipboard content involved.

Run: python tools/probes/winrt_classes.py
Verified 2026-09-25: public Clipboard API in windows.applicationmodel.datatransfer.dll, internal
ClipboardHistoryServer & co. in OOP server CBDHSvc, Win+V UI wrapper in windowsudk.shellcommon.dll,
cloud clipboard in cdprt.dll (see CLAUDE.md section 1.1).
"""
import winreg

ACTIVATABLE = r"SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId"
SERVERS = r"SOFTWARE\Microsoft\WindowsRuntime\Server"


def value(key, name):
    """Returns a registry value or None."""
    try:
        return winreg.QueryValueEx(key, name)[0]
    except OSError:
        return None


def main():
    root = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, ACTIVATABLE)
    names, index = [], 0
    while True:
        try:
            name = winreg.EnumKey(root, index)
        except OSError:
            break
        index += 1
        if "lipboard" in name or name.startswith("WindowsUdk.ApplicationModel.DataTransfer"):
            names.append(name)

    servers = set()
    for name in sorted(names):
        key = winreg.OpenKey(root, name)
        dll, server = value(key, "DllPath"), value(key, "Server")
        if server:
            servers.add(server)
        print(f"{name:<82} {dll or 'OOP server=' + str(server)}")

    for server in sorted(servers):
        try:
            key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, SERVERS + "\\" + server)
        except OSError as error:
            print("no server key", server, error)
            continue
        for field in ("ServiceName", "ServerType", "Identity", "IdentityType"):
            print(f"Server {server}.{field} = {value(key, field)}")


if __name__ == "__main__":
    main()
