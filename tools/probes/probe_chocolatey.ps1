<#
.SYNOPSIS
    Probe: the Chocolatey CLI behaviors a BetterClipboard Chocolatey package depends on, observed in a private
    Chocolatey root so the machine's own Chocolatey, PATH and packages are never touched.

.DESCRIPTION
    Builds a dummy BC-TEST package ("bctest-clip", versions 9.9.1-9.9.3) whose zips mimic the release zip's layout
    (BetterClipboard.exe, bclip.exe and the two helper executables a self-contained WinUI publish carries,
    createdump.exe and RestartAgent.exe - here copies of PING.EXE and whoami.exe), then installs, upgrades and
    uninstalls it with a copy of the installed choco.exe and observes:

      A  shims: every *.exe in the package folder is shimmed unless "<exe>.ignore" exists; "<exe>.gui" makes a GUI
         shim (Chocolatey does not detect GUI apps itself); package parameters reach Get-PackageParameters.
      B  an upgrade while the app runs and before-modify does not stop it: the package folder is MOVED to lib-bkp
         (a running exe can be moved, not deleted), the new version installs, deleting the backup fails - a
         warning, exit 0 - and the old binary keeps running from the backup.
      D  the same upgrade when the installed version's before-modify stops the app: clean.
      -  no file of an older version survives an upgrade: Chocolatey copies the old folder back from lib-bkp, then
         deletes every file whose checksum still matches the snapshot it took after that version's install
         (.chocolatey\<id>.<version>\.files), extracted payload included. Before-modify sees the OLD version in
         ChocolateyPackageVersion, and a file it writes into the package folder (not in the snapshot) reaches the
         new install script - a way to hand over "the app was running".
      E  uninstall removes the shims and the snapshot's files, but a file written after install (that marker)
         stays, and with it the package folder, while Chocolatey reports success: the uninstall script has to
         delete such files itself.
      F  ARM64 (simulated with PROCESSOR_ARCHITECTURE=ARM64, which is what the helpers read): Chocolatey treats it
         as 32-bit, so a package that passes only the 64-bit file/URL fails.
      G  the same with the package choosing the zip itself and passing it in the 32-bit slot (-File/-Url): works.
      H  --x86 on x64 with that design: ChocolateyForceX86 is 'true' and the x64 zip still installs.

    Isolation: choco.exe, its helpers and tools are copied (read only) into a work folder whose "root" becomes
    ChocolateyInstall for this process only - Chocolatey's official build takes its root from that variable and
    sets environment variables at process scope only. Shims land in <work>\root\bin, which is on no PATH; the
    download cache is <work>\cache. No elevation needed (a non-admin portable install is a supported mode).
    The dummy "app" is a renamed PING.EXE started hidden from the package folder; only processes started by this
    probe are ever stopped (the before-modify script matches the exact path inside the work folder), so a real
    BetterClipboard.exe is never touched. Prints package/version/file names and Chocolatey's messages only.

    Result on Windows 11 26200 with Chocolatey CLI 2.3.0 (2026-10-01): every observation as described above -
    docs/chocolatey.md section 3 and CLAUDE.md section 3.2. Re-run after a Chocolatey upgrade; a CHANGED row means
    the package design has to be re-checked.

.PARAMETER ChocolateySource
    Folder of the installed Chocolatey whose choco.exe, helpers and tools are copied (only read). Default:
    $env:ChocolateyInstall, else C:\ProgramData\chocolatey.

.PARAMETER WorkDirectory
    Folder for the private root, the generated packages, the dummy payloads and every log. Default: a new folder
    under the user's temp folder. Deleted at the end unless -Keep is given; never reused if it already exists.

.PARAMETER Keep
    Keep the work folder for inspection: <work>\logs\<scenario>.log holds choco's console output per step,
    <work>\root\logs\chocolatey.log Chocolatey's own debug log (the lib-bkp moves and copy-backs are there).

.EXAMPLE
    pwsh tools/probes/probe_chocolatey.ps1

    Runs every scenario and prints the observation table; exit code 0 when nothing changed.

.EXAMPLE
    pwsh tools/probes/probe_chocolatey.ps1 -Keep

    Same, and keeps the logs.

.NOTES
    Exit codes: 0 = every observation as verified on 2026-10-01; 1 = at least one CHANGED row; 2 = the probe could
    not run (no Chocolatey found, or the work folder already exists).
    Takes about a minute (61 s on 2026-10-01), most of it Chocolatey's own start-up per command.
