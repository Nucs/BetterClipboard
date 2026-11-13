<#
.SYNOPSIS
    Builds BetterClipboard's Microsoft Store packages (MSIX), one per architecture, and optionally signs them with a
    test certificate so they can be installed for a check before a submission.

.DESCRIPTION
    The Microsoft Store edition (CLAUDE.md §2.47, docs/microsoft-store.md) is the same app as a full-trust MSIX
    package. For every architecture this script:

      1. publishes bclip (the command line) self-contained and takes its own four files: the package declares bclip as
         the app's console alias (an app execution alias with an executable of its own), and its runtime files are
         the app's own;
      2. fills in the manifest (packaging/msix/Package.appxmanifest) with the identity, the version and the
         architecture;
      3. publishes the app with WindowsPackageType=MSIX, which makes the package: the app, bclip, the logos
         (packaging/msix/Assets) and install.ps1, which that edition runs in its uninstall mode to remove a copy of
         BetterClipboard it replaces;
      4. checks the package (its manifest says what was asked, and declares nothing the Store refuses at upload: one
         application, none hidden from Start, bclip as its console alias; the app, bclip, the installer script and
         the resource index are in it; everything bclip needs is in it; it is under the Store's size limit);
      5. with a test certificate, signs it.

    Output: BetterClipboard-<version>-store-<arch>.msix in -OutputDirectory, and with a new test certificate its
    .pfx and .cer next to them.

    Two kinds of package:

      For the Store (-ForStore). Unsigned: the Store signs what it publishes. The identity must be the one Partner
      Center assigned when the app's name was reserved (packaging/msix/identity.json with "reserved": true, or the
      three -Identity… parameters); the Store refuses anything else, so this script refuses first.

      For a test (-TestCertificate or -NewTestCertificate). Signed with a self-signed certificate, whose subject
      becomes the package's publisher (Windows refuses a package whose publisher and signer differ). To install it on
      a test PC, trust the .cer there first (as administrator):
          Import-Certificate -FilePath BetterClipboard-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
          Add-AppxPackage BetterClipboard-<version>-store-x64.msix
      Only ever on a disposable test PC: that trust lets anything signed with the test certificate install there.

    Without either, the packages are unsigned test packages with the placeholder identity: good for looking inside,
    and for registering their unpacked folder on a PC in Developer Mode.

    The package version is (X+1).Y.Z.0 for the release X.Y.Z: 0.2.11 becomes 1.2.11.0. The Store asks for four
    numbers, keeps the fourth for itself (it must be 0) and refuses a first number of 0, and BetterClipboard's
    releases are 0.x. One is added to the first number for every release, for good: a rule that stopped adding at
    1.0.0 would make that package (1.0.0.0) older than the last 0.x one (1.2.11.0), and the Store only ever offers
    the highest version. A pre-release suffix has no place in a package version. The app itself still carries and
    shows the release's own version; only Windows' list of installed apps shows the package's.

    Needs the .NET SDK and the restored NuGet packages; MakeAppx and SignTool come from the
    Microsoft.Windows.SDK.BuildTools package the app already uses (or from an installed Windows SDK).

.PARAMETER Version
    Semantic version without the leading 'v' (e.g. 0.2.11); stamped into the assemblies. The package gets
    (X+1).Y.Z.0 (see the description).

.PARAMETER Architectures
    Any of x64, arm64. Default: both.

.PARAMETER OutputDirectory
    Where the packages go (created; packages of the same name are replaced). Default: artifacts/store.

.PARAMETER IdentityFile
    The JSON file with the package identity. Default: packaging/msix/identity.json.

.PARAMETER IdentityName
    Overrides the identity file's "name" (Partner Center: Package/Identity/Name).

.PARAMETER Publisher
    Overrides the identity file's "publisher" (Partner Center: Package/Identity/Publisher, "CN=…"). Ignored with a test
    certificate, whose subject is used.

.PARAMETER PublisherDisplayName
    Overrides the identity file's "publisherDisplayName" (Partner Center: Package/Properties/PublisherDisplayName).

.PARAMETER DisplayName
    Overrides the identity file's "displayName" (the reserved app name).

.PARAMETER ForStore
    Build the packages for a submission: refuse a placeholder identity and a test certificate.

.PARAMETER TestCertificate
    A .pfx file to sign the packages with (its subject becomes the publisher).

