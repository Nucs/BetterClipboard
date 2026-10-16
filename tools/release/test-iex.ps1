<#
.SYNOPSIS
    Checks that the README's install command, `irm .../install.ps1 | iex`, gets past install.ps1's parameter
    block in Windows PowerShell 5.1 and in the shell running this script.

.DESCRIPTION
    Invoke-Expression does not bind a script's parameters: it runs the param block as variable declarations and
    checks every validation attribute ([ValidatePattern], [ValidateSet], [ValidateNotNullOrEmpty], ...) against
    the value each variable gets, which is empty for a parameter without a default. One such attribute makes the
    whole command fail before its first line with "The attribute cannot be added because variable ... would no
    longer be valid". That happened from 0.2.6 to 0.2.8 ([ValidatePattern] on -PackageSha256): the README's main
    install command did nothing but print that error, while every test passed, because they all run the script
    with -File or as a script block, which binds parameters normally.

    This check takes install.ps1's param block out of the script (nothing else runs) and evaluates it through
    Invoke-Expression, in this shell and in a Windows PowerShell 5.1 child process without PSModulePath (PowerShell
    7's module path breaks 5.1's own modules). It changes nothing on the PC, so it is safe anywhere; CI runs it on
    every push.

.PARAMETER Installer
    The installer to check (default: the repository's install.ps1).

.EXAMPLE
    pwsh tools/release/test-iex.ps1
#>
[CmdletBinding()]
param(
    [string] $Installer
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if (-not $Installer) {
    # Resolved here, not as the parameter's default: Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter
    # defaults when the script is started with a forward-slash or relative -File path.
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Installer = Join-Path $here '..\..\install.ps1'
}

$path = (Resolve-Path -LiteralPath $Installer).Path
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $tokens, [ref] $errors)
if ($errors.Count -gt 0) { throw "$path does not parse: $($errors[0].Message)" }
if ($null -eq $ast.ParamBlock) { throw "$path has no param block." }

# The param block and a marker line, nothing else: the probe proves the declarations evaluate, and runs no installer
# code (default values such as Join-Path on LOCALAPPDATA have no side effects).
$marker = 'BC-PARAM-BLOCK-OK'
$probe = $ast.ParamBlock.Extent.Text + [Environment]::NewLine + "'$marker'"
$failures = 0

function Write-Check([string] $Shell, [string] $Output) {
    <#
    .SYNOPSIS
        Prints PASS when the probe's output ends with the marker line, else FAIL with what the shell printed.
    .PARAMETER Shell
        Which shell ran the probe, for the message.
    .PARAMETER Output
        Everything the probe printed, errors included.
    .OUTPUTS
        1 for a failure, 0 for a pass (summed by the caller).
    #>
    if ($Output -match [regex]::Escape($marker)) {
        Write-Host "  PASS  install.ps1's param block runs through Invoke-Expression in $Shell"
        return 0
    }

    Write-Host "  FAIL  install.ps1's param block does not run through Invoke-Expression in $Shell" -ForegroundColor Red
    Write-Host "        $($Output.Trim())"
    return 1
}

# This shell. A script block gives the declarations a scope of their own, so they cannot overwrite this script's
# variables; Invoke-Expression runs in that scope like `irm | iex` runs in the caller's.
$output = try { (& { $probe | Invoke-Expression } 2>&1 | Out-String) } catch { $_.Exception.Message }
$failures += Write-Check "PowerShell $($PSVersionTable.PSVersion)" $output

# Windows PowerShell 5.1, the shell most people paste the README's command into. The probe goes through a file:
# a long command line would need quoting that the probe itself would then have to survive.
$probeFile = Join-Path ([IO.Path]::GetTempPath()) ("bc-iex-probe-{0}.ps1" -f [guid]::NewGuid().ToString('N'))
$savedModulePath = $env:PSModulePath
try {
    Set-Content -LiteralPath $probeFile -Value $probe -Encoding UTF8
    Remove-Item Env:PSModulePath -ErrorAction SilentlyContinue
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $command = "try { Get-Content -Raw -LiteralPath '$probeFile' | Invoke-Expression } catch { `$_.Exception.Message }"
    $output = (& $windowsPowerShell -NoLogo -NoProfile -NonInteractive -Command $command 2>&1 | Out-String)
    $failures += Write-Check 'Windows PowerShell 5.1' $output
}
finally {
    $env:PSModulePath = $savedModulePath
    Remove-Item -LiteralPath $probeFile -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "install.ps1 would fail the README's 'irm ... | iex' command: no validation attributes in its param block." -ForegroundColor Red
    exit 1
}

Write-Host "install.ps1 gets past its param block under 'irm ... | iex'."