#>
# Below the help on purpose: a #Requires line above it hides the whole help block from Get-Help.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ChocolateySource = $(if ($env:ChocolateyInstall) { $env:ChocolateyInstall } else { 'C:\ProgramData\chocolatey' }),
    [string] $WorkDirectory = (Join-Path ([IO.Path]::GetTempPath()) "BetterClipboard-choco-probe-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"),
    [switch] $Keep
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$PackageId = 'bctest-clip'
$Root = Join-Path $WorkDirectory 'root'
$Feed = Join-Path $WorkDirectory 'feed'
$Cache = Join-Path $WorkDirectory 'cache'
$Logs = Join-Path $WorkDirectory 'logs'
$Results = [System.Collections.Generic.List[object]]::new()
$Dummies = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

# The package files, written by New-ProbePackage. Single-quoted here-strings: nothing in them is expanded here.
$NuspecTemplate = @'
<?xml version="1.0" encoding="utf-8"?>
<!-- BC-TEST probe package (tools/probes/probe_chocolatey.ps1). Never pushed anywhere. -->
<package xmlns="http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd">
  <metadata>
    <id>bctest-clip</id>
    <version>__VERSION__</version>
    <title>BC-TEST Clip (probe)</title>
    <authors>BC-TEST author</authors>
    <owners>bctest-owner</owners>
    <projectUrl>https://github.com/Nucs/BetterClipboard</projectUrl>
    <licenseUrl>https://github.com/Nucs/BetterClipboard/blob/main/LICENSE</licenseUrl>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <tags>bctest probe</tags>
    <summary>BC-TEST probe package</summary>
    <description>BC-TEST: a dummy package that only observes Chocolatey's shim, architecture and upgrade behavior.</description>
  </metadata>
  <files>
    <file src="tools\**" target="tools" />
  </files>
</package>
'@

# Install: BCTEST_ARCH stands in for the OS-architecture lookup the real package makes (it cannot be simulated
# on x64 hardware); BCTEST_MODE=file64only reproduces the naive "64-bit slot only" call.
$InstallScript = @'
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$pp = Get-PackageParameters
$params = ($pp.Keys | Sort-Object | ForEach-Object { "$_=$($pp[$_])" }) -join ' '
Write-Host ("BCTEST install {0}: params=[{1}] ForceX86='{2}' width={3}" -f $env:ChocolateyPackageVersion, $params, $env:ChocolateyForceX86, (Get-OSArchitectureWidth))
$arch = if ($env:BCTEST_ARCH) { $env:BCTEST_ARCH } else { 'x64' }
$zip = (Get-Item "$toolsDir\payload-*-$arch.zip").FullName
$dest = Join-Path $toolsDir 'app'
# A file the previous version's before-modify wrote: does it reach this script through the upgrade?
$marker = Join-Path $toolsDir 'bctest-marker.txt'
if (Test-Path $marker) { Write-Host "BCTEST install: before-modify marker found ($((Get-Content $marker -Raw).Trim()))"; Remove-Item $marker -Force }
if ($env:BCTEST_MODE -eq 'file64only') {
    Install-ChocolateyZipPackage -PackageName $env:ChocolateyPackageName -File64 $zip -UnzipLocation $dest
}
else {
    Install-ChocolateyZipPackage -PackageName $env:ChocolateyPackageName -File $zip -UnzipLocation $dest
}
Remove-Item "$toolsDir\*.zip" -Force
foreach ($name in 'createdump.exe', 'RestartAgent.exe') { New-Item -ItemType File -Path (Join-Path $dest "$name.ignore") -Force | Out-Null }
New-Item -ItemType File -Path (Join-Path $dest 'BetterClipboard.exe.gui') -Force | Out-Null
'@

# Before-modify runs from the INSTALLED version; __STOPPER__ decides whether that version stops the dummy app.
$BeforeModifyTemplate = @'
$stopper = __STOPPER__
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Write-Host "BCTEST beforemodify: ChocolateyPackageVersion=$env:ChocolateyPackageVersion stopper=$stopper"
# Not part of the post-install snapshot (.files): observed surviving the upgrade's copy-back, and the uninstall.
Set-Content -Path (Join-Path $toolsDir 'bctest-marker.txt') -Value "from $env:ChocolateyPackageVersion"
if ($stopper) {
    $exe = Join-Path $toolsDir 'app\BetterClipboard.exe'
    $running = @(Get-Process -Name BetterClipboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    Write-Host "BCTEST beforemodify: stopping $($running.Count) dummy process(es)"
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(5000) | Out-Null }
}
'@