.PARAMETER TestCertificatePassword
    The .pfx file's password. A test certificate protects nothing, so this is plain text. Default: the password this
    script gives a certificate it makes.

.PARAMETER NewTestCertificate
    Make a new self-signed test certificate (valid 30 days) in -OutputDirectory and sign with it. Nothing is added
    to any certificate store on this PC.

.EXAMPLE
    pwsh tools/release/package-msix.ps1 -Version 0.2.11 -Architectures x64 -NewTestCertificate

.EXAMPLE
    pwsh tools/release/package-msix.ps1 -Version 0.2.11 -ForStore

.NOTES
    Windows PowerShell 5.1 and PowerShell 7.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [string] $OutputDirectory = 'artifacts/store',

    [string] $IdentityFile = 'packaging/msix/identity.json',

    [string] $IdentityName,

    [string] $Publisher,

    [string] $PublisherDisplayName,

    [string] $DisplayName,

    [switch] $ForStore,

    [string] $TestCertificate,

    [string] $TestCertificatePassword = 'BetterClipboard-test',

    [switch] $NewTestCertificate
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# With -File, PowerShell hands "x64,arm64" over as one text, not as a list: split it here so both spellings work.
$Architectures = @($Architectures | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $root 'src\BetterClipboard.App\BetterClipboard.App.csproj'
$cliProject = Join-Path $root 'src\BetterClipboard.Cli\BetterClipboard.Cli.csproj'
$manifestTemplate = Join-Path $root 'packaging\msix\Package.appxmanifest'
# A relative output folder is relative to the repository, whatever folder the script is called from.
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
$work = Join-Path $root 'artifacts\store-build\stage'

# The Store's limit for one package of a desktop app is far above this; a package this size means something else got in.
$maximumPackageBytes = 250MB

# What bclip adds to the app's folder. Everything else in its self-contained publish is the .NET runtime and the
# libraries the app ships itself; step 4 proves that.
$bclipOwnFiles = @('bclip.exe', 'bclip.dll', 'bclip.deps.json', 'bclip.runtimeconfig.json')

# Files that ride along in the package root next to bclip (see the description).
$extraRootFiles = @('install.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.md')

function Get-SdkTool {
    <#
    .SYNOPSIS
        Full path of a Windows SDK command-line tool (MakeAppx.exe, SignTool.exe).
    .DESCRIPTION
        Looks first in the Microsoft.Windows.SDK.BuildTools NuGet package, which the app's build restores on every
        machine (so a build machine needs no Windows SDK installed), then in an installed Windows SDK. Takes the
        newest version found. The x64 tools: they also run on ARM64 Windows.
    .PARAMETER Name
        The tool's file name.
    .OUTPUTS
        System.String.
    .NOTES
        Throws when the tool is nowhere: signing or inspecting a package is then impossible.
    #>
    param([Parameter(Mandatory = $true)] [string] $Name)

    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $candidates = @()
    $buildTools = Join-Path $packages 'microsoft.windows.sdk.buildtools'
    if (Test-Path -LiteralPath $buildTools) {
        $candidates += @(Get-ChildItem -LiteralPath $buildTools -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' })
    }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $candidates += @(Get-ChildItem -LiteralPath $kits -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' })
    }

    $newest = $candidates | Sort-Object { [version] $_.VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
    if (-not $newest) {
        throw "Could not find $Name. Restore the solution (dotnet restore) so the Microsoft.Windows.SDK.BuildTools package is there, or install the Windows SDK."
    }

    return $newest.FullName
}

function New-TestCertificate {
    <#
    .SYNOPSIS
        Makes a self-signed code-signing certificate for test packages and writes its .pfx and .cer.
    .DESCRIPTION
        Made in memory and written to two files: nothing is added to a certificate store of this PC. The .cer (the
        public part) is what a test PC is told to trust; the .pfx (with the private key) signs the packages. Valid
        for 30 days: a test certificate that is trusted somewhere should not stay useful for long.
    .PARAMETER Subject
        The certificate's subject, which becomes the package's publisher.
    .PARAMETER PfxPath
        Where the .pfx goes.
    .PARAMETER CerPath
        Where the .cer goes.
    .PARAMETER Password
        The .pfx file's password.
    .OUTPUTS
        None.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Subject,
        [Parameter(Mandatory = $true)] [string] $PfxPath,
        [Parameter(Mandatory = $true)] [string] $CerPath,
        [Parameter(Mandatory = $true)] [string] $Password
    )

    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
            $Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

        # What Windows asks of a certificate that signs a package: not a certificate authority, usable for digital
        # signatures, and for code signing.
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)))
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
        $usages = New-Object System.Security.Cryptography.OidCollection
        [void] $usages.Add((New-Object System.Security.Cryptography.Oid('1.3.6.1.5.5.7.3.3')))
        $request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($usages, $true)))

        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
        try {
            [IO.File]::WriteAllBytes($PfxPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password))
            [IO.File]::WriteAllBytes($CerPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $rsa.Dispose()
    }
}

