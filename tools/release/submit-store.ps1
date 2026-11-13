#requires -Version 7.2
<#
.SYNOPSIS
    Sends a new version of BetterClipboard to the Microsoft Store: a new submission made from the published one, with
    the new packages and "What's new", committed for certification.

.DESCRIPTION
    The release pipeline's last step for the Microsoft Store edition (docs/microsoft-store.md, CLAUDE.md section 3.5).
    It talks to the Microsoft Store submission API (https://manage.devcenter.microsoft.com), the documented way to
    update a packaged (MSIX) app without Partner Center's pages:

      1. signs in as the Microsoft Entra application that the developer account trusts (client credentials);
      2. reads the app; a submission that is still pending is deleted, because the Store allows one at a time;
      3. creates a submission, which the Store fills with a copy of the published one (listings, pricing, age
         ratings, notes for certification: all of that stays as it is);
      4. marks the published packages for removal and lists the new ones; sets "What's new" from the release notes;
      5. uploads the packages (one ZIP, in blocks) to the address the Store gave;
      6. commits the submission and waits until the Store accepted or refused it. Certification itself takes hours
         or days and is not waited for.

    What it cannot do, by Microsoft's rules: the very first submission of an app. That one is made in Partner Center,
    by hand, with the age ratings questionnaire; from then on this script updates the app. Until then it ends with
    exit code 4 and changes nothing.

    Footgun: a submission that this script created must not be edited in Partner Center before it is committed.
    Microsoft: "If you use Partner Center to change a submission that you originally created by using the API, you
    will no longer be able to change or commit that submission by using the API." Delete such a submission there and
    run the script again.

    The credentials come from the environment and are never written anywhere:
      PARTNER_CENTER_TENANT_ID      the Entra tenant linked to the developer account
      PARTNER_CENTER_CLIENT_ID      the Entra application added to the account with the Manager role
      PARTNER_CENTER_CLIENT_SECRET  a client secret of that application
    Without them the script ends with exit code 3 and changes nothing, so a pipeline can treat "not set up" as a
    warning.

.PARAMETER Mode
    Submit (default): everything above. Draft: stops before the commit and leaves the submission pending, to look at
    in Partner Center (look only: see the footgun) or to delete; the next run deletes it. Check: signs in and reads
    the app, to prove the credentials and the product id; changes nothing.

.PARAMETER Version
    The release version (X.Y.Z, as package-msix.ps1 got it). Names the package files. Not needed for -Mode Check.

.PARAMETER PackageDirectory
    The folder with BetterClipboard-<version>-store-<arch>.msix (package-msix.ps1 -ForStore). Default: artifacts/store.

.PARAMETER Architectures
    Which packages to send. Default: x64 and arm64. Every one must exist; a submission with a missing architecture
    would take that architecture's users off updates.

.PARAMETER ProductId
    The app's Store ID. Default: "storeId" of the identity file.

.PARAMETER IdentityFile
    The package identity (packaging/msix/identity.json). Every package must carry exactly this name and publisher:
    a test-signed package has another publisher and is refused here, before anything is sent.

.PARAMETER ReleaseNotesFile
    The release's notes in Markdown (the annotated tag's message). They are shortened to plain text of at most 1,500
    characters, the Store's limit for "What's new in this version". Without the file, "What's new" is left empty.

.PARAMETER NotesLanguage
    The listing language that gets the notes. Default: en-us. Every other language's "What's new" is emptied, so that
    no listing keeps the notes of an older version.

.PARAMETER KeepPending
    Fail instead of deleting a submission that is still pending (for example one somebody is preparing by hand).

.PARAMETER CommitTimeoutMinutes
    How long to wait for the Store to accept or refuse the commit. Default: 15.

.PARAMETER ApiBaseUri
    The submission API's address. Only tests change it (a stand-in on this PC); anything but https is refused unless
    it is a loopback address.

.PARAMETER LoginBaseUri
    The sign-in service's address. Only tests change it, under the same rule.

.OUTPUTS
    Progress lines on the host. Exit code 0: done (Submit: the Store accepted the commit; Draft: the draft is there;
    Check: the app was read). 1: the Store refused or something failed. 2: wrong arguments or packages. 3: the
    credentials are not set. 4: the app has no published submission yet, so the API cannot be used for it.