$UninstallScript = @'
Write-Host "BCTEST uninstall: ChocolateyPackageVersion=$env:ChocolateyPackageVersion"
'@

function Copy-ChocolateyRoot {
    <#
    .SYNOPSIS
        Copies the parts of the installed Chocolatey that commands need into the private root.
    .DESCRIPTION
        choco.exe resolves helpers (helpers\chocolateyInstaller.psm1), 7z.exe and shimgen.exe (tools\) under its
        root, so those folders come along. lib, bin, config and logs are left behind on purpose: the private root
        starts empty and Chocolatey creates them there.
    .OUTPUTS
        None.
    .NOTES
        Throws when the source has no choco.exe (the caller turns that into exit code 2).
    #>
    $choco = Join-Path $ChocolateySource 'choco.exe'
    if (-not (Test-Path $choco)) { throw "No Chocolatey at '$ChocolateySource' (choco.exe missing)." }
    New-Item -ItemType Directory -Path $Root | Out-Null
    Copy-Item $choco, (Join-Path $ChocolateySource 'choco.exe.manifest') -Destination $Root -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $ChocolateySource 'helpers'), (Join-Path $ChocolateySource 'tools') -Destination $Root -Recurse
}

function New-Payload {
    <#
    .SYNOPSIS
        Writes one dummy app zip (BC-TEST markers, renamed system tools) and returns its path.
    .PARAMETER Version
        Version written into ARCH.txt and into the version-only file "only-<Version>.txt", which shows whether files
        of older versions survive upgrades (they do not: Chocolatey deletes unchanged files of the old install).
    .PARAMETER Arch
        "x64" or "arm64"; only a label - both zips hold x64 tools, the probe reads ARCH.txt to see which one got
        extracted.
    .OUTPUTS
        System.String - the zip's full path.
    #>
    param([string] $Version, [string] $Arch)
    $system = [Environment]::GetFolderPath('System')
    $dir = Join-Path $WorkDirectory "payload-$Version-$Arch"
    New-Item -ItemType Directory -Path $dir | Out-Null
    # PING.EXE runs as long as asked, so it can stand in for the running app (its image file stays locked).
    Copy-Item (Join-Path $system 'PING.EXE') (Join-Path $dir 'BetterClipboard.exe')
    foreach ($name in 'bclip.exe', 'createdump.exe', 'RestartAgent.exe') { Copy-Item (Join-Path $system 'whoami.exe') (Join-Path $dir $name) }
    Set-Content -Path (Join-Path $dir 'ARCH.txt') -Value "BC-TEST $Version $Arch"
    Set-Content -Path (Join-Path $dir "only-$Version.txt") -Value 'BC-TEST'
    $zip = Join-Path $WorkDirectory "payload-$Version-$Arch.zip"
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    return $zip
}

