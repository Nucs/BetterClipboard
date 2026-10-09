<#
.SYNOPSIS
    Tests install.ps1's update mode - the command line every released BetterClipboard runs to update itself -
    against a packaged release zip, in scratch folders only.

.DESCRIPTION
    The app's update button downloads a release zip, takes install.ps1 out of it and runs it as

      powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script>
          -Update -Package <zip> -PackageSha256 <hash> -InstallDir <folder> -ResultPath <file> -LogPath <file>

    That command line is a contract: an installed BetterClipboard of any earlier version must be able to run the
    install.ps1 of every later release this way. This script runs exactly that, with Windows PowerShell 5.1 and
    without PSModulePath (as the app starts it), and checks the outcome:

      1. A wrong checksum: refused, the copy untouched, the failure written to the result file.
      2. Options that do not go with -Update: refused before anything is read.
      3. The real thing: the copy's folder is replaced by the package's files, the result file says so with the
         version, this instance's installer.json gets the new version and keeps its other values, the log is
         written, no staging folder stays behind, and nothing is started (the copy was not running).
      4. Nothing else changed: the "start with Windows" values, the Installed-apps entry, Explorer's released
         shortcuts and the user PATH are the same as before.

    Safe on any PC, a developer's own included: the "installed copy" is the package itself, extracted into a new
    folder under the temp folder, with a data folder of its own next to it. An update touches only the folder it
    is told to replace and the records that name that folder, which is the very thing check 4 proves. CI runs it
    on every push, the release job before it publishes.

.PARAMETER Version
    The packaged version (e.g. 0.2.7), as in the zip's file name.

.PARAMETER Architecture
    Which package to test: x64 or arm64. It must run on this PC (an arm64 package does not run on an x64 PC).

.PARAMETER PackageDirectory
    Folder with BetterClipboard-<version>-win-<arch>.zip and SHA256SUMS.txt (tools/release/package.ps1's output).

.EXAMPLE
    pwsh tools/release/test-update.ps1 -Version 0.2.7 -PackageDirectory artifacts/release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64',

    [string] $PackageDirectory = 'artifacts/release'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packages = if ([IO.Path]::IsPathRooted($PackageDirectory)) { $PackageDirectory } else { Join-Path $root $PackageDirectory }
$zipName = "BetterClipboard-$Version-win-$Architecture.zip"
$zip = Join-Path $packages $zipName
$sumsPath = Join-Path $packages 'SHA256SUMS.txt'
foreach ($needed in @($zip, $sumsPath)) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) { throw "Not found: $needed. Run tools/release/package.ps1 first." }
}

$script:Failures = 0

function Confirm-That([string] $What, [bool] $Condition, [string] $Detail = '') {
    <#
    .SYNOPSIS
        Records one check: prints PASS or FAIL with what was checked, and counts the failures.
    .PARAMETER What
        What must hold, as a statement.
    .PARAMETER Condition
        Whether it holds.
    .PARAMETER Detail
        What was found instead, printed for a failure.
    #>
    if ($Condition) {
        Write-Host "  PASS  $What"
    }
    else {
        $script:Failures++
        Write-Host "  FAIL  $What$(if ($Detail) { " ($Detail)" })" -ForegroundColor Red
    }
}