.EXAMPLE
    pwsh tools/release/submit-store.ps1 -Mode Check
    Proves the three credentials and the product id without changing anything.

.EXAMPLE
    pwsh tools/release/submit-store.ps1 -Version 0.2.11 -ReleaseNotesFile notes.md
    Sends both packages of 0.2.11 and commits the submission.
#>
[CmdletBinding()]
param(
    [ValidateSet('Submit', 'Draft', 'Check')]
    [string] $Mode = 'Submit',

    [string] $Version,

    [string] $PackageDirectory = 'artifacts/store',

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [string] $ProductId,

    [string] $IdentityFile = 'packaging/msix/identity.json',

    [string] $ReleaseNotesFile,

    [string] $NotesLanguage = 'en-us',

    [switch] $KeepPending,

    [ValidateRange(1, 240)]
    [int] $CommitTimeoutMinutes = 15,

    [string] $ApiBaseUri = 'https://manage.devcenter.microsoft.com',

    [string] $LoginBaseUri = 'https://login.microsoftonline.com'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# The Store's limit for "What's new in this version" (Partner Center refuses more).
$NotesLimit = 1500

# Azure Blob Storage takes a blob in blocks; 4 MiB is the largest block every version of its API accepts, and the
# upload address the Store hands out names an old version.
$BlockSize = 4MB

# What the token is asked for: the submission API's own resource id (the same for every caller).
$ApiResource = 'https://manage.devcenter.microsoft.com'

# Submission states. After a commit the Store answers CommitStarted until it has looked at the request.
$WaitingStates = @('CommitStarted')
$FailedStates = @('CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'ReleaseFailed', 'PublishFailed', 'Canceled')

class StoreExit : System.Exception {
    # An ending the script chose, with the exit code that tells a pipeline what happened.
    [int] $Code
    StoreExit([int] $code, [string] $message) : base($message) { $this.Code = $code }
}

function Write-Step {
    <#
    .SYNOPSIS
        Prints one progress line, in the release scripts' "==>" style.
    .PARAMETER Message
        The line. Never a credential, a token or an upload address (which carries its own key).
    .OUTPUTS
        None.
    #>
    param([string] $Message)
    Write-Host "==> $Message"
}

function Assert-Endpoint {
    <#
    .SYNOPSIS
        Refuses an address the credentials or the packages must not be sent to: anything but https, except plain http
        to this PC itself (the tests' stand-in).
    .PARAMETER Uri
        The address.
    .PARAMETER What
        What the address is for, in the error's words.
    .OUTPUTS
        System.Uri. The parsed address.
    #>
    param([string] $Uri, [string] $What)
    $parsed = $null
    if (-not [Uri]::TryCreate($Uri, [UriKind]::Absolute, [ref] $parsed)) {
        throw [StoreExit]::new(2, "$What is not an address: $Uri")
    }
    $secure = $parsed.Scheme -eq 'https'
    $local = $parsed.Scheme -eq 'http' -and $parsed.IsLoopback
    if (-not ($secure -or $local)) {
        throw [StoreExit]::new(2, "$What must be an https address (plain http only on this PC, for tests): $($parsed.Scheme)://$($parsed.Host)")
    }
    return $parsed
}

function Get-MsixIdentity {
    <#
    .SYNOPSIS
        Reads a package's own identity from the manifest inside it, without unpacking it.
    .PARAMETER Path
        The .msix file.
    .OUTPUTS
        A hashtable: Name, Publisher, Version, Architecture, Signed (whether the package carries a signature).
    #>
    param([string] $Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw [StoreExit]::new(2, "$(Split-Path $Path -Leaf) is not an MSIX package: it has no AppxManifest.xml.") }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $manifest = [xml] $reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        return @{
            Name         = [string] $identity.Name
            Publisher    = [string] $identity.Publisher
            Version      = [string] $identity.Version
            Architecture = [string] $identity.ProcessorArchitecture
            Signed       = [bool] $zip.GetEntry('AppxSignature.p7x')
        }
    }
    finally { $zip.Dispose() }
}

function ConvertTo-StoreNotes {
    <#
    .SYNOPSIS
        Shortens a release's Markdown notes to the plain text the Store's "What's new in this version" takes.
    .DESCRIPTION
        The notes follow the releases' format (CLAUDE.md section 3.1): a title line, the install command in a code
        block, one paragraph per change that starts with a bold lead-in, a files table, the code signing note. The
        Store's field is plain text of at most 1,500 characters and says what changed, not how to install. So: the
        title, code blocks, tables and the code signing paragraph are left out; every other paragraph becomes one
        line, its lead-in and its first sentence; Markdown marks are removed. Too long: the lead-ins alone; still too
        long: cut at the last whole line that fits, with an ellipsis.
    .PARAMETER Markdown
        The notes.
    .PARAMETER Limit
        The most characters the result may have.
    .OUTPUTS
        System.String. May be empty when the notes hold nothing to say.
    #>
    param([string] $Markdown, [int] $Limit = 1500)

    $text = ($Markdown -replace "`r`n?", "`n")
    # Fenced code blocks (the install command) go as a whole.
    $text = [regex]::Replace($text, '(?ms)^```.*?^```[ \t]*$', '')
    $paragraphs = @($text -split "`n[ \t]*`n" | ForEach-Object { $_.Trim("`n", ' ', "`t") } | Where-Object { $_ })

    $items = [System.Collections.Generic.List[object]]::new()
    $first = $true
    foreach ($paragraph in $paragraphs) {
        $lines = @($paragraph -split "`n")
        if ($first) {
            $first = $false
            # The title line ("BetterClipboard 1.2.3") says nothing the Store's page does not say already.
            if ($lines.Count -eq 1 -and $lines[0] -match '^\s*#*\s*BetterClipboard\s+v?\d+\.\d+\.\d+\S*\s*$') { continue }
        }
        if (@($lines | Where-Object { $_ -match '^\s*\|' }).Count -eq $lines.Count) { continue }           # a table
        # The note about code signing and SmartScreen is about the downloads; the Store signs its own package.
        if ($paragraph -match '(?i)code[- ]sign|SmartScreen') { continue }
        if ($paragraph -match '^-{3,}$') { continue }                                                     # a rule

        # A paragraph and the bullets under it: the first line group is the paragraph, bullets are details.
        $head = [System.Text.StringBuilder]::new()
        foreach ($line in $lines) {
            if ($line -match '^\s*[-*+]\s+' -and $head.Length -gt 0) { break }
            [void] $head.Append(' ').Append(($line -replace '^\s*[-*+]\s+', '').Trim())
        }
        $plain = $head.ToString().Trim()
        $lead = ''
        if ($plain -match '^\*\*(.+?)\*\*\s*(.*)$') {
            $lead = $Matches[1].Trim()
            $plain = $Matches[2].Trim()
        }

        # Markdown marks: links keep their text, emphasis and code marks go.
        $clean = {
            param($value)
            $value = [regex]::Replace($value, '\[([^\]]+)\]\([^)]*\)', '$1')
            $value = $value -replace '[`*]', ''
            ($value -replace '\s+', ' ').Trim()
        }
        $lead = & $clean $lead
        $plain = & $clean $plain
        $sentence = if ($plain -match '^(.+?[.!?])(\s|$)') { $Matches[1] } else { $plain }
        # A sentence that introduced the bullets ("... with four choices:") stands alone here.
        $sentence = $sentence -replace ':$', '.'
        if (-not $lead -and -not $sentence) { continue }

        # "Fixed" is followed by one bullet per fix and no sentence of its own: say how many.
        $bullets = @($lines | Where-Object { $_ -match '^[-*+]\s+' }).Count
        if ($lead -match '^Fix' -and -not $sentence -and $bullets -gt 0) {
            $lead = "$($lead.TrimEnd('.', ':')):"
            $sentence = if ($bullets -eq 1) { '1 fix.' } else { "$bullets fixes." }
        }
        if ($lead -and $lead -notmatch '[.!?:]$') { $lead += '.' }
        $items.Add(@{ Lead = $lead; Sentence = $sentence })
    }
    if ($items.Count -eq 0) { return '' }

    $long = @($items | ForEach-Object { '• ' + ((@($_.Lead, $_.Sentence) | Where-Object { $_ }) -join ' ') })
    $short = @($items | ForEach-Object { '• ' + $(if ($_.Lead) { $_.Lead -replace ':$', '.' } else { $_.Sentence }) })
    foreach ($candidate in @($long, $short)) {
        $joined = $candidate -join "`n"
        if ($joined.Length -le $Limit) { return $joined }
    }

    # Even the lead-ins are too many: whole lines while they fit, then an ellipsis.
    $kept = [System.Collections.Generic.List[string]]::new()
    $length = 0
    foreach ($line in $short) {
        if ($length + $line.Length + 1 + 1 -gt $Limit) { break }
        $kept.Add($line)
        $length += $line.Length + 1
    }
    return (($kept -join "`n") + "`n…").Trim()
}

function Get-StoreToken {
    <#
    .SYNOPSIS
        Signs in as the Entra application and returns the access token for the submission API (valid for an hour).
    .PARAMETER Http
        The HTTP client.
    .PARAMETER LoginBase
        The sign-in service's address.
    .PARAMETER Tenant
        The tenant id.
    .PARAMETER ClientId
        The application's id.
    .PARAMETER ClientSecret
        The application's secret. It goes into the request's body and nowhere else.
    .OUTPUTS
        System.String. The token; never printed.
    #>
    param([System.Net.Http.HttpClient] $Http, [Uri] $LoginBase, [string] $Tenant, [string] $ClientId, [string] $ClientSecret)

    $form = [System.Collections.Generic.Dictionary[string, string]]::new()
    $form['grant_type'] = 'client_credentials'
    $form['client_id'] = $ClientId
    $form['client_secret'] = $ClientSecret
    $form['resource'] = $ApiResource
    $content = [System.Net.Http.FormUrlEncodedContent]::new($form)
    $uri = [Uri]::new($LoginBase, "/$([Uri]::EscapeDataString($Tenant))/oauth2/token")
    $response = $Http.PostAsync($uri, $content).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        # The sign-in service's own error code says what is wrong (an expired secret, an unknown application). Its
        # long description can quote the request, so only the code and the first sentence are shown.
        $code = 'unknown'
        $description = ''
        try {
            $parsed = [System.Text.Json.Nodes.JsonNode]::Parse($body)
            if ($parsed['error']) { $code = $parsed['error'].ToString() }
            if ($parsed['error_description']) { $description = ($parsed['error_description'].ToString() -split "`r?`n")[0] }
        }
        catch { $description = '' }
        throw [StoreExit]::new(1, "Signing in to the Store's API failed (HTTP $([int] $response.StatusCode), $code). $description".Trim())
    }
    $token = [System.Text.Json.Nodes.JsonNode]::Parse($body)['access_token']
    if (-not $token) { throw [StoreExit]::new(1, 'Signing in to the Store''s API returned no access token.') }
    return $token.ToString()
}

function Invoke-Store {
    <#
    .SYNOPSIS
        One call of the submission API.
    .PARAMETER Http
        The HTTP client.
    .PARAMETER Token
        The access token.
    .PARAMETER Method
        GET, POST, PUT or DELETE.
    .PARAMETER Uri
        The full address.
    .PARAMETER Body
        A JSON body, for PUT.
    .OUTPUTS
        System.String. The response's body (JSON, or empty).
    #>
    param([System.Net.Http.HttpClient] $Http, [string] $Token, [string] $Method, [Uri] $Uri, [string] $Body)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Uri)
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)
    if ($Body) {
        $request.Content = [System.Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json')
    }
    elseif ($Method -in @('POST', 'PUT')) {
        # An empty POST still needs a length, or some front ends answer 411.
        $request.Content = [System.Net.Http.ByteArrayContent]::new([byte[]]::new(0))
    }
    $response = $Http.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $code = ''
        $message = $text
        try {
            $parsed = [System.Text.Json.Nodes.JsonNode]::Parse($text)
            if ($parsed['code']) { $code = $parsed['code'].ToString() }
            if ($parsed['message']) { $message = $parsed['message'].ToString() }
        }
        catch { $message = $text }
        if ($message.Length -gt 600) { $message = $message.Substring(0, 600) + '…' }
        throw [StoreExit]::new(1, "The Store's API refused $Method $($Uri.AbsolutePath) (HTTP $([int] $response.StatusCode) $code): $message")
    }
    return $text
}