function New-ProbePackage {
    <#
    .SYNOPSIS
        Writes the bctest-clip package for one version (both zips embedded) and packs it into the local feed.
    .PARAMETER Version
        Package version.
    .PARAMETER StopsApp
        Whether this version's before-modify stops the dummy app. It runs when this version is the INSTALLED one,
        i.e. before the upgrade AWAY from it.
    .OUTPUTS
        None.
    .NOTES
        Throws when choco pack fails (a broken template would make every later observation meaningless).
    #>
    param([string] $Version, [bool] $StopsApp)
    $package = Join-Path $WorkDirectory "pkg-$Version"
    $tools = Join-Path $package 'tools'
    New-Item -ItemType Directory -Path $tools | Out-Null
    foreach ($arch in 'x64', 'arm64') { Move-Item (New-Payload $Version $arch) $tools }
    Set-Content -Path (Join-Path $package "$PackageId.nuspec") -Value $NuspecTemplate.Replace('__VERSION__', $Version) -Encoding utf8
    # Chocolatey's guidance: package scripts are UTF-8 with BOM (Windows PowerShell reads BOM-less files as ANSI).
    Set-Content -Path (Join-Path $tools 'chocolateyinstall.ps1') -Value $InstallScript -Encoding utf8BOM
    Set-Content -Path (Join-Path $tools 'chocolateyuninstall.ps1') -Value $UninstallScript -Encoding utf8BOM
    $flag = if ($StopsApp) { '$true' } else { '$false' }
    Set-Content -Path (Join-Path $tools 'chocolateybeforemodify.ps1') -Value $BeforeModifyTemplate.Replace('__STOPPER__', $flag) -Encoding utf8BOM
    Push-Location $package
    try {
        & (Join-Path $Root 'choco.exe') pack --outputdirectory $Feed --limit-output *> (Join-Path $Logs "pack-$Version.log")
        if ($LASTEXITCODE -ne 0) { throw "choco pack $Version failed ($LASTEXITCODE); see $Logs." }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Choco {
    <#
    .SYNOPSIS
        Runs the private choco.exe once and returns its exit code and console output.
    .PARAMETER Name
        Scenario name; the console output goes to <work>\logs\<Name>.log.
    .PARAMETER Arguments
        choco arguments; the cache location, -y and --no-progress are added.
    .PARAMETER ExtraEnvironment
        Variables set for this one command (process scope) and restored afterwards, e.g. PROCESSOR_ARCHITECTURE
        for the ARM64 simulation.
    .OUTPUTS
        PSCustomObject with ExitCode (int) and Text (string, the whole console output).
    #>
    param([string] $Name, [string[]] $Arguments, [hashtable] $ExtraEnvironment = @{})
    $log = Join-Path $Logs "$Name.log"
    $saved = @{}
    foreach ($key in $ExtraEnvironment.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $ExtraEnvironment[$key])
    }

    try {
        & (Join-Path $Root 'choco.exe') @Arguments --cache-location $Cache -y --no-progress *> $log
        $code = $LASTEXITCODE
    }
    finally {
        # Restore even when choco throws, or the ARM64 simulation would leak into every later scenario.
        foreach ($key in $ExtraEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }

    return [pscustomobject]@{ ExitCode = $code; Text = (Get-Content -Path $log -Raw) }
}

function Get-RootState {
    <#
    .SYNOPSIS
        Describes what the private root holds right now.
    .OUTPUTS
        PSCustomObject: Version (installed package version or ''), Payload (ARCH.txt of the extracted app or ''),
        Shims (sorted shim names in root\bin, comma-separated), Markers (.ignore/.gui files), OnlyFiles (the
        "only-<version>.txt" files present in the app folder) and Backup (whether lib-bkp\bctest-clip exists).
    #>
    $lib = Join-Path $Root "lib\$PackageId"
    $app = Join-Path $lib 'tools\app'
    $nuspec = Join-Path $lib "$PackageId.nuspec"
    $bin = Join-Path $Root 'bin'
    [pscustomobject]@{
        Version   = if (Test-Path $nuspec) { ([xml](Get-Content $nuspec -Raw)).package.metadata.version } else { '' }
        Payload   = if (Test-Path (Join-Path $app 'ARCH.txt')) { (Get-Content (Join-Path $app 'ARCH.txt') -Raw).Trim() } else { '' }
        Shims     = if (Test-Path $bin) { (@(Get-ChildItem $bin -Filter *.exe | ForEach-Object Name | Sort-Object) -join ', ') } else { '' }
        Markers   = if (Test-Path $app) { (@(Get-ChildItem $app -File | Where-Object Name -Match '\.exe\.(ignore|gui)$' | ForEach-Object Name | Sort-Object) -join ', ') } else { '' }
        OnlyFiles = if (Test-Path $app) { (@(Get-ChildItem $app -Filter 'only-*.txt' | ForEach-Object Name | Sort-Object) -join ', ') } else { '' }
        Backup    = Test-Path (Join-Path $Root "lib-bkp\$PackageId")
    }
}

function Add-Observation {
    <#
    .SYNOPSIS
        Records one observation for the final table.
    .PARAMETER Scenario
        Scenario letter (see the script's description).
    .PARAMETER What
        What was observed, in words.
    .PARAMETER Expected
        The value verified on 2026-10-01 (Chocolatey CLI 2.3.0).
    .PARAMETER Actual
        The value observed now; compared as strings.
    .OUTPUTS
        None.
    #>
    param([string] $Scenario, [string] $What, $Expected, $Actual)
    $Results.Add([pscustomobject]@{
            Scenario = $Scenario
            What     = $What
            Expected = "$Expected"
            Actual   = "$Actual"
            Result   = if ("$Expected" -eq "$Actual") { 'same' } else { 'CHANGED' }
        })
}

function Start-DummyApp {
    <#
    .SYNOPSIS
        Starts the dummy BetterClipboard.exe (a renamed PING.EXE) hidden from the installed package folder.
    .OUTPUTS
        System.Diagnostics.Process - also remembered, so the probe stops it at the end whatever happens.
    #>
    $exe = Join-Path $Root "lib\$PackageId\tools\app\BetterClipboard.exe"
    $process = Start-Process -FilePath $exe -ArgumentList '-n', '900', '127.0.0.1' -WindowStyle Hidden -PassThru
    $Dummies.Add($process)
    # Give the loader time to map the image, so the file is really in use when Chocolatey reaches it.
    Start-Sleep -Milliseconds 500
    return $process
}

function Test-Contains([string] $Text, [string] $Fragment) {
    <#
    .SYNOPSIS
        Whether choco's output contains a fragment (literal, case-insensitive) - a bool for Add-Observation.
    .PARAMETER Text
        The console output.
    .PARAMETER Fragment
        The literal text to look for.
    .OUTPUTS
        System.Boolean.
    #>
    return $Text.IndexOf($Fragment, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

if (Test-Path $WorkDirectory) {
    Write-Error "Work folder '$WorkDirectory' already exists; refusing to reuse it." -ErrorAction Continue
    exit 2
}

# "Could not run" until the table is printed; StrictMode would also refuse an unset variable at the final exit.
$exitCode = 2
try {
    New-Item -ItemType Directory -Path $WorkDirectory, $Feed, $Cache, $Logs | Out-Null
    try { Copy-ChocolateyRoot } catch { Write-Error $_ -ErrorAction Continue; exit 2 }
    # Process scope only: the machine's ChocolateyInstall (user/machine environment) is never written.
    $env:ChocolateyInstall = $Root
    $chocoVersion = (& (Join-Path $Root 'choco.exe') --version 2>$null | Select-Object -Last 1)
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host "Chocolatey CLI $chocoVersion (copied from $ChocolateySource), Windows $([Environment]::OSVersion.Version), elevated: $elevated"
    Write-Host "Work folder: $WorkDirectory"

    New-ProbePackage '9.9.1' $false
    New-ProbePackage '9.9.2' $true
    New-ProbePackage '9.9.3' $true
    $source = @('--source', $Feed)

    $run = Invoke-Choco 'A-install' (@('install', $PackageId, '--version', '9.9.1', '--params', "'/NoStartup /Shortcut:none'") + $source)
    $state = Get-RootState
    Add-Observation 'A' 'install exit code' 0 $run.ExitCode
    Add-Observation 'A' 'shims created' 'bclip.exe, BetterClipboard.exe' $state.Shims
    Add-Observation 'A' 'GUI shim from BetterClipboard.exe.gui' $true (Test-Contains $run.Text 'created a gui shim for BetterClipboard.exe')
    Add-Observation 'A' 'package parameters parsed' $true (Test-Contains $run.Text 'params=[NoStartup=True Shortcut=none]')

    $dummy = Start-DummyApp
    $run = Invoke-Choco 'B-upgrade-app-running' (@('upgrade', $PackageId, '--version', '9.9.2') + $source)
    $state = Get-RootState
    Add-Observation 'B' 'upgrade while running (no stop): exit code' 0 $run.ExitCode
    Add-Observation 'B' 'version after that upgrade' '9.9.2' $state.Version
    Add-Observation 'B' 'old app still running afterwards' $true (-not $dummy.HasExited)
    Add-Observation 'B' 'backup not deletable (running exe)' $true (Test-Contains $run.Text "Access to the path 'BetterClipboard.exe' is denied")
    Add-Observation 'B' 'lib-bkp left behind' $true $state.Backup
    Add-Observation 'B' 'before-modify sees the old version' $true (Test-Contains $run.Text 'ChocolateyPackageVersion=9.9.1 stopper=False')
    Add-Observation 'B' 'version-only files after the upgrade (9.9.1 removed)' 'only-9.9.2.txt' $state.OnlyFiles
    Add-Observation 'B' 'a file before-modify wrote reaches the new install script' $true (Test-Contains $run.Text 'before-modify marker found (from 9.9.1)')
    if (-not $dummy.HasExited) { $dummy.Kill(); $dummy.WaitForExit() }

    $dummy = Start-DummyApp
    $run = Invoke-Choco 'D-upgrade-stop-first' (@('upgrade', $PackageId, '--version', '9.9.3') + $source)
    $state = Get-RootState
    Add-Observation 'D' 'upgrade after before-modify stops the app: exit code' 0 $run.ExitCode
    Add-Observation 'D' 'app stopped by before-modify' $true $dummy.HasExited
    Add-Observation 'D' 'no access-denied warning' $false (Test-Contains $run.Text 'is denied')
    Add-Observation 'D' 'lib-bkp left behind (B''s leftover included)' $false $state.Backup
    Add-Observation 'D' 'version-only files after the upgrade (9.9.2 removed)' 'only-9.9.3.txt' $state.OnlyFiles

    $run = Invoke-Choco 'E-uninstall' @('uninstall', $PackageId)
    $state = Get-RootState
    Add-Observation 'E' 'uninstall exit code' 0 $run.ExitCode
    Add-Observation 'E' 'before-modify also runs before uninstall' $true (Test-Contains $run.Text 'BCTEST beforemodify')
    Add-Observation 'E' 'shims after uninstall' '' $state.Shims
    # Chocolatey deletes only the files of its post-install snapshot, so the file before-modify just wrote keeps
    # the folder alive - and a later fresh install would find it. Removed by hand so F-H start clean.
    $leftovers = if (Test-Path (Join-Path $Root "lib\$PackageId")) { (@(Get-ChildItem (Join-Path $Root "lib\$PackageId") -Recurse -File | ForEach-Object { $_.FullName.Substring($Root.Length + 1) }) -join ', ') } else { '' }
    Add-Observation 'E' 'left behind by uninstall (a file written after install)' "lib\$PackageId\tools\bctest-marker.txt" $leftovers
    if (Test-Path (Join-Path $Root "lib\$PackageId")) { Remove-Item (Join-Path $Root "lib\$PackageId") -Recurse -Force }

    $run = Invoke-Choco 'F-arm64-64bit-slot-only' (@('install', $PackageId, '--version', '9.9.3') + $source) @{ PROCESSOR_ARCHITECTURE = 'ARM64'; BCTEST_MODE = 'file64only'; BCTEST_ARCH = 'arm64' }
    Add-Observation 'F' 'helpers report ARM64 as 32-bit (width=32)' $true (Test-Contains $run.Text 'width=32')
    Add-Observation 'F' 'ARM64 + 64-bit slot only fails' $true (($run.ExitCode -ne 0) -and (Test-Contains $run.Text 'This package does not support 32 bit architecture'))
    if (Test-Path (Join-Path $Root "lib\$PackageId")) { Invoke-Choco 'F-cleanup' @('uninstall', $PackageId, '--force') | Out-Null }

    $run = Invoke-Choco 'G-arm64-chosen-zip' (@('install', $PackageId, '--version', '9.9.3') + $source) @{ PROCESSOR_ARCHITECTURE = 'ARM64'; BCTEST_ARCH = 'arm64' }
    $state = Get-RootState
    Add-Observation 'G' 'ARM64 + zip chosen by the package: exit code' 0 $run.ExitCode
    Add-Observation 'G' 'extracted payload' 'BC-TEST 9.9.3 arm64' $state.Payload
    Invoke-Choco 'G-uninstall' @('uninstall', $PackageId) | Out-Null

    $run = Invoke-Choco 'H-x86-forced' (@('install', $PackageId, '--version', '9.9.3', '--x86') + $source)
    $state = Get-RootState
    Add-Observation 'H' '--x86 sets ChocolateyForceX86' $true (Test-Contains $run.Text "ForceX86='true'")
    Add-Observation 'H' '--x86 with the chosen zip: exit code' 0 $run.ExitCode
    Add-Observation 'H' 'extracted payload' 'BC-TEST 9.9.3 x64' $state.Payload
    Invoke-Choco 'H-uninstall' @('uninstall', $PackageId) | Out-Null

    # One line per observation: a table squeezes the values into unreadable wrapped columns.
    foreach ($result in $Results) {
        Write-Host ("{0,-7} {1}  {2}: expected '{3}', got '{4}'" -f $result.Result, $result.Scenario, $result.What, $result.Expected, $result.Actual)
    }

    $changed = @($Results | Where-Object Result -EQ 'CHANGED').Count
    Write-Host ("{0} observations, {1} changed since 2026-10-01." -f $Results.Count, $changed)
    $exitCode = if ($changed -gt 0) { 1 } else { 0 }
}
finally {
    # Stop only what this probe started; a real BetterClipboard.exe is never in this list.
    foreach ($process in $Dummies) { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() } }
    if ($Keep) {
        Write-Host "Kept $WorkDirectory"
    }
    elseif (Test-Path $WorkDirectory) {
        Remove-Item -Path $WorkDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
