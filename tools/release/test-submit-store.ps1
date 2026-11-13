#requires -Version 7.2
<#
.SYNOPSIS
    Runs tools/release/submit-store.ps1 against a stand-in for Microsoft's sign-in service, the Store submission API
    and its upload storage, on this PC, and checks every request it makes.

.DESCRIPTION
    The real API cannot be tried freely: a submission it creates is a real submission of the real app, and an app's
    first submission cannot be made through it at all. So the script is proven here against a small HTTP server on
    127.0.0.1 that answers like the documented API (learn.microsoft.com/windows/uwp/monetize/manage-app-submissions)
    and records what it was sent:

      - the sign-in request, the app, the published submission, a new submission that is a copy of it;
      - the update: the published packages marked for removal, the new ones listed, "What's new" set in one
        language and emptied in the others, everything else sent back as it came (dates stay text);
      - the upload in blocks, put together again and compared byte for byte with the packages;
      - the commit and the wait for its outcome, accepted or refused;
      - what must not happen: a request without credentials, a first submission, a package that is not the Store's,
        a version that is not higher, a pending submission deleted against -KeepPending, a secret or an upload
        address in the output.

    The packages are small stand-ins with a real manifest, so the test needs no build and takes seconds. Nothing
    leaves this PC and nothing on it is changed outside a temp folder; CI runs it on every push.

.PARAMETER Script
    The script under test (default: submit-store.ps1 next to this file).

.EXAMPLE
    pwsh tools/release/test-submit-store.ps1
#>
[CmdletBinding()]
param(
    [string] $Script
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Script) { $Script = Join-Path $here 'submit-store.ps1' }
$Script = (Resolve-Path -LiteralPath $Script).Path

$script:failures = 0
$script:checks = 0

function Assert-That {
    <#
    .SYNOPSIS
        Prints PASS or FAIL for one expectation and counts it.
    .PARAMETER Name
        What is expected, as a sentence.
    .PARAMETER Condition
        Whether it holds.
    .PARAMETER Detail
        What was seen instead, shown only for a failure.
    .OUTPUTS
        None.
    #>
    param([string] $Name, [bool] $Condition, [string] $Detail = '')
    $script:checks++
    if ($Condition) { Write-Host "  PASS  $Name" }
    else {
        $script:failures++
        Write-Host "  FAIL  $Name$(if ($Detail) { ": $Detail" })" -ForegroundColor Red
    }
}

# --- The stand-in server -----------------------------------------------------------------------------------------------

$Secret = 'TEST-CLIENT-SECRET-do-not-print'
$Token = 'TEST-ACCESS-TOKEN-do-not-print'
$UploadKey = 'TESTSIG-do-not-print'
$ProductId = '9TESTTESTTES'
$PublishedId = '1152921504621000001'
$PendingId = '1152921504621000002'
$NewId = '1152921504621000003'

$probe = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = $probe.LocalEndpoint.Port
$probe.Stop()
$base = "http://127.0.0.1:$port"

$state = [hashtable]::Synchronized(@{
        Requests         = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
        Token            = $Token
        TokenFails       = $false
        Published        = $true
        Pending          = $false
        PublishedVersion = '1.2.10.0'
        Statuses         = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
        Blocks           = [hashtable]::Synchronized(@{})
        Archive          = $null
        FailBlocks       = 0
        Base             = $base
        ProductId        = $ProductId
        PublishedId      = $PublishedId
        PendingId        = $PendingId
        NewId            = $NewId
        UploadKey        = $UploadKey
    })

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("$base/")
$listener.Start()

