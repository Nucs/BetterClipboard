@echo off
rem msix_lab's child: started by the packaged probe in several ways (msixlab child MODE). It writes one registry value
rem and one file named after the mode; run-lab.ps1 then looks where they landed. A value in the real registry and a
rem file in the real AppData folder mean that way of starting a process left the package's environment.
reg add "HKCU\Software\BC-TEST-MSIX" /v "child-%~1" /t REG_SZ /d 1 /f >nul
if not exist "%LOCALAPPDATA%\BC-TEST-MSIX-files" mkdir "%LOCALAPPDATA%\BC-TEST-MSIX-files"
echo BC-TEST> "%LOCALAPPDATA%\BC-TEST-MSIX-files\child-%~1.txt"