function Get-CertificateSubject {
    <#
    .SYNOPSIS
        The subject of a .pfx file's certificate, as Windows compares it with a package's publisher.
    .PARAMETER PfxPath
        The .pfx file.
    .PARAMETER Password
        Its password.
    .OUTPUTS
        System.String.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $PfxPath,
        [Parameter(Mandatory = $true)] [string] $Password
    )

    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $Password)
    try {
        return $certificate.Subject
    }
    finally {
        $certificate.Dispose()
    }
}

function Get-PackageFamilyName {
    <#
    .SYNOPSIS
        The package family name Windows gives a package with this name and publisher: <name>_<publisher id>.
    .DESCRIPTION
        The publisher id is the first 8 bytes of the SHA-256 of the publisher string (UTF-16LE), written in base 32
        with Windows' own alphabet (13 characters). It is how Windows, the Store and the app's data folder
        (%LOCALAPPDATA%\Packages\<family name>) name the package, so it proves a copied identity.
    .PARAMETER Name
        Package/Identity/Name.
    .PARAMETER Publisher
        Package/Identity/Publisher, exactly as in the manifest (case and spacing count).
    .OUTPUTS
        System.String.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $Publisher
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($Publisher)) }
    finally { $sha.Dispose() }

    # 64 bits, padded with one zero bit to 65: thirteen groups of five.
    $bits = (-join ($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') })) + '0'
    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    $id = -join (0..12 | ForEach-Object { $alphabet[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)] })
    return "${Name}_$id"
}

function ConvertTo-XmlText {
    <#
    .SYNOPSIS
        Escapes a text for use inside an XML attribute or element.
    .PARAMETER Text
        The text.
    .OUTPUTS
        System.String.
    #>
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Text)
    return [System.Security.SecurityElement]::Escape($Text)
}

