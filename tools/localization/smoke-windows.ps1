<#
.SYNOPSIS
    Checks the published UI in English and Simplified Chinese on a disposable Windows CI runner.
.DESCRIPTION
    Never run against the installed copy. Uses a separate data directory for each language, disables imports,
    capture, hooks and update checks, and reads UI Automation labels without injecting any keys or mouse input.
.PARAMETER PublishDirectory
    Folder containing the published BetterClipboard.exe.
.PARAMETER OutputDirectory
    Folder for UI label snapshots and screenshots from the disposable runner's desktop.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path (Join-Path $PublishDirectory 'BetterClipboard.exe')).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
$previousData = $env:BETTERCLIPBOARD_DATA_DIR
try {
    foreach ($language in @('en','zh-Hans')) {
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
            $expected = if ($language -eq 'zh-Hans') { '快捷键' } else { 'Shortcut' }
            $names = @()
            for ($attempt = 0; $attempt -lt 25; $attempt++) {
                Start-Sleep -Seconds 1
                if ($process.HasExited) { throw "App exited for $language with code $($process.ExitCode)" }
                $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
                $controls = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
                $names = @($controls | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)
                if ($names -contains $expected) { break }
            }
            $names | ConvertTo-Json | Set-Content (Join-Path $output "ui-$language.json") -Encoding utf8
            if ($names -notcontains $expected) { throw "Expected UI label '$expected' missing for $language" }
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
