<#
.SYNOPSIS
    Checks the published UI in every language the repository's catalogs hold, on a disposable Windows CI runner.
.DESCRIPTION
    Never run against the installed copy. Uses a separate data directory for each language, disables imports,
    capture, hooks and update checks, and reads UI Automation labels without injecting any keys or mouse input.
    The languages come from src/BetterClipboard.Core/Localization/*.json, and each one must show its own word for
    "Shortcut" (the English word where its catalog lacks it), so a new catalog is checked without editing this script.
    A catalog translated too little to be offered starts in English, and is checked for that.
.PARAMETER PublishDirectory
    Folder containing the published BetterClipboard.exe.
.PARAMETER OutputDirectory
    Folder for UI label snapshots and screenshots from the disposable runner's desktop.
.PARAMETER ConfirmDisposableMachine
    Runs outside GitHub Actions too. Only on a machine nobody uses (a claude-desktops VM): the script opens windows,
    and its screenshots copy the whole screen, whatever else is on it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$ConfirmDisposableMachine
)
$ErrorActionPreference = 'Stop'

# The screenshots below copy the whole screen: on someone's own PC that captures their windows. Like
# tools/release/test-chocolatey.ps1, refuse anywhere but a CI runner unless the machine is declared disposable.
if ($env:GITHUB_ACTIONS -ne 'true' -and -not $ConfirmDisposableMachine) {
    throw 'This UI smoke test opens windows and copies the whole screen: run it on a CI runner, or pass -ConfirmDisposableMachine on a machine nobody uses.'
}
$exe = (Resolve-Path (Join-Path $PublishDirectory 'BetterClipboard.exe')).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
$previousData = $env:BETTERCLIPBOARD_DATA_DIR
$catalogs = Join-Path $PSScriptRoot '..\..\src\BetterClipboard.Core\Localization'
try {
    foreach ($file in Get-ChildItem -Path $catalogs -Filter '*.json' | Sort-Object Name) {
        $language = $file.BaseName
        # Keys are the English text: the label a language shows for "Shortcut" is its catalog's value, or English. Read
        # with a pattern, not ConvertFrom-Json: two English texts that differ only in letter case are two keys, which
        # PowerShell's JSON reader refuses as duplicates.
        $text = Get-Content -Path $file.FullName -Raw -Encoding utf8
        $word = if ($text -match '(?m)^\s*"Shortcut": ("(?:[^"\\]|\\.)*")') { ConvertFrom-Json -InputObject $Matches[1] } else { $null }
        $data = Join-Path $output "isolated-$language"
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        $env:BETTERCLIPBOARD_DATA_DIR = $data
        @{
            InterfaceLanguage = $language
            IsCapturePaused = $true
            UseKeyboardHookFallback = $false
            ImportWindowsHistoryOnStartup = $false
            ImportShareXScreenshots = $false
            ImportWindowsScreenshots = $false
            RecordRunHistory = $false
            ShowEverythingTab = $false
            ShowPowerShellTab = $false
            ShowCmdTab = $false
            KeepClaudeCodePrompts = $false
            KeepCodexPrompts = $false
            CheckForUpdates = $false
        } | ConvertTo-Json | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
        $process = Start-Process -FilePath $exe -PassThru
        try {
            $expected = if ($word) { [string]$word } else { 'Shortcut' }
            $fallback = 'Shortcut' 
            $names = @()
            for ($attempt = 0; $attempt -lt 25; $attempt++) {
                Start-Sleep -Seconds 1
                if ($process.HasExited) { throw "App exited for $language with code $($process.ExitCode)" }
                $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
                $controls = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
                $names = @($controls | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)
                if ($names -contains $expected -or $names -contains $fallback) { break }
            }
            $names | ConvertTo-Json | Set-Content (Join-Path $output "ui-$language.json") -Encoding utf8
            # A catalog too incomplete to be offered starts in English (the app's log says so); any other must show its word.
            if ($names -notcontains $expected) {
                if ($names -contains $fallback) { Write-Output "UI language $language is not offered yet: the app started in English." }
                else { throw "Expected UI label '$expected' missing for $language" }
            }
            $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bounds.Size)
                $bitmap.Save((Join-Path $output "settings-$language.png"))
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
            Write-Output "UI language verified: $language ($($names.Count) labels)"
        } finally {
            # The scoped environment sends Exit only to this isolated test instance.
            if (-not $process.HasExited) {
                Start-Process -FilePath $exe -ArgumentList '--exit' -Wait
                if (-not $process.WaitForExit(10000)) { $process.Kill() }
            }
        }
    }
} finally { $env:BETTERCLIPBOARD_DATA_DIR = $previousData }