$serve = {
    param($listener, $state)

    function Get-Submission([string] $id, [bool] $new) {
        <#
        .SYNOPSIS
            The submission the stand-in hands out, as the JSON text the Store would send.
        .DESCRIPTION
            A copy of the published submission. Dates, pricing and the certification notes are in it to prove that
            the script under test sends back what it did not mean to change.
        .PARAMETER id
            The submission's id, as it appears in the JSON and in the upload address.
        .PARAMETER new
            True for a submission that was just created: only that one carries the address to upload packages to.
        .OUTPUTS
            System.String. The submission as JSON.
        #>
        $upload = if ($new) { """fileUploadUrl"": ""$($state.Base)/blob/ingestion/$id`?sv=2014-02-14&sr=b&sig=$($state.UploadKey)&se=2026-12-01T00:00:00Z&sp=rwl""," } else { '' }
        return @"
{
  "id": "$id",
  "applicationCategory": "UtilitiesAndTools",
  "pricing": { "trialPeriod": "NoFreeTrial", "marketSpecificPricings": {}, "sales": [], "priceId": "Free", "isAdvancedPricingModel": true },
  "visibility": "Public",
  "targetPublishMode": "Immediate",
  "targetPublishDate": "1601-01-01T00:00:00Z",
  "listings": {
    "en-us": { "baseListing": { "copyrightAndTrademarkInfo": "", "keywords": ["clipboard"], "licenseTerms": "", "privacyPolicy": "https://example.invalid/privacy", "supportContact": "", "websiteUrl": "", "description": "English description", "features": ["one"], "releaseNotes": "notes of the version before", "images": [], "title": "BetterClipboard", "shortDescription": "short" }, "platformOverrides": {} },
    "ru": { "baseListing": { "copyrightAndTrademarkInfo": "", "keywords": [], "licenseTerms": "", "privacyPolicy": "https://example.invalid/privacy", "supportContact": "", "websiteUrl": "", "description": "Описание", "features": [], "releaseNotes": "старые заметки", "images": [], "title": "BetterClipboard", "shortDescription": "" }, "platformOverrides": {} }
  },
  "hardwarePreferences": [],
  "automaticBackupEnabled": false,
  "canInstallOnRemovableMedia": true,
  "isGameDvrEnabled": false,
  "hasExternalInAppProducts": false,
  "meetAccessibilityGuidelines": false,
  "notesForCertification": "keep these notes",
  "status": "$(if ($new) { 'PendingCommit' } else { 'Published' })",
  "statusDetails": { "errors": [], "warnings": [], "certificationReports": [] },
  $upload
  "applicationPackages": [
    { "fileName": "BetterClipboard-old-store-x64.msix", "fileStatus": "Uploaded", "id": "11", "version": "$($state.PublishedVersion)", "architecture": "x64", "languages": ["en-US"], "capabilities": ["runFullTrust"], "minimumDirectXVersion": "None", "minimumSystemRam": "None", "targetDeviceFamilies": ["Windows.Desktop min version 10.0.19041.0"] },
    { "fileName": "BetterClipboard-old-store-arm64.msix", "fileStatus": "Uploaded", "id": "12", "version": "$($state.PublishedVersion)", "architecture": "arm64", "languages": ["en-US"], "capabilities": ["runFullTrust"], "minimumDirectXVersion": "None", "minimumSystemRam": "None", "targetDeviceFamilies": ["Windows.Desktop min version 10.0.19041.0"] }
  ],
  "packageDeliveryOptions": { "packageRollout": { "isPackageRollout": false, "packageRolloutPercentage": 0.0, "packageRolloutStatus": "PackageRolloutNotStarted", "fallbackSubmissionId": "0" }, "isMandatoryUpdate": false, "mandatoryUpdateEffectiveDate": "1601-01-01T00:00:00.0000000Z" },
  "enterpriseLicensing": "Online",
  "allowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies": true,
  "allowTargetFutureDeviceFamilies": { "Desktop": false, "Mobile": false, "Holographic": false, "Xbox": false, "Team": false },
  "friendlyName": "Submission",
  "trailers": []
}
"@
    }

    while ($listener.IsListening) {
        try { $context = $listener.GetContext() } catch { break }
        $request = $context.Request
        $response = $context.Response
        $status = 200
        $answer = ''
        try {
            $memory = [IO.MemoryStream]::new()
            $request.InputStream.CopyTo($memory)
            $bytes = $memory.ToArray()
            $path = $request.Url.AbsolutePath
            $method = $request.HttpMethod
            $query = $request.Url.Query
            $isBlob = $path.StartsWith('/blob/')
            [void] $state.Requests.Add([pscustomobject]@{
                    Method        = $method
                    Path          = $path
                    Query         = $query
                    Authorization = [string] $request.Headers['Authorization']
                    Body          = if ($isBlob) { '' } else { [Text.Encoding]::UTF8.GetString($bytes) }
                    Length        = $bytes.Length
                })
            $app = "/v1.0/my/applications/$($state.ProductId)"

            if ($method -eq 'POST' -and $path -match '^/[^/]+/oauth2/token$') {
                if ($state.TokenFails) {
                    $status = 401
                    $answer = '{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: 00000000-0000-0000-0000-000000000000"}'
                }
                else { $answer = "{""token_type"":""Bearer"",""expires_in"":""3599"",""resource"":""https://manage.devcenter.microsoft.com"",""access_token"":""$($state.Token)""}" }
            }
            elseif ($isBlob) {
                # The upload storage: blocks, then the list that puts them in order. No bearer token: the address has its key.
                if ($query -notmatch [regex]::Escape("sig=$($state.UploadKey)")) { $status = 403 }
                elseif ($query -match 'comp=blocklist') {
                    $ids = [regex]::Matches([Text.Encoding]::UTF8.GetString($bytes), '<Latest>(.*?)</Latest>') | ForEach-Object { $_.Groups[1].Value }
                    $whole = [IO.MemoryStream]::new()
                    foreach ($id in $ids) { $block = $state.Blocks[$id]; $whole.Write($block, 0, $block.Length) }
                    $state.Archive = $whole.ToArray()
                    $status = 201
                }
                elseif ($query -match 'comp=block&blockid=([^&]+)') {
                    if ($state.FailBlocks -gt 0) { $state.FailBlocks--; $status = 500 }
                    else { $state.Blocks[[Uri]::UnescapeDataString($Matches[1])] = $bytes; $status = 201 }
                }
                else { $status = 400 }
            }
            elseif ($request.Headers['Authorization'] -ne "Bearer $($state.Token)") {
                $status = 401
                $answer = '{"code":"Unauthorized","message":"The token is missing or wrong."}'
            }
            elseif ($method -eq 'GET' -and $path -eq $app) {
                $published = if ($state.Published) { """lastPublishedApplicationSubmission"": { ""id"": ""$($state.PublishedId)"", ""resourceLocation"": ""applications/$($state.ProductId)/submissions/$($state.PublishedId)"" }," } else { '' }
                $pending = if ($state.Pending) { """pendingApplicationSubmission"": { ""id"": ""$($state.PendingId)"", ""resourceLocation"": ""applications/$($state.ProductId)/submissions/$($state.PendingId)"" }," } else { '' }
                $answer = "{ ""id"": ""$($state.ProductId)"", ""primaryName"": ""BetterClipboard"", $published $pending ""hasAdvancedListingPermission"": false }"
            }
            elseif ($method -eq 'GET' -and $path -eq "$app/submissions/$($state.PublishedId)") { $answer = Get-Submission $state.PublishedId $false }
            elseif ($method -eq 'DELETE' -and $path -eq "$app/submissions/$($state.PendingId)") { $state.Pending = $false; $status = 204 }
            elseif ($method -eq 'POST' -and $path -eq "$app/submissions") {
                if (-not $state.Published) { $status = 409; $answer = '{"code":"InvalidOperation","message":"The app has no published submission."}' }
                elseif ($state.Pending) { $status = 409; $answer = '{"code":"InvalidOperation","message":"A submission is already pending."}' }
                else { $status = 201; $answer = Get-Submission $state.NewId $true }
            }
            elseif ($method -eq 'PUT' -and $path -eq "$app/submissions/$($state.NewId)") { $answer = [Text.Encoding]::UTF8.GetString($bytes) }
            elseif ($method -eq 'POST' -and $path -eq "$app/submissions/$($state.NewId)/commit") { $status = 202; $answer = '{"status":"CommitStarted"}' }
            elseif ($method -eq 'GET' -and $path -eq "$app/submissions/$($state.NewId)/status") {
                # The answers queued by the test, the last one repeated.
                $answer = $state.Statuses[0]
                if ($state.Statuses.Count -gt 1) { $state.Statuses.RemoveAt(0) }
            }
            else { $status = 404; $answer = '{"code":"ResourceNotFound","message":"No such resource in the stand-in."}' }
        }
        catch {
            $status = 500
            $answer = "{""code"":""StandInError"",""message"":""$($_.Exception.Message -replace '"', "'")""}"
        }
        try {
            $response.StatusCode = $status
            $payload = [Text.Encoding]::UTF8.GetBytes($answer)
            $response.ContentType = 'application/json'
            $response.ContentLength64 = $payload.Length
            $response.OutputStream.Write($payload, 0, $payload.Length)
            $response.Close()
        }
        catch { }
    }
}

$runner = [powershell]::Create()
[void] $runner.AddScript($serve).AddArgument($listener).AddArgument($state)
$handle = $runner.BeginInvoke()

# --- Stand-in packages, identity and notes --------------------------------------------------------------------------------

$work = Join-Path ([IO.Path]::GetTempPath()) "bc-store-test-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-TestPackage {
    <#
    .SYNOPSIS
        Writes a stand-in .msix: a zip with a real package manifest and some payload.
    .PARAMETER Path
        The file to write.
    .PARAMETER Architecture
        The manifest's ProcessorArchitecture.
    .PARAMETER Version
        The manifest's Version.
    .PARAMETER Publisher
        The manifest's Publisher.
    .PARAMETER PayloadBytes
        How much random payload to add (random, so the zip cannot shrink it and the upload needs several blocks).
    .PARAMETER Signed
        Also add a signature entry, as a signed package has.
    .OUTPUTS
        None.
    #>
    param([string] $Path, [string] $Architecture, [string] $Version = '1.2.11.0', [string] $Publisher = 'CN=TEST-PUBLISHER', [int] $PayloadBytes = 1000, [switch] $Signed)
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = "<?xml version=""1.0"" encoding=""utf-8""?><Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""><Identity Name=""Test.BetterClipboard"" Publisher=""$Publisher"" Version=""$Version"" ProcessorArchitecture=""$Architecture"" /></Package>"
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('AppxManifest.xml').Open())
        $writer.Write($manifest); $writer.Dispose()
        $payload = [byte[]]::new($PayloadBytes)
        [Random]::new(7).NextBytes($payload)
        $stream = $zip.CreateEntry('payload.bin', [IO.Compression.CompressionLevel]::NoCompression).Open()
        $stream.Write($payload, 0, $payload.Length); $stream.Dispose()
        if ($Signed) { $zip.CreateEntry('AppxSignature.p7x').Open().Dispose() }
    }
    finally { $zip.Dispose() }
}

$good = Join-Path $work 'good'
New-Item -ItemType Directory -Path $good | Out-Null
# 5.5 MB in one package: the archive is larger than one 4 MiB block, so the upload needs at least two.
New-TestPackage -Path (Join-Path $good 'BetterClipboard-9.9.9-store-x64.msix') -Architecture x64 -PayloadBytes 5500000
New-TestPackage -Path (Join-Path $good 'BetterClipboard-9.9.9-store-arm64.msix') -Architecture arm64 -PayloadBytes 300000

$wrongPublisher = Join-Path $work 'wrong-publisher'
New-Item -ItemType Directory -Path $wrongPublisher | Out-Null
New-TestPackage -Path (Join-Path $wrongPublisher 'BetterClipboard-9.9.9-store-x64.msix') -Architecture x64 -Publisher 'CN=BetterClipboard Test Package (not for distribution)'
New-TestPackage -Path (Join-Path $wrongPublisher 'BetterClipboard-9.9.9-store-arm64.msix') -Architecture arm64

$signed = Join-Path $work 'signed'
New-Item -ItemType Directory -Path $signed | Out-Null
New-TestPackage -Path (Join-Path $signed 'BetterClipboard-9.9.9-store-x64.msix') -Architecture x64 -Signed
New-TestPackage -Path (Join-Path $signed 'BetterClipboard-9.9.9-store-arm64.msix') -Architecture arm64

$identityFile = Join-Path $work 'identity.json'
'{ "reserved": true, "name": "Test.BetterClipboard", "publisher": "CN=TEST-PUBLISHER", "publisherDisplayName": "Test", "displayName": "BetterClipboard", "storeId": "9TESTTESTTES" }' |
    Set-Content -LiteralPath $identityFile -Encoding utf8

$notesFile = Join-Path $work 'notes.md'
@'
BetterClipboard 9.9.9

```powershell
irm https://raw.githubusercontent.com/Nucs/BetterClipboard/main/install.ps1 | iex
```

**A new tab.** It lists what you copied in `Notepad`, with [a link](https://example.invalid/x). More words follow
here that are not part of the first sentence.
- A detail.
- Another detail.

**Fixed**
- One thing no longer breaks.
- Another thing no longer breaks.

| File | What |
|---|---|
| `BetterClipboard-9.9.9-win-x64.zip` | App |

- Not code-signed yet: a zip downloaded with a browser may trigger SmartScreen.
'@ | Set-Content -LiteralPath $notesFile -Encoding utf8
$expectedNotes = "• A new tab. It lists what you copied in Notepad, with a link.`n• Fixed: 2 fixes."

function Reset-StandIn {
    <#
    .SYNOPSIS
        Puts the stand-in back to "an app with a published submission, nothing pending, commits accepted".
    .OUTPUTS
        None.
    #>
    $state.Requests.Clear()
    $state.TokenFails = $false
    $state.Published = $true
    $state.Pending = $false
    $state.PublishedVersion = '1.2.10.0'
    $state.Blocks.Clear()
    $state.Archive = $null
    $state.FailBlocks = 0
    $state.Statuses.Clear()
    [void] $state.Statuses.Add('{"status":"CommitStarted","statusDetails":{"errors":[],"warnings":[],"certificationReports":[]}}')
    [void] $state.Statuses.Add('{"status":"PreProcessing","statusDetails":{"errors":[],"warnings":[],"certificationReports":[]}}')
}

function Invoke-Submit {
    <#
    .SYNOPSIS
        Runs the script under test in its own PowerShell, with or without the three credentials.
    .PARAMETER Arguments
        The script's arguments; the stand-in's addresses and the test identity are added.
    .PARAMETER NoCredentials
        Leave the three environment variables unset.
    .OUTPUTS
        An object: ExitCode and Output (everything the script printed, as one string).
    #>
    param([string[]] $Arguments, [switch] $NoCredentials)
    $saved = @{}
    foreach ($name in 'PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_CLIENT_ID', 'PARTNER_CENTER_CLIENT_SECRET') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        $env:PARTNER_CENTER_TENANT_ID = if ($NoCredentials) { $null } else { '11111111-2222-3333-4444-555555555555' }
        $env:PARTNER_CENTER_CLIENT_ID = if ($NoCredentials) { $null } else { 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' }
        $env:PARTNER_CENTER_CLIENT_SECRET = if ($NoCredentials) { $null } else { $Secret }
        $all = @('-NoProfile', '-File', $Script) + $Arguments
        if ($Arguments -notcontains '-ApiBaseUri') { $all += @('-ApiBaseUri', $base) }
        $all += @('-LoginBaseUri', $base, '-IdentityFile', $identityFile)
        $output = (& pwsh @all 2>&1 | Out-String)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}

function Get-Calls {
    <#
    .SYNOPSIS
        The requests the stand-in received, as "METHOD path" strings (uploads as "PUT blob block" / "PUT blob list").
    .OUTPUTS
        System.String[].
    #>
    return @($state.Requests.ToArray() | ForEach-Object {
            if ($_.Path.StartsWith('/blob/')) { if ($_.Query -match 'comp=blocklist') { 'PUT blob list' } else { 'PUT blob block' } }
            else { "$($_.Method) $($_.Path -replace '^/v1\.0/my/applications/[^/]+', 'app' -replace '^/[^/]+/oauth2/token$', 'token')" }
        })
}

function Test-Leak {
    <#
    .SYNOPSIS
        Whether a run's output holds a credential, a token or the upload address's key.
    .PARAMETER Output
        What the script printed.
    .OUTPUTS
        System.Boolean. True when something leaked.
    #>
    param([string] $Output)
    return $Output.Contains($Secret) -or $Output.Contains($Token) -or $Output.Contains($UploadKey)
}

try {
    $submit = @('-Version', '9.9.9', '-PackageDirectory', $good, '-ReleaseNotesFile', $notesFile)

    Write-Host 'No credentials'
    Reset-StandIn
    $run = Invoke-Submit -Arguments $submit -NoCredentials
    Assert-That 'ends with exit code 3' ($run.ExitCode -eq 3) "exit $($run.ExitCode): $($run.Output)"
    Assert-That 'makes no request' ($state.Requests.Count -eq 0) (Get-Calls | Out-String)

    Write-Host 'Check'
    Reset-StandIn
    $run = Invoke-Submit -Arguments @('-Mode', 'Check')
    Assert-That 'ends with exit code 0' ($run.ExitCode -eq 0) "exit $($run.ExitCode): $($run.Output)"
    Assert-That 'only signs in and reads' ((Get-Calls) -join '; ' -eq "POST token; GET app; GET app/submissions/$PublishedId") ((Get-Calls) -join '; ')
    Assert-That 'names the published version' ($run.Output -match '1\.2\.10\.0') $run.Output
    Assert-That 'prints no credential' (-not (Test-Leak $run.Output))
    $form = $state.Requests[0].Body
    Assert-That 'the sign-in asks for client credentials for the submission API' ($form -match 'grant_type=client_credentials' -and $form -match 'resource=https%3A%2F%2Fmanage\.devcenter\.microsoft\.com' -and $form -match [regex]::Escape("client_secret=$Secret")) $form.Replace($Secret, '<secret>')

    Write-Host 'An app without a published submission'
    Reset-StandIn
    $state.Published = $false
    $run = Invoke-Submit -Arguments $submit
    Assert-That 'ends with exit code 4' ($run.ExitCode -eq 4) "exit $($run.ExitCode): $($run.Output)"
    Assert-That 'creates nothing' (-not ((Get-Calls) -match '^POST app/submissions')) ((Get-Calls) -join '; ')

    Write-Host 'Submit'
    Reset-StandIn
    $run = Invoke-Submit -Arguments $submit
    $calls = Get-Calls
    Assert-That 'ends with exit code 0' ($run.ExitCode -eq 0) "exit $($run.ExitCode): $($run.Output)"
    Assert-That 'creates, updates, uploads, commits and asks for the outcome, in that order' (
        ($calls -join '; ') -match "^POST token; GET app; GET app/submissions/$PublishedId; POST app/submissions; PUT app/submissions/$NewId; (PUT blob block; ){2,}PUT blob list; POST app/submissions/$NewId/commit; GET app/submissions/$NewId/status; GET app/submissions/$NewId/status$") ($calls -join '; ')
    $updates = @($state.Requests.ToArray() | Where-Object { $_.Method -eq 'PUT' -and $_.Path -like "*/submissions/$NewId" })
    Assert-That 'sends exactly one update' ($updates.Count -eq 1) "$($updates.Count) update(s)"
    # Without an update there is nothing to look into; an empty one lets the checks below fail one by one instead.
    $update = if ($updates.Count -gt 0) { $updates[0].Body } else { '{"applicationPackages":[],"listings":{"en-us":{"baseListing":{"releaseNotes":null,"description":null}},"ru":{"baseListing":{"releaseNotes":null,"description":null}}},"pricing":{"priceId":null},"notesForCertification":null}' }
    $sent = $update | ConvertFrom-Json -AsHashtable
    $byStatus = $sent.applicationPackages | Group-Object { $_.fileStatus } -AsHashTable -AsString
    Assert-That 'the published packages are marked for removal' (@($byStatus['PendingDelete']).Count -eq 2 -and (@($byStatus['PendingDelete'].fileName) -join ',') -match 'old-store-x64.*old-store-arm64') ($sent.applicationPackages | ConvertTo-Json -Compress)
    Assert-That 'the new packages are listed for upload' ((@($byStatus['PendingUpload'].fileName) -join ',') -eq 'BetterClipboard-9.9.9-store-x64.msix,BetterClipboard-9.9.9-store-arm64.msix' -and @($byStatus['PendingUpload'] | Where-Object { $_.minimumDirectXVersion -ne 'None' -or $_.minimumSystemRam -ne 'None' }).Count -eq 0) ($sent.applicationPackages | ConvertTo-Json -Compress)
    Assert-That '"What''s new" is this release''s notes in English' ($sent.listings.'en-us'.baseListing.releaseNotes -ceq $expectedNotes) $sent.listings.'en-us'.baseListing.releaseNotes
    Assert-That '"What''s new" is emptied in the other languages' ($sent.listings.ru.baseListing.releaseNotes -eq '') $sent.listings.ru.baseListing.releaseNotes
    Assert-That 'the rest goes back as it came' (
        $update.Contains('"targetPublishDate":"1601-01-01T00:00:00Z"') -and $update.Contains('"mandatoryUpdateEffectiveDate":"1601-01-01T00:00:00.0000000Z"') -and
        $sent.pricing.priceId -eq 'Free' -and $sent.notesForCertification -eq 'keep these notes' -and $sent.listings.ru.baseListing.description -ceq 'Описание' -and
        $sent.listings.'en-us'.baseListing.description -eq 'English description') $update.Substring(0, [Math]::Min(400, $update.Length))

    $archive = Join-Path $work 'uploaded.zip'
    Assert-That 'the upload was closed with its block list' ($null -ne $state.Archive)
    if ($null -eq $state.Archive) {
        # An empty archive, so the checks of its content fail by themselves.
        [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create).Dispose()
    }
    else { [IO.File]::WriteAllBytes($archive, [byte[]] $state.Archive) }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = @($zip.Entries | ForEach-Object FullName)
        Assert-That 'the upload is one archive with both packages at its root' (($names -join ',') -eq 'BetterClipboard-9.9.9-store-x64.msix,BetterClipboard-9.9.9-store-arm64.msix') ($names -join ',')
        $same = $true
        foreach ($entry in $zip.Entries) {
            $copy = [IO.MemoryStream]::new()
            $stream = $entry.Open(); $stream.CopyTo($copy); $stream.Dispose()
            $original = [IO.File]::ReadAllBytes((Join-Path $good $entry.FullName))
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($copy.ToArray())) -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original))) { $same = $false }
        }
        Assert-That 'the uploaded packages are the built ones, byte for byte' $same
    }
    finally { $zip.Dispose() }
    Assert-That 'prints no credential and no upload address' (-not (Test-Leak $run.Output)) ($run.Output -replace [regex]::Escape($Secret), '<secret>')

    Write-Host 'A pending submission'
    Reset-StandIn
    $state.Pending = $true
    $run = Invoke-Submit -Arguments $submit
    $calls = Get-Calls
    Assert-That 'is deleted before a new one is created' ($run.ExitCode -eq 0 -and ($calls -join '; ') -match "DELETE app/submissions/$PendingId; POST app/submissions;") "exit $($run.ExitCode): $($calls -join '; ')"

    Reset-StandIn
    $state.Pending = $true
    $run = Invoke-Submit -Arguments ($submit + '-KeepPending')
    $calls = Get-Calls
    Assert-That 'stays with -KeepPending, and nothing is created (exit code 1)' ($run.ExitCode -eq 1 -and -not ($calls -match '^(DELETE|POST app/submissions)')) "exit $($run.ExitCode): $($calls -join '; ')"

    Write-Host 'A commit the Store refuses'
    Reset-StandIn
    $state.Statuses.Clear()
    [void] $state.Statuses.Add('{"status":"CommitStarted","statusDetails":{"errors":[],"warnings":[],"certificationReports":[]}}')
    [void] $state.Statuses.Add('{"status":"CommitFailed","statusDetails":{"errors":[{"code":"InvalidState","details":"The package version is too low."}],"warnings":[{"code":"ListingOptOutWarning","details":"A listing was removed."}],"certificationReports":[]}}')
    $run = Invoke-Submit -Arguments $submit
    Assert-That 'ends with exit code 1 and shows the Store''s words' ($run.ExitCode -eq 1 -and $run.Output -match 'CommitFailed' -and $run.Output -match 'The package version is too low\.' -and $run.Output -match 'A listing was removed\.') "exit $($run.ExitCode): $($run.Output)"

    Write-Host 'A version that is not higher than the published one'
    Reset-StandIn
    $state.PublishedVersion = '1.2.11.0'
    $run = Invoke-Submit -Arguments $submit
    Assert-That 'is refused before anything is created (exit code 1)' ($run.ExitCode -eq 1 -and -not ((Get-Calls) -match '^POST app/submissions') -and $run.Output -match '1\.2\.11\.0') "exit $($run.ExitCode): $((Get-Calls) -join '; ')"

    Write-Host 'Packages that are not the Store''s'
    Reset-StandIn
    $run = Invoke-Submit -Arguments @('-Version', '9.9.9', '-PackageDirectory', $wrongPublisher)
    Assert-That 'another publisher (a test-signed package) is refused without a request (exit code 2)' ($run.ExitCode -eq 2 -and $state.Requests.Count -eq 0 -and $run.Output -match 'not the Store''s package') "exit $($run.ExitCode): $($run.Output)"
    $run = Invoke-Submit -Arguments @('-Version', '9.9.9', '-PackageDirectory', $signed)
    Assert-That 'a signed package is refused without a request (exit code 2)' ($run.ExitCode -eq 2 -and $state.Requests.Count -eq 0 -and $run.Output -match 'is signed') "exit $($run.ExitCode): $($run.Output)"
    $run = Invoke-Submit -Arguments @('-Version', '9.9.8', '-PackageDirectory', $good)
    Assert-That 'a missing package is refused without a request (exit code 2)' ($run.ExitCode -eq 2 -and $state.Requests.Count -eq 0) "exit $($run.ExitCode): $($run.Output)"
    $run = Invoke-Submit -Arguments @('-Version', '9.9.9-rc.1', '-PackageDirectory', $good)
    Assert-That 'a pre-release is refused without a request (exit code 2)' ($run.ExitCode -eq 2 -and $state.Requests.Count -eq 0) "exit $($run.ExitCode): $($run.Output)"

    Write-Host 'Draft'
    Reset-StandIn
    $run = Invoke-Submit -Arguments ($submit + @('-Mode', 'Draft'))
    $calls = Get-Calls
    Assert-That 'uploads and does not commit' ($run.ExitCode -eq 0 -and ($calls -contains 'PUT blob list') -and -not ($calls -match 'commit$')) "exit $($run.ExitCode): $($calls -join '; ')"

    Write-Host 'An address that is not the Store''s'
    Reset-StandIn
    $run = Invoke-Submit -Arguments ($submit + @('-ApiBaseUri', 'http://example.com'))
    Assert-That 'plain http to another computer is refused without a request (exit code 2)' ($run.ExitCode -eq 2 -and $state.Requests.Count -eq 0) "exit $($run.ExitCode): $($run.Output)"

    Write-Host 'A sign-in that fails'
    Reset-StandIn
    $state.TokenFails = $true
    $run = Invoke-Submit -Arguments @('-Mode', 'Check')
    Assert-That 'ends with exit code 1, names the service''s error code, and prints no credential' ($run.ExitCode -eq 1 -and $run.Output -match 'invalid_client' -and -not (Test-Leak $run.Output) -and $run.Output -notmatch 'Trace ID') "exit $($run.ExitCode): $($run.Output -replace [regex]::Escape($Secret), '<secret>')"

    Write-Host 'An upload block that fails once'
    Reset-StandIn
    $state.FailBlocks = 1
    $run = Invoke-Submit -Arguments $submit
    $whole = $state.Archive
    Assert-That 'is sent again, and the archive arrives whole' ($run.ExitCode -eq 0 -and $null -ne $whole -and $whole.Length -eq (Get-Item -LiteralPath $archive).Length) "exit $($run.ExitCode): $($run.Output)"

    Write-Host 'Release notes for "What''s new"'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref] $null, [ref] $null)
    $definition = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-StoreNotes' }, $true)[0]
    . ([scriptblock]::Create($definition.Extent.Text))
    $markdown = Get-Content -LiteralPath $notesFile -Raw -Encoding utf8
    Assert-That 'the title, the install command, the table and the signing note are left out; leads keep their first sentence' ((ConvertTo-StoreNotes -Markdown $markdown) -ceq $expectedNotes) (ConvertTo-StoreNotes -Markdown $markdown)
    Assert-That 'too long: the lead-ins alone' ((ConvertTo-StoreNotes -Markdown $markdown -Limit 40) -ceq "• A new tab.`n• Fixed.") (ConvertTo-StoreNotes -Markdown $markdown -Limit 40)
    $cut = ConvertTo-StoreNotes -Markdown $markdown -Limit 16
    Assert-That 'still too long: whole lines that fit, then an ellipsis, never over the limit' ($cut -ceq "• A new tab.`n…" -and $cut.Length -le 16) $cut
    Assert-That 'notes with nothing to say give nothing' ((ConvertTo-StoreNotes -Markdown "BetterClipboard 1.2.3`n`n| a | b |`n|---|---|") -eq '') (ConvertTo-StoreNotes -Markdown "BetterClipboard 1.2.3`n`n| a | b |`n|---|---|")
    $many = "BetterClipboard 1.2.3`n`n" + ((1..80 | ForEach-Object { "**Change number $_.** This is the sentence of change $_ and it goes on for a while to take room." }) -join "`n`n")
    $long = ConvertTo-StoreNotes -Markdown $many
    Assert-That 'eighty changes still fit the Store''s 1,500 characters' ($long.Length -le 1500 -and $long.Length -gt 1000 -and $long.StartsWith('• Change number 1.')) "$($long.Length) characters"
    $windows = ConvertTo-StoreNotes -Markdown "BetterClipboard 1.2.3`r`n`r`n**One.** First.`r`n- detail`r`n`r`n**Two.** Second."
    Assert-That 'Windows line endings read the same' ($windows -ceq "• One. First.`n• Two. Second.") $windows
}
finally {
    $listener.Stop()
    try { [void] $runner.EndInvoke($handle) } catch { }
    $runner.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "$($script:failures) of $($script:checks) checks failed." -ForegroundColor Red
    exit 1
}
Write-Host "submit-store.ps1 does what the Store's submission API asks: $($script:checks) checks passed."
exit 0