function Send-PackageArchive {
    <#
    .SYNOPSIS
        Uploads the ZIP with the packages to the address the Store gave, in blocks, and closes the blob.
    .DESCRIPTION
        The address is a blob with its own access key in the query (a shared access signature). A blob is written as
        numbered blocks and then a list that names them in order; a block that fails is sent again (three tries),
        and nothing counts until the list is written, so a broken upload leaves no half package behind.
    .PARAMETER Http
        The HTTP client.
    .PARAMETER UploadUri
        The address from the submission ("fileUploadUrl"). Never printed: it carries its key.
    .PARAMETER Path
        The ZIP file.
    .OUTPUTS
        None.
    #>
    param([System.Net.Http.HttpClient] $Http, [Uri] $UploadUri, [string] $Path)

    $separator = if ($UploadUri.Query) { '&' } else { '?' }
    $base = $UploadUri.AbsoluteUri
    $stream = [IO.File]::OpenRead($Path)
    $ids = [System.Collections.Generic.List[string]]::new()
    try {
        $buffer = [byte[]]::new($BlockSize)
        $index = 0
        while ($true) {
            # A block is filled completely unless the file ends: a short read in the middle would make short blocks.
            $filled = 0
            while ($filled -lt $buffer.Length) {
                $read = $stream.Read($buffer, $filled, $buffer.Length - $filled)
                if ($read -le 0) { break }
                $filled += $read
            }
            if ($filled -eq 0) { break }

            # Block ids must have the same length within a blob; six digits, base64.
            $id = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('{0:D6}' -f $index))
            $blockUri = [Uri]::new("$base${separator}comp=block&blockid=$([Uri]::EscapeDataString($id))")
            $attempt = 0
            while ($true) {
                $attempt++
                $content = [System.Net.Http.ByteArrayContent]::new($buffer, 0, $filled)
                try {
                    $response = $Http.PutAsync($blockUri, $content).GetAwaiter().GetResult()
                    if ($response.IsSuccessStatusCode) { break }
                    $problem = "HTTP $([int] $response.StatusCode)"
                }
                catch { $problem = $_.Exception.GetBaseException().Message }
                if ($attempt -ge 3) { throw [StoreExit]::new(1, "Uploading the packages failed at block $index ($problem).") }
                Start-Sleep -Seconds (2 * $attempt)
            }
            $ids.Add($id)
            $index++
            if ($filled -lt $buffer.Length) { break }
        }
    }
    finally { $stream.Dispose() }

    $list = '<?xml version="1.0" encoding="utf-8"?><BlockList>' + (($ids | ForEach-Object { "<Latest>$_</Latest>" }) -join '') + '</BlockList>'
    $listContent = [System.Net.Http.StringContent]::new($list, [Text.Encoding]::UTF8, 'application/xml')
    $response = $Http.PutAsync([Uri]::new("$base${separator}comp=blocklist"), $listContent).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw [StoreExit]::new(1, "Closing the uploaded packages failed (HTTP $([int] $response.StatusCode)).")
    }
}