function Get-MachineState {
    <#
    .SYNOPSIS
        What an update must never change on this PC, as one comparable text: the "start with Windows" values, the
        Installed-apps entry of BetterClipboard, Explorer's released shortcuts and the user PATH.
    .OUTPUTS
        System.String.
    #>
    $run = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
    $runValues = if ($run) { @($run.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Name)=$($_.Value)" } | Sort-Object) -join '|' } else { '' }
    $entry = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterClipboard' -ErrorAction SilentlyContinue
    $entryValues = if ($entry) { "$($entry.DisplayVersion)@$($entry.InstallLocation)" } else { '' }
    $advanced = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -ErrorAction SilentlyContinue
    $released = if ($advanced -and $advanced.PSObject.Properties['DisabledHotkeys']) { [string] $advanced.DisabledHotkeys } else { '' }
    $path = [string] [Environment]::GetEnvironmentVariable('Path', 'User')
    return "run:$runValues;entry:$entryValues;released:$released;path:$path"
}

function Invoke-Updater([string] $Script, [string[]] $Arguments, [string] $DataDirectory) {
    <#
    .SYNOPSIS
        Runs an installer script the way the app's updater does and returns its exit code and output.
    .DESCRIPTION
        Windows PowerShell 5.1 from the system folder, no profile, no window, -File with the arguments as they are,
        the temp folder as working folder, PSModulePath removed from the environment, and BETTERCLIPBOARD_DATA_DIR
        set (an isolated instance hands its data folder on like this). Unlike the app, the output is captured: the
        script's own log is one of the things under test, but a failure here should show why at once.
    .PARAMETER Script
        The installer script to run.
    .PARAMETER Arguments
        Its arguments; each is quoted for the command line.
    .PARAMETER DataDirectory
        The data folder the run belongs to.
    .OUTPUTS
        PSCustomObject with ExitCode and Output.
    #>
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    # One string, quoted by hand: Windows PowerShell's .NET has no ArgumentList. No argument here ends in a backslash
    # or holds a quote, the two cases where quoting needs more than a pair of quotes.
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '^-[A-Za-z0-9]+$') { $_ } else { '"' + $_ + '"' } })
    $info.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Script`" $($quoted -join ' ')"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = [IO.Path]::GetTempPath()
    $info.EnvironmentVariables.Remove('PSModulePath')
    $info.EnvironmentVariables['BETTERCLIPBOARD_DATA_DIR'] = $DataDirectory
    $process = [System.Diagnostics.Process]::Start($info)
    # Both streams are read to their end before waiting, or a full pipe would block the script.
    $errorTask = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    if (-not $process.WaitForExit(300000)) {
        $process.Kill()
        throw 'The installer did not finish within 5 minutes.'
    }

    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = ($output + $errorTask.Result) }
}

function Read-Result([string] $Path) {
    <#
    .SYNOPSIS
        The installer's result file as an object, or $null when there is none.
    .PARAMETER Path
        The -ResultPath given to the installer.
    .OUTPUTS
        PSCustomObject, or $null.
    #>
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

$expected = $null
foreach ($line in (Get-Content -LiteralPath $sumsPath)) {
    if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $zipName) { $expected = $Matches[1].ToLowerInvariant() }
}
if (-not $expected) { throw "SHA256SUMS.txt has no entry for $zipName." }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("BetterClipboard-update-test-" + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $scratch 'app'
$data = Join-Path $scratch 'data'
$work = Join-Path $data 'updates'
$resultPath = Join-Path $work 'result.json'
$logPath = Join-Path (Join-Path $data 'logs') 'betterclipboard-update.log'
$marker = Join-Path $app 'BC-TEST-file-of-the-old-version.txt'
$before = Get-MachineState

try {
    Write-Host "==> An installed copy to update: $zipName extracted to $app"
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $app -Force
    Set-Content -LiteralPath $marker -Value 'A file only the old version has: an update replaces the folder, so it must be gone afterwards.'
    # What install.ps1 records at an install; releasedKeys stands for the values an update must keep.
    [ordered]@{ version = '0.0.0-old'; installDir = $app; releasedWinV = $true; releasedKeys = 'V'; onUserPath = $false; installedUtc = '2026-01-01T00:00:00Z' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'installer.json') -Encoding UTF8

    # The script comes out of the package, as in the app (never the installed copy's own, which is being replaced).
    $script = Join-Path $work 'install.ps1'
    Copy-Item -LiteralPath (Join-Path $app 'install.ps1') -Destination $script
    $common = @('-InstallDir', $app, '-ResultPath', $resultPath, '-LogPath', $logPath)

    Write-Host '==> 1. A package that does not have the stated checksum is refused'
    $run = Invoke-Updater -Script $script -Arguments (@('-Update', '-Package', $zip, '-PackageSha256', ('0' * 64)) + $common) -DataDirectory $data
    $result = Read-Result $resultPath
    Confirm-That 'the installer fails' ($run.ExitCode -ne 0) "exit code $($run.ExitCode)"
    Confirm-That 'the result file reports the failure' ($null -ne $result -and $result.ok -eq $false -and $result.error -match 'Checksum mismatch') "$($result | ConvertTo-Json -Compress)"
    Confirm-That 'the copy is untouched' ((Test-Path -LiteralPath $marker) -and -not (Test-Path -LiteralPath "$app.new") -and -not (Test-Path -LiteralPath "$app.old"))
    Confirm-That 'the log names the reason' ((Test-Path -LiteralPath $logPath) -and ((Get-Content -LiteralPath $logPath -Raw) -match 'Checksum mismatch'))
    Remove-Item -LiteralPath $resultPath, $logPath -Force -ErrorAction SilentlyContinue

    Write-Host '==> 2. Options that set an install up do not go with -Update'
    $run = Invoke-Updater -Script $script -Arguments (@('-Update', '-AddToPath', '-Package', $zip, '-PackageSha256', $expected) + $common) -DataDirectory $data
    $result = Read-Result $resultPath
    Confirm-That 'the installer fails' ($run.ExitCode -ne 0) "exit code $($run.ExitCode)"
    Confirm-That 'the result file says which option' ($null -ne $result -and $result.ok -eq $false -and $result.error -match '-AddToPath') "$($result | ConvertTo-Json -Compress)"
    Confirm-That 'the copy is untouched' (Test-Path -LiteralPath $marker)
    Remove-Item -LiteralPath $resultPath, $logPath -Force -ErrorAction SilentlyContinue

    Write-Host '==> 3. The update itself, with the command line the app uses'
    $run = Invoke-Updater -Script $script -Arguments (@('-Update', '-Package', $zip, '-PackageSha256', $expected) + $common) -DataDirectory $data
    $result = Read-Result $resultPath
    Confirm-That 'the installer succeeds' ($run.ExitCode -eq 0) "exit code $($run.ExitCode): $($run.Output)"
    Confirm-That 'the result file reports success and the version' ($null -ne $result -and $result.ok -eq $true -and $result.version -eq $Version -and $result.error -eq '') "$($result | ConvertTo-Json -Compress)"
    Confirm-That 'the folder was replaced as a whole' (-not (Test-Path -LiteralPath $marker))
    $exe = Join-Path $app 'BetterClipboard.exe'
    $productVersion = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion } else { '' }
    Confirm-That 'the new files are in place' ($productVersion -like "$Version*") "product version '$productVersion'"
    Confirm-That 'no staging or backup folder stays behind' (-not (Test-Path -LiteralPath "$app.new") -and -not (Test-Path -LiteralPath "$app.old"))
    $state = Get-Content -LiteralPath (Join-Path $data 'installer.json') -Raw | ConvertFrom-Json
    Confirm-That "this instance's installer.json has the new version and keeps its other values" ($state.version -eq $Version -and $state.releasedKeys -eq 'V' -and $state.installDir -eq $app) "$($state | ConvertTo-Json -Compress)"
    $log = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
    Confirm-That 'the log tells what was done' ($log -match [regex]::Escape("BetterClipboard was updated to $Version."))
    Confirm-That 'the log has no elevation warning and no hotkey or Explorer step' ($log -notmatch 'Running elevated|Taking over|Restarting Explorer|Start menu shortcut')
    $started = @(Get-Process -Name BetterClipboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
    Confirm-That 'nothing was started (the copy was not running)' ($started.Count -eq 0)

    Write-Host '==> 4. Nothing else on this PC changed'
    $after = Get-MachineState
    Confirm-That 'startup entries, the Installed-apps entry, released shortcuts and the user PATH are as before' ($after -eq $before)
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:Failures -gt 0) {
    throw "$script:Failures check(s) failed."
}

Write-Host ''
Write-Host "install.ps1's update mode works for $zipName." -ForegroundColor Green