function Get-PackageEntries {
    <#
    .SYNOPSIS
        The file names inside a package, as written in its manifest's sense (unescaped, backslashes).
    .DESCRIPTION
        A package is a zip whose entry names are percent-encoded where a name has a space or another special
        character; they are decoded here so they compare with file names.
    .PARAMETER PackagePath
        The .msix file.
    .OUTPUTS
        System.String[].
    #>
    param([Parameter(Mandatory = $true)] [string] $PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        return @($zip.Entries | ForEach-Object { [Uri]::UnescapeDataString($_.FullName).Replace('/', '\') })
    }
    finally {
        $zip.Dispose()
    }
}

function Read-PackageManifest {
    <#
    .SYNOPSIS
        The manifest inside a package, parsed.
    .PARAMETER PackagePath
        The .msix file.
    .OUTPUTS
        System.Xml.XmlDocument.
    #>
    param([Parameter(Mandatory = $true)] [string] $PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        if (-not $entry) { throw "$PackagePath has no AppxManifest.xml." }
        $reader = New-Object IO.StreamReader($entry.Open())
        try {
            $document = New-Object System.Xml.XmlDocument
            $document.LoadXml($reader.ReadToEnd())
            return $document
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Get-ManifestStoreProblems {
    <#
    .SYNOPSIS
        What in a package's manifest the Microsoft Store refuses at upload, or what would leave bclip unreachable.
    .DESCRIPTION
        Partner Center refused the first packages of this edition the moment they were uploaded ("specifies a
        headless app"): bclip was a second application hidden from Start (AppListEntry="none"), which the Store
        allows only with a waiver. MakeAppx had packed that manifest without a word, so the build checks the rule
        itself, where a mistake costs seconds instead of an upload:

          - exactly one application, and no application hidden from Start;
          - that application's one execution alias is the console alias bclip.exe, started from bclip.exe (Windows
            allows one windows.appExecutionAlias extension per application, which MakeAppx does enforce; this check
            is about the one that is there being the command line's).
    .PARAMETER Manifest
        The parsed AppxManifest.xml of a built package (Read-PackageManifest).
    .OUTPUTS
        System.String[]. One sentence fragment per problem; empty when the manifest is fine.
    #>
    param([Parameter(Mandatory = $true)] [System.Xml.XmlDocument] $Manifest)

    $namespaces = New-Object System.Xml.XmlNamespaceManager($Manifest.NameTable)
    $namespaces.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $namespaces.AddNamespace('uap5', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/5')
    $namespaces.AddNamespace('desktop4', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/4')

    $found = @()
    $applications = @($Manifest.SelectNodes('/m:Package/m:Applications/m:Application', $namespaces))
    if ($applications.Count -ne 1) {
        $found += "the manifest declares $($applications.Count) applications; the Store edition is one application (a second, hidden one is what the Store refuses as a headless app)"
    }

    # Case-insensitive on purpose: the schema accepts "none" only, but a refusal at upload is the wrong place to learn
    # that a differently written value slipped through.
    $hidden = @($Manifest.SelectNodes('//uap:VisualElements[@AppListEntry]', $namespaces) |
        Where-Object { $_.GetAttribute('AppListEntry') -ieq 'none' })
    if ($hidden.Count -gt 0) {
        $found += 'an application is hidden from Start (AppListEntry="none"), which the Store refuses as a headless app'
    }

    $aliases = @($Manifest.SelectNodes('/m:Package/m:Applications/m:Application/m:Extensions/uap5:Extension[@Category="windows.appExecutionAlias"]', $namespaces))
    if ($aliases.Count -ne 1) {
        $found += "the manifest declares $($aliases.Count) execution alias extensions; bclip's console alias must be the one"
    }
    else {
        $alias = $aliases[0]
        $names = @($alias.SelectNodes('uap5:AppExecutionAlias/uap5:ExecutionAlias', $namespaces) | ForEach-Object { $_.GetAttribute('Alias') })
        $holder = $alias.SelectSingleNode('uap5:AppExecutionAlias', $namespaces)
        $subsystem = if ($holder) { $holder.GetAttribute('Subsystem', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/4') } else { '' }
        if ($alias.GetAttribute('Executable') -ine 'bclip.exe') { $found += "the execution alias starts '$($alias.GetAttribute('Executable'))', not bclip.exe" }
        if ($names.Count -ne 1 -or $names[0] -ine 'bclip.exe') { $found += "the execution alias is '$($names -join ', ')', not bclip.exe alone" }
        if ($subsystem -ine 'console') { $found += 'the execution alias is not a console alias (desktop4:Subsystem="console"), so a terminal would not wait for bclip or get its output' }
    }

    return $found
}

# --- The identity ----------------------------------------------------------------------------------------------------
$identityPath = if ([IO.Path]::IsPathRooted($IdentityFile)) { $IdentityFile } else { Join-Path $root $IdentityFile }
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
$name = if ($IdentityName) { $IdentityName } else { [string] $identity.name }
$publisherSubject = if ($Publisher) { $Publisher } else { [string] $identity.publisher }
$publisherName = if ($PublisherDisplayName) { $PublisherDisplayName } else { [string] $identity.publisherDisplayName }
$appName = if ($DisplayName) { $DisplayName } else { [string] $identity.displayName }
$reserved = [bool] $identity.reserved -or ($IdentityName -and $Publisher -and $PublisherDisplayName)

if ($ForStore) {
    if ($TestCertificate -or $NewTestCertificate) {
        throw 'A package for the Store is not signed with a test certificate: the Store signs it. Leave out -ForStore for a test package.'
    }

    # The identity file also states the package family name Partner Center shows. It follows from the name and the
    # publisher, so a name or publisher that was mistyped or edited later is caught here, not by the Store after the
    # upload (it refuses a package whose identity is not the reserved one).
    $statedFamily = $identity.PSObject.Properties['packageFamilyName']
    if ($statedFamily -and $statedFamily.Value -and -not ($IdentityName -or $Publisher)) {
        $computedFamily = Get-PackageFamilyName -Name $name -Publisher $publisherSubject
        if ($computedFamily -cne [string] $statedFamily.Value) {
            throw "The identity in $identityPath does not fit its own ""packageFamilyName"": name and publisher give $computedFamily, the file says $($statedFamily.Value). Copy Package/Identity/Name and Package/Identity/Publisher again from Partner Center > Product identity."
        }
    }

    if (-not $reserved) {
        throw "The package identity is still the placeholder. Reserve the app's name in Partner Center, copy the three identity values into $identityPath and set ""reserved"" to true (docs/microsoft-store.md), or pass -IdentityName, -Publisher and -PublisherDisplayName."
    }
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$pfx = $null
if ($NewTestCertificate) {
    $pfx = Join-Path $output 'BetterClipboard-test.pfx'
    $cer = Join-Path $output 'BetterClipboard-test.cer'
    New-TestCertificate -Subject 'CN=BetterClipboard Test Package (not for distribution)' -PfxPath $pfx -CerPath $cer -Password $TestCertificatePassword
    Write-Host "==> Test certificate: $cer (trust it on the test PC), $pfx" -ForegroundColor Cyan
}
elseif ($TestCertificate) {
    $pfx = (Resolve-Path -LiteralPath $TestCertificate).Path
}

if ($pfx) {
    # Windows installs a package only when its publisher is its signer.
    $publisherSubject = Get-CertificateSubject -PfxPath $pfx -Password $TestCertificatePassword
}

# (X+1).Y.Z.0: the release's three numbers, the first plus one (the Store refuses a first number of 0; see the
# description for why one is added for every release), and the fourth left to the Store.
$numbers = @((($Version -replace '[-+].*$', '') -split '\.') | ForEach-Object { [int] $_ })
$numbers[0] = $numbers[0] + 1
if (@($numbers | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw "The version $Version cannot be a package version: each of its numbers must be at most 65535 (the first one at most 65534)."
}

$packageVersion = '{0}.{1}.{2}.0' -f $numbers[0], $numbers[1], $numbers[2]
$platforms = @{ x64 = 'x64'; arm64 = 'ARM64' }
$packages = @()

foreach ($arch in $Architectures) {
    $stage = Join-Path $work $arch
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    $bclipFull = Join-Path $stage 'bclip'
    $extras = Join-Path $stage 'extras'
    $appx = Join-Path $stage 'appx'
    New-Item -ItemType Directory -Path $bclipFull, $extras, $appx -Force | Out-Null

    # 1. bclip, self-contained like the app, so it runs on the app's own runtime files in the package.
    Write-Host "==> Publishing bclip win-$arch $Version" -ForegroundColor Cyan
    & dotnet publish $cliProject -c Release -r "win-$arch" --self-contained true "-p:Version=$Version" -p:DebugType=none -o $bclipFull --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of bclip failed for win-$arch (exit $LASTEXITCODE)." }
    foreach ($file in $bclipOwnFiles) {
        Copy-Item -LiteralPath (Join-Path $bclipFull $file) -Destination $extras
    }

    foreach ($file in $extraRootFiles) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $extras
    }

    # 2. The manifest for this package.
    $manifest = Join-Path $stage 'Package.appxmanifest'
    $text = [IO.File]::ReadAllText($manifestTemplate)

    # The template's comments explain it to whoever edits it; the package that ships needs none of them.
    $text = [regex]::Replace($text, '(?s)<!--.*?-->\s*', '')
    $tokens = @{
        '{{Name}}'                 = $name
        '{{Publisher}}'            = $publisherSubject
        '{{PublisherDisplayName}}' = $publisherName
        '{{DisplayName}}'          = $appName
        '{{Version}}'              = $packageVersion
        '{{Architecture}}'         = $arch
    }
    foreach ($token in $tokens.Keys) {
        $text = $text.Replace($token, (ConvertTo-XmlText $tokens[$token]))
    }

    # Anything of the tokens' form that is left over is a token nobody filled.
    if ($text -match '\{\{[A-Za-z]+\}\}') { throw "The manifest template has a token this script does not fill: $($Matches[0])." }
    [IO.File]::WriteAllText($manifest, $text, (New-Object System.Text.UTF8Encoding($false)))

    # 3. The app as a package: PublishAppxPackage makes the package after the publish step, from the published files.
    #    The trailing backslashes belong to the folder properties; "\\" keeps them from escaping the closing quote of
    #    the argument.
    Write-Host "==> Packaging win-$arch $packageVersion ($name)" -ForegroundColor Cyan
    & dotnet publish $project -c Release -r "win-$arch" --self-contained true "-p:Platform=$($platforms[$arch])" `
        "-p:Version=$Version" -p:DebugType=none -p:WindowsPackageType=MSIX -p:PublishAppxPackage=true `
        "-p:StoreAppxManifest=$manifest" "-p:StoreExtraFilesDir=$extras\\" "-p:AppxPackageDir=$appx\\" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed for win-$arch (exit $LASTEXITCODE)." }

    $built = @(Get-ChildItem -LiteralPath $appx -Recurse -Filter '*.msix')
    if ($built.Count -ne 1) { throw "Expected one package under $appx, found $($built.Count)." }
    $package = Join-Path $output "BetterClipboard-$Version-store-$arch.msix"
    Copy-Item -LiteralPath $built[0].FullName -Destination $package -Force

    # 4. The package is what was asked for.
    $document = Read-PackageManifest -PackagePath $package
    $made = $document.Package.Identity
    $problems = @()
    if ($made.Name -ne $name) { $problems += "identity name is '$($made.Name)', not '$name'" }
    if ($made.Publisher -ne $publisherSubject) { $problems += "publisher is '$($made.Publisher)', not '$publisherSubject'" }
    if ($made.Version -ne $packageVersion) { $problems += "version is '$($made.Version)', not '$packageVersion'" }
    if ($made.ProcessorArchitecture -ne $arch) { $problems += "architecture is '$($made.ProcessorArchitecture)', not '$arch'" }

    # What the Store refuses at upload although MakeAppx packs it. @(): an empty result comes back as nothing.
    $problems += @(Get-ManifestStoreProblems -Manifest $document)

    $entries = Get-PackageEntries -PackagePath $package
    $have = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) { [void] $have.Add($entry) }
    foreach ($needed in @('BetterClipboard.exe', 'resources.pri', 'AppxManifest.xml') + $bclipOwnFiles + $extraRootFiles) {
        if (-not $have.Contains($needed)) { $problems += "$needed is missing" }
    }

    # bclip runs on what is in the package: every file of its own self-contained publish must be there. Documentation
    # (.xml) and debug (.pdb) files are for developers, not for running it.
    $bclipNeeds = @(Get-ChildItem -LiteralPath $bclipFull -Recurse -File |
        Where-Object { $_.Extension -notin '.xml', '.pdb' } |
        ForEach-Object { $_.FullName.Substring($bclipFull.Length + 1) })
    $missingForBclip = @($bclipNeeds | Where-Object { -not $have.Contains($_) })
    if ($missingForBclip.Count -gt 0) { $problems += "bclip needs files the package lacks: $($missingForBclip -join ', ')" }

    $logos = @($entries | Where-Object { $_ -like 'Assets\Store\*.png' })
    if ($logos.Count -eq 0) { $problems += 'the package has no logos under Assets\Store' }

    $size = (Get-Item -LiteralPath $package).Length
    if ($size -gt $maximumPackageBytes) { $problems += ("the package is {0:N0} MB, more than {1:N0} MB" -f ($size / 1MB), ($maximumPackageBytes / 1MB)) }
    if ($problems.Count -gt 0) {
        throw "The package for win-$arch is not right: $($problems -join '; ')."
    }

    # 5. A test package is signed; one for the Store is not.
    if ($pfx) {
        $signTool = Get-SdkTool 'signtool.exe'
        & $signTool sign /fd SHA256 /f $pfx /p $TestCertificatePassword $package | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for win-$arch (SignTool exit $LASTEXITCODE)." }
    }

    $packages += Get-Item -LiteralPath $package
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("    {0} ({1:N1} MB, {2} files, {3} logos, sha256 {4})" -f $package, ($size / 1MB), $entries.Count, $logos.Count, $hash)
}

$kind = if ($ForStore) { 'for the Store (unsigned: the Store signs them)' }
elseif ($pfx) { "signed with the test certificate ($publisherSubject)" }
else { 'unsigned test packages' }
Write-Host "==> $($packages.Count) package(s) $kind in $output" -ForegroundColor Cyan
if (-not $reserved) {
    Write-Warning "These packages carry the placeholder identity ($name). They cannot be submitted to the Store: see docs/microsoft-store.md for the owner's steps."
}