function Get-StatusProblems {
    <#
    .SYNOPSIS
        The errors and warnings of a submission's status, as lines.
    .PARAMETER Status
        The status resource (JSON node).
    .OUTPUTS
        System.String[]. "error <code>: <details>" and "warning <code>: <details>" lines; empty when there are none.
    #>
    param($Status)
    $lines = [System.Collections.Generic.List[string]]::new()
    $details = $Status['statusDetails']
    if ($details) {
        foreach ($kind in @('errors', 'warnings')) {
            $entries = $details[$kind]
            if (-not $entries) { continue }
            foreach ($entry in $entries) {
                $code = if ($entry['code']) { $entry['code'].ToString() } else { '' }
                $text = if ($entry['details']) { $entry['details'].ToString() } else { '' }
                $lines.Add("$($kind.TrimEnd('s')) ${code}: $text")
            }
        }
    }
    return , $lines.ToArray()
}

# ---------------------------------------------------------------------------------------------------------------------

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exitCode = 0
$http = $null
$archive = $null
try {
    $api = Assert-Endpoint $ApiBaseUri 'The submission API (-ApiBaseUri)'
    $login = Assert-Endpoint $LoginBaseUri 'The sign-in service (-LoginBaseUri)'

    # --- What to send, checked before anything leaves this PC --------------------------------------------------------
    $identityPath = if ([IO.Path]::IsPathRooted($IdentityFile)) { $IdentityFile } else { Join-Path $root $IdentityFile }
    if (-not (Test-Path -LiteralPath $identityPath)) { throw [StoreExit]::new(2, "The identity file does not exist: $identityPath") }
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    if (-not $ProductId) { $ProductId = [string] $identity.PSObject.Properties['storeId']?.Value }
    if ($ProductId -notmatch '^[0-9A-Za-z]{12}$') {
        throw [StoreExit]::new(2, "No Store ID: pass -ProductId, or put ""storeId"" into $identityPath (Partner Center > Product identity > Store ID, 12 letters and digits).")
    }

    $packages = @()
    $notes = ''
    if ($Mode -ne 'Check') {
        if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') { throw [StoreExit]::new(2, '-Version must be the release version (X.Y.Z).') }
        if ($Version -match '-') { throw [StoreExit]::new(2, "A pre-release ($Version) is not sent to the Store: its customers get every published version.") }
        $directory = if ([IO.Path]::IsPathRooted($PackageDirectory)) { $PackageDirectory } else { Join-Path $root $PackageDirectory }
        $versions = @{}
        foreach ($architecture in $Architectures) {
            $file = Join-Path $directory "BetterClipboard-$Version-store-$architecture.msix"
            if (-not (Test-Path -LiteralPath $file)) {
                throw [StoreExit]::new(2, "The $architecture package is missing: $file (build it with tools/release/package-msix.ps1 -Version $Version -ForStore).")
            }
            $own = Get-MsixIdentity $file
            if ($own.Name -cne [string] $identity.name -or $own.Publisher -cne [string] $identity.publisher) {
                throw [StoreExit]::new(2, "$(Split-Path $file -Leaf) is not the Store's package: its identity is $($own.Name) / $($own.Publisher), the Store's is $($identity.name) / $($identity.publisher). A test-signed package cannot be submitted; build with -ForStore.")
            }
            if ($own.Signed) { throw [StoreExit]::new(2, "$(Split-Path $file -Leaf) is signed. The Store signs what it publishes; build with -ForStore.") }
            if ($own.Architecture -ne $architecture) { throw [StoreExit]::new(2, "$(Split-Path $file -Leaf) is built for $($own.Architecture), not $architecture.") }
            $versions[$own.Version] = $true
            $packages += [pscustomobject]@{ Path = $file; Name = (Split-Path $file -Leaf); Version = $own.Version; Architecture = $architecture }
        }
        if ($versions.Count -ne 1) { throw [StoreExit]::new(2, "The packages disagree about their version: $($versions.Keys -join ', ').") }

        if ($ReleaseNotesFile) {
            if (-not (Test-Path -LiteralPath $ReleaseNotesFile)) { throw [StoreExit]::new(2, "The release notes file does not exist: $ReleaseNotesFile") }
            $notes = ConvertTo-StoreNotes -Markdown (Get-Content -LiteralPath $ReleaseNotesFile -Raw -Encoding utf8) -Limit $NotesLimit
        }
    }

    # --- Credentials ---------------------------------------------------------------------------------------------------
    $tenant = $env:PARTNER_CENTER_TENANT_ID
    $clientId = $env:PARTNER_CENTER_CLIENT_ID
    $clientSecret = $env:PARTNER_CENTER_CLIENT_SECRET
    $missing = @(
        if (-not $tenant) { 'PARTNER_CENTER_TENANT_ID' }
        if (-not $clientId) { 'PARTNER_CENTER_CLIENT_ID' }
        if (-not $clientSecret) { 'PARTNER_CENTER_CLIENT_SECRET' }
    )
    if ($missing.Count -gt 0) {
        throw [StoreExit]::new(3, "The Store's API is not set up here: $($missing -join ', ') $(if ($missing.Count -eq 1) { 'is' } else { 'are' }) not set (docs/microsoft-store.md, ""The release pipeline""). Nothing was sent.")
    }

    $http = [System.Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromMinutes(10)
    $http.DefaultRequestHeaders.UserAgent.ParseAdd('BetterClipboard-release')

    Write-Step 'Signing in to the Microsoft Store submission API'
    $token = Get-StoreToken -Http $http -LoginBase $login -Tenant $tenant -ClientId $clientId -ClientSecret $clientSecret

    $appUri = [Uri]::new($api, "/v1.0/my/applications/$ProductId")
    $app = [System.Text.Json.Nodes.JsonNode]::Parse((Invoke-Store -Http $http -Token $token -Method GET -Uri $appUri))
    $appName = if ($app['primaryName']) { $app['primaryName'].ToString() } else { '(no name)' }
    $published = $app['lastPublishedApplicationSubmission']
    $pending = $app['pendingApplicationSubmission']
    Write-Step "App $ProductId is ""$appName"": $(if ($published) { 'it has a published submission' } else { 'nothing is published yet' })$(if ($pending) { ', and a submission is pending' })"

    # The versions the Store serves now. A package that is not higher would be refused after the upload, with a
    # pending submission left behind; asked here, nothing is created for it.
    $publishedVersions = @()
    if ($published) {
        $publishedText = Invoke-Store -Http $http -Token $token -Method GET -Uri ([Uri]::new($api, "/v1.0/my/applications/$ProductId/submissions/$($published['id'])"))
        $publishedPackages = [System.Text.Json.Nodes.JsonNode]::Parse($publishedText)['applicationPackages']
        if ($publishedPackages) {
            foreach ($entry in $publishedPackages) {
                $parsedVersion = $null
                if ($entry['version'] -and [version]::TryParse($entry['version'].ToString(), [ref] $parsedVersion)) { $publishedVersions += $parsedVersion }
            }
        }
        Write-Step "Published package version(s): $(if ($publishedVersions) { ($publishedVersions | Sort-Object -Unique) -join ', ' } else { 'none listed' })"
    }

    if ($Mode -eq 'Check') {
        Write-Step 'The credentials and the product id work. Nothing was changed.'
        if (-not $published) { Write-Step 'Updates through this script start once the first submission, made in Partner Center, is published.' }
        return
    }

    if (-not $published) {
        throw [StoreExit]::new(4, "App $ProductId has no published submission yet. Microsoft's API cannot make an app's first submission: make it in Partner Center (docs/microsoft-store.md); from the next release on, this step updates the app. Nothing was sent.")
    }

    $newVersion = [version] $packages[0].Version
    $notLower = @($publishedVersions | Where-Object { $_ -ge $newVersion } | Sort-Object -Unique)
    if ($notLower.Count -gt 0) {
        throw [StoreExit]::new(1, "The Store already serves package version $($notLower -join ', '), and this release's packages are $newVersion. The Store only takes a higher version (tools/release/package-msix.ps1 derives it from the release version). Nothing was sent.")
    }

    if ($pending) {
        $pendingId = $pending['id'].ToString()
        if ($KeepPending) {
            throw [StoreExit]::new(1, "A submission ($pendingId) is still pending and -KeepPending was given. Finish or delete it in Partner Center, then run again.")
        }
        Write-Step "Deleting the pending submission $pendingId (the Store allows one at a time)"
        [void] (Invoke-Store -Http $http -Token $token -Method DELETE -Uri ([Uri]::new($api, "/v1.0/my/applications/$ProductId/submissions/$pendingId")))
    }

    Write-Step 'Creating a submission from the published one'
    $submissionText = Invoke-Store -Http $http -Token $token -Method POST -Uri ([Uri]::new($api, "/v1.0/my/applications/$ProductId/submissions"))
    $submission = [System.Text.Json.Nodes.JsonNode]::Parse($submissionText)
    $submissionId = $submission['id'].ToString()
    $uploadAddress = if ($submission['fileUploadUrl']) { $submission['fileUploadUrl'].ToString() } else { '' }
    $upload = Assert-Endpoint $uploadAddress 'The upload address the Store gave'

    # Packages: the published ones go, the new ones come. The Store reads everything else about a package itself.
    $list = $submission['applicationPackages']
    if (-not $list) {
        $list = [System.Text.Json.Nodes.JsonArray]::new()
        $submission['applicationPackages'] = $list
    }
    foreach ($entry in $list) {
        $entry['fileStatus'] = [System.Text.Json.Nodes.JsonValue]::Create('PendingDelete')
    }
    foreach ($package in $packages) {
        $node = [System.Text.Json.Nodes.JsonObject]::new()
        $node['fileName'] = [System.Text.Json.Nodes.JsonValue]::Create($package.Name)
        $node['fileStatus'] = [System.Text.Json.Nodes.JsonValue]::Create('PendingUpload')
        $node['minimumDirectXVersion'] = [System.Text.Json.Nodes.JsonValue]::Create('None')
        $node['minimumSystemRam'] = [System.Text.Json.Nodes.JsonValue]::Create('None')
        $list.Add($node)
    }

    # "What's new": this release's notes in one language, nothing in the others (never an older version's notes).
    $listings = $submission['listings']
    $languages = @()
    if ($listings) {
        foreach ($listing in @($listings.AsObject())) {
            $base = $listing.Value['baseListing']
            if (-not $base) { continue }
            $languages += $listing.Key
            $text = if ($listing.Key -ieq $NotesLanguage) { $notes } else { '' }
            $base['releaseNotes'] = [System.Text.Json.Nodes.JsonValue]::Create($text)
        }
    }
    if ($notes -and @($languages | Where-Object { $_ -ieq $NotesLanguage }).Count -eq 0) {
        Write-Warning "The listing has no language '$NotesLanguage' (it has: $($languages -join ', ')), so ""What's new"" stays empty."
    }

    Write-Step "Updating submission $submissionId`: $($packages.Count) package(s) of version $newVersion, ""What's new"" of $($notes.Length) character(s) in $NotesLanguage"
    $submissionUri = [Uri]::new($api, "/v1.0/my/applications/$ProductId/submissions/$submissionId")
    [void] (Invoke-Store -Http $http -Token $token -Method PUT -Uri $submissionUri -Body $submission.ToJsonString())

    # One ZIP with the packages at its root, as the submission names them. MSIX is compressed already.
    $archive = Join-Path ([IO.Path]::GetTempPath()) "betterclipboard-store-$([Guid]::NewGuid().ToString('N')).zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($package in $packages) {
            [void] [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $package.Path, $package.Name, [IO.Compression.CompressionLevel]::NoCompression)
        }
    }
    finally { $zip.Dispose() }
    Write-Step "Uploading the packages ($([math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 1)) MB)"
    Send-PackageArchive -Http $http -UploadUri $upload -Path $archive

    if ($Mode -eq 'Draft') {
        Write-Step "Submission $submissionId is a draft in Partner Center; it was not committed. Look at it there without changing it, or delete it; the next run deletes it."
        return
    }

    Write-Step 'Committing the submission'
    [void] (Invoke-Store -Http $http -Token $token -Method POST -Uri ([Uri]::new($api, "/v1.0/my/applications/$ProductId/submissions/$submissionId/commit")))

    # The Store looks at the request (the ZIP, the packages' identity and version) before certification starts.
    $deadline = [DateTime]::UtcNow.AddMinutes($CommitTimeoutMinutes)
    $state = 'CommitStarted'
    $status = $null
    while ($true) {
        $status = [System.Text.Json.Nodes.JsonNode]::Parse((Invoke-Store -Http $http -Token $token -Method GET -Uri ([Uri]::new($api, "/v1.0/my/applications/$ProductId/submissions/$submissionId/status"))))
        $state = if ($status['status']) { $status['status'].ToString() } else { 'None' }
        if ($state -notin $WaitingStates) { break }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw [StoreExit]::new(1, "The Store did not answer the commit of submission $submissionId within $CommitTimeoutMinutes minutes (still $state). Look at it in Partner Center.")
        }
        Start-Sleep -Seconds $(if ($api.IsLoopback) { 1 } else { 30 })
    }

    foreach ($line in (Get-StatusProblems $status)) { Write-Host "    $line" }
    if ($state -in $FailedStates) {
        throw [StoreExit]::new(1, "The Store refused submission $submissionId ($state). It stays in Partner Center, where it can be deleted.")
    }
    Write-Step "Submission $submissionId was accepted ($state). Certification follows; Partner Center mails the result. https://partner.microsoft.com/dashboard/products/$ProductId/overview"
}
catch [StoreExit] {
    $exitCode = $_.Exception.Code
    # 3 and 4 are "not yet", for a pipeline to report as a warning; the rest are failures.
    if ($exitCode -in @(3, 4)) { Write-Warning $_.Exception.Message } else { Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red }
}
catch {
    $exitCode = 1
    Write-Host "ERROR: $($_.Exception.GetBaseException().Message)" -ForegroundColor Red
}
finally {
    if ($http) { $http.Dispose() }
    if ($archive -and (Test-Path -LiteralPath $archive)) { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
