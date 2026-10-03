<#
.SYNOPSIS
    Writes the invented demo data that the README photos show, inside a disposable claude-desktops Windows desktop
    only: an "Acme" demo project, a notes file to copy lines from, and Claude Code, Codex and PowerShell histories
    for the Claude, Codex and Pwsh tabs.

.DESCRIPTION
    The README's photos must never show anyone's real copies, prompts or commands (CLAUDE.md, section 4), so they
    are made on a throwaway Windows desktop with this data. Everything here is made up: a demo project with two
    documents and one source file, four lines to copy, seven Claude Code sends in two sessions (one prompt sent
    twice), two Codex prompts and six PowerShell commands. Times are relative to when the script runs.

    It writes the agents' and PowerShell's own history files, so on a real PC it would replace that user's prompt
    and command history. Two guards prevent that: it runs only where the claude-desktops agent is installed
    (C:\ProgramData\claude-desktop exists only inside those desktops), and it refuses to replace any file that
    already exists.

    The copies themselves (Notepad lines, a Windows Terminal selection, Explorer files, a Snipping Tool snip, an
    Edge address) are made by hand afterwards, so every card names the real app it came from. CLAUDE.md
    section 3.4 has the whole procedure.

.PARAMETER Root
    Folder of the demo project. Default: C:\Projects\Acme. Its paths show on the cards (a copied path, the
    prompts' project name), so keep it short and free of the desktop's user name.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Z:\readme\demo-data.ps1

    Run inside the desktop (Z: is its persist folder), before installing BetterClipboard, so the app's first
    import finds the prompt histories.
#>
[CmdletBinding()]
param(
    [string] $Root = 'C:\Projects\Acme'
)
$ErrorActionPreference = 'Stop'

# The claude-desktops agent keeps its logs here; no ordinary PC has this folder. Without it, this is not a
# throwaway desktop, and the history files below may be someone's real ones.
if (-not (Test-Path -LiteralPath 'C:\ProgramData\claude-desktop')) {
    throw 'This is not a claude-desktops Windows desktop (C:\ProgramData\claude-desktop is missing). The demo data replaces Claude Code, Codex and PowerShell histories, so it runs only there.'
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$now = [DateTimeOffset]::UtcNow
$psReadLine = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\PowerShell\PSReadLine'
$targets = @{
    Report   = Join-Path $Root 'docs\Q3 report.pdf'
    Roadmap  = Join-Path $Root 'docs\Roadmap 2027.docx'
    Source   = Join-Path $Root 'src\App.xaml.cs'
    Notes    = Join-Path $Root 'notes.txt'
    Claude   = Join-Path $env:USERPROFILE '.claude\history.jsonl'
    Codex    = Join-Path $env:USERPROFILE '.codex\history.jsonl'
    PowerShell = Join-Path $psReadLine 'ConsoleHost_history.txt'
}

# Check every target before writing any, so a refusal leaves nothing half-written behind.
$existing = @($targets.Values | Where-Object { Test-Path -LiteralPath $_ })
if ($existing.Count -gt 0) {
    throw ("These files exist already, and demo data never replaces a file:`n  " + ($existing -join "`n  "))
}

<#
.SYNOPSIS
    Writes a text file in UTF-8 without a BOM, creating its folder first.
.PARAMETER Path
    The file to create.
.PARAMETER Text
    The whole content, line endings included.
#>
function Write-DemoFile([string] $Path, [string] $Text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
}

<#
.SYNOPSIS
    Returns the Unix time in milliseconds of a moment some minutes before the script started.
.PARAMETER MinutesAgo
    How long before now.
.OUTPUTS
    System.Int64. Claude Code's history stores its timestamps this way.
#>
function Get-UnixMs([double] $MinutesAgo) { $now.AddMinutes(-$MinutesAgo).ToUnixTimeMilliseconds() }

# Demo project: Explorer copies the two documents as a file list; the source file's path is copied as text.
# The byte contents only need to exist (a PDF and a ZIP signature), nothing ever opens them.
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targets.Report) | Out-Null
[IO.File]::WriteAllBytes($targets.Report, [byte[]](37, 80, 68, 70, 45, 49, 46, 55, 10))
[IO.File]::WriteAllBytes($targets.Roadmap, [byte[]](80, 75, 3, 4))
Write-DemoFile $targets.Source "namespace Acme;`r`n"

# One copy per line, in this order: the reply to pin, the meeting note, the path, the color.
Write-DemoFile $targets.Notes ((@(
    "Thanks, I'll review it today and send my notes by Friday.",
    "Meeting moved to 15:30 tomorrow, same room $([char]0x2705)",
    (Join-Path $Root 'src\App.xaml.cs'),
    '#7C3AED'
) -join "`r`n") + "`r`n")

# Claude Code's history.jsonl: one JSON object per line (display, pastedContents, timestamp in ms, project,
# sessionId). The unit-test prompt is sent twice, so its card reads "sent 2 times".
$session1 = '6f1c2a9e-3b7d-4c55-9a1e-2d8f0b6c4e11'
$session2 = 'a2d94f70-8c1b-4e3a-b5d6-71e0c9f3a822'
$claude = @(
    @{ Text = 'Add a dark mode switch to the settings page and keep the choice after a restart'; Ago = 2880; Session = $session1 },
    @{ Text = 'Write unit tests for the invoice total, including rounding to cents'; Ago = 1500; Session = $session1 },
    @{ Text = '/review'; Ago = 1490; Session = $session1 },
    @{ Text = 'Why does the login test fail only on CI? It passes on my machine.'; Ago = 300; Session = $session2 },
    @{ Text = 'Stream the upload to disk instead of buffering the whole file in memory'; Ago = 95; Session = $session2 },
    @{ Text = 'Write unit tests for the invoice total, including rounding to cents'; Ago = 40; Session = $session2 },
    @{ Text = 'Add a --json flag to the export command and document it in the README'; Ago = 4; Session = $session2 }
)
$lines = foreach ($prompt in $claude) {
    [ordered]@{
        display        = $prompt.Text
        pastedContents = @{}
        timestamp      = (Get-UnixMs $prompt.Ago)
        project        = $Root
        sessionId      = $prompt.Session
    } | ConvertTo-Json -Compress
}
Write-DemoFile $targets.Claude (($lines -join "`n") + "`n")

# Codex's CLI history.jsonl: session_id, ts in Unix seconds, text.
$codex = @(
    @{ Text = 'Explain what the retry policy in http_client.py does'; Ago = 2000 },
    @{ Text = 'Make the CSV export handle commas inside quoted fields'; Ago = 180 }
)
$lines = foreach ($prompt in $codex) {
    [ordered]@{
        session_id = '0199a7c2-5e4f-7a10-b3c8-4d2e6f8a9b01'
        ts         = $now.AddMinutes(-$prompt.Ago).ToUnixTimeSeconds()
        text       = $prompt.Text
    } | ConvertTo-Json -Compress
}
Write-DemoFile $targets.Codex (($lines -join "`n") + "`n")

# PSReadLine's ConsoleHost_history.txt: one command per line, CRLF. Its existence alone shows the Pwsh tab.
Write-DemoFile $targets.PowerShell ((@(
    'winget upgrade --all',
    'git status',
    'git log --oneline --graph --decorate -20',
    'dotnet test',
    'Get-ChildItem -Recurse -Filter *.log | Remove-Item',
    'git status'
) -join "`r`n") + "`r`n")

# A hashtable's Values collection would bind as one space-joined string; the array cast passes each path.
Get-Item -LiteralPath ([string[]] @($targets.Values)) | Select-Object FullName, Length | Format-Table -AutoSize |
    Out-String -Width 200
