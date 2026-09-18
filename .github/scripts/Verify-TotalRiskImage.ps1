[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ImageRef,
    [Parameter(Mandatory)][ValidateScript({
        if ($_ -cnotmatch '^[0-9a-f]{40}$') {
            throw 'ExpectedSourceRevision must be a full lowercase Git commit.'
        }
        return $true
    })][string]$ExpectedSourceRevision,
    [Parameter(Mandatory)][ValidateScript({
        if ($_ -cne 'not-published' -and $_ -cnotmatch '^[0-9a-f]{40}$') {
            throw "ExpectedSnapshotRevision must be a full lowercase Git commit or 'not-published'."
        }
        return $true
    })][string]$ExpectedSnapshotRevision,
    [switch]$MetadataOnly,
    [ValidateRange(5, 300)][int]$StartupTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRepository = 'https://github.com/USACE-RMC/RMC-TotalRisk'
$snapshotLabelName = 'mil.army.usace.cwbi.snapshot-revision'
$runtimeBase = 'mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8'
$filesystemVerificationLabelName = 'mil.army.usace.cwbi.image-verifier-export'
$approvedRuntimeTrustStorePaths = @(
    '/etc/ssl/cert.pem',
    '/etc/ssl/certs/ca-certificates.crt',
    '/etc/ssl1.1/cert.pem'
)

function Invoke-Docker {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($output)
}

function Assert-Equal {
    param(
        [AllowEmptyString()][string]$Actual,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Actual -cne $Expected) {
        throw "$Description must be '$Expected'; found '$Actual'."
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)][object[]]$Values,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Values -cnotcontains $Expected) {
        throw "$Description must include '$Expected'."
    }
}

function ConvertTo-CwbiArchivePath {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)

    $segments = [System.Collections.Generic.List[string]]::new()
    foreach ($segment in $Path.Replace('\', '/').Split('/', [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($segment -ceq '.') {
            continue
        }
        if ($segment -ceq '..') {
            if ($segments.Count -eq 0) {
                throw 'Exported root filesystem contains a path that escapes the archive root.'
            }
            $segments.RemoveAt($segments.Count - 1)
            continue
        }
        $segments.Add($segment)
    }

    return "/$($segments -join '/')"
}

function Resolve-CwbiArchiveLinkPath {
    param(
        [Parameter(Mandatory)][string]$EntryPath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$LinkName
    )

    if ($LinkName.StartsWith('/', [StringComparison]::Ordinal)) {
        return ConvertTo-CwbiArchivePath -Path $LinkName
    }

    $lastSeparator = $EntryPath.LastIndexOf('/', [StringComparison]::Ordinal)
    $parentPath = if ($lastSeparator -le 0) { '/' } else { $EntryPath.Substring(0, $lastSeparator) }
    return ConvertTo-CwbiArchivePath -Path "$parentPath/$LinkName"
}

function Get-CwbiRootFsManifest {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string[]]$TrustStorePaths
    )

    $allPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $trustPathSet = [System.Collections.Generic.HashSet[string]]::new($TrustStorePaths, [StringComparer]::Ordinal)
    $trustEntries = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $archiveStream = [System.IO.File]::Open($ArchivePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    $reader = [System.Formats.Tar.TarReader]::new($archiveStream, $true)
    try {
        while ($null -ne ($entry = $reader.GetNextEntry($false))) {
            $entryPath = ConvertTo-CwbiArchivePath -Path $entry.Name
            if (-not $allPaths.Add($entryPath)) {
                throw "Exported root filesystem contains duplicate path '$entryPath'."
            }

            if ($trustPathSet.Contains($entryPath)) {
                $contentHash = $null
                if ($entry.EntryType -in @(
                    [System.Formats.Tar.TarEntryType]::RegularFile,
                    [System.Formats.Tar.TarEntryType]::V7RegularFile
                )) {
                    if ($null -eq $entry.DataStream) {
                        throw "Unable to read runtime trust-store entry '$entryPath'."
                    }
                    $contentHash = [Convert]::ToHexString(
                        [System.Security.Cryptography.SHA256]::HashData($entry.DataStream)
                    ).ToLowerInvariant()
                }

                $trustEntries.Add($entryPath, [pscustomobject]@{
                    EntryType = [string]$entry.EntryType
                    LinkName = [string]$entry.LinkName
                    Length = [long]$entry.Length
                    ContentHash = $contentHash
                })
            }
        }
    }
    finally {
        $reader.Dispose()
        $archiveStream.Dispose()
    }

    return [pscustomobject]@{
        Paths = [string[]]@($allPaths)
        TrustEntries = $trustEntries
    }
}

function Get-CwbiResolvedArchiveContentHash {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$Path
    )

    $currentPath = $Path
    $visitedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($true) {
        if (-not $visitedPaths.Add($currentPath)) {
            throw "Runtime trust-store entry '$Path' contains a symbolic-link cycle."
        }

        $entry = $null
        if (-not $Manifest.TrustEntries.TryGetValue($currentPath, [ref]$entry)) {
            throw "Runtime trust-store entry '$Path' does not resolve within the approved trust store."
        }

        if ($entry.EntryType -in @('RegularFile', 'V7RegularFile')) {
            if ([string]::IsNullOrWhiteSpace($entry.ContentHash)) {
                throw "Runtime trust-store entry '$Path' has no verifiable content hash."
            }
            return [string]$entry.ContentHash
        }
        if ($entry.EntryType -cne 'SymbolicLink' -or [string]::IsNullOrWhiteSpace($entry.LinkName)) {
            throw "Runtime trust-store entry '$Path' has an unsupported archive entry type."
        }

        $currentPath = Resolve-CwbiArchiveLinkPath -EntryPath $currentPath -LinkName $entry.LinkName
    }
}

function Assert-CwbiRuntimeTrustStoreMatchesPinnedBase {
    param(
        [Parameter(Mandatory)][object]$CandidateManifest,
        [Parameter(Mandatory)][object]$PinnedBaseManifest,
        [Parameter(Mandatory)][string[]]$TrustStorePaths
    )

    foreach ($path in $TrustStorePaths) {
        $candidateEntry = $null
        if (-not $CandidateManifest.TrustEntries.TryGetValue($path, [ref]$candidateEntry)) {
            throw "Runtime trust-store entry '$path' is missing from the candidate image."
        }

        $baseEntry = $null
        if (-not $PinnedBaseManifest.TrustEntries.TryGetValue($path, [ref]$baseEntry)) {
            throw "Pinned runtime base is missing trust-store entry '$path'."
        }
        if ($candidateEntry.EntryType -cne $baseEntry.EntryType) {
            throw "Runtime trust-store entry '$path' differs in archive entry type from the pinned runtime base."
        }
        if ($candidateEntry.LinkName -cne $baseEntry.LinkName) {
            throw "Runtime trust-store entry '$path' differs in symbolic-link target from the pinned runtime base."
        }
        if ($candidateEntry.Length -ne $baseEntry.Length -or $candidateEntry.ContentHash -cne $baseEntry.ContentHash) {
            throw "Runtime trust-store entry '$path' differs in cryptographic content from the pinned runtime base."
        }

        $candidateResolvedHash = Get-CwbiResolvedArchiveContentHash -Manifest $CandidateManifest -Path $path
        $baseResolvedHash = Get-CwbiResolvedArchiveContentHash -Manifest $PinnedBaseManifest -Path $path
        if ($candidateResolvedHash -cne $baseResolvedHash) {
            throw "Runtime trust-store entry '$path' differs in resolved cryptographic content from the pinned runtime base."
        }
    }
}

function Test-SecretFieldName {
    param([Parameter(Mandatory)][string]$Name)

    $normalized = $Name -replace '[^A-Za-z0-9]', ''
    foreach ($secretName in @(
        'password', 'passwd', 'pwd', 'secret', 'token', 'apikey',
        'authorization', 'credential', 'credentials', 'connectionstring',
        'privatekey', 'accesskey', 'accesskeyid', 'secretaccesskey',
        'accesstoken', 'refreshtoken'
    )) {
        if ($normalized.Equals($secretName, [StringComparison]::OrdinalIgnoreCase) -or
            $normalized.EndsWith($secretName, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Test-ObviousSecretValue {
    param([AllowEmptyString()][string]$Value)

    return (
        $Value -match '(?i)\b(?:password|passwd|pwd|secret|token|api[_-]?key|authorization|credential|connection[_-]?string)\s*[:=]\s*\S+' -or
        $Value -match '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+' -or
        $Value -cmatch '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b' -or
        $Value -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
        $Value -match '(?i)\b(?:AccountKey|SharedAccessSignature)=[^;\s]+'
    )
}

function Assert-NoObviousSecrets {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [string]) {
        if (Test-ObviousSecretValue -Value ([string]$Value)) {
            throw "Detailed health response contains an obvious secret-bearing value at '$Path'."
        }
        return
    }

    if ($Value.GetType().IsValueType) {
        return
    }

    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            $name = [string]$key
            if (Test-SecretFieldName -Name $name) {
                throw "Detailed health response contains secret-bearing field '$Path.$name'."
            }
            Assert-NoObviousSecrets -Value $Value[$key] -Path "$Path.$name"
        }
        return
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-NoObviousSecrets -Value $item -Path "$Path[$index]"
            $index++
        }
        return
    }

    $properties = @($Value.PSObject.Properties | Where-Object { $_.MemberType -in @('NoteProperty', 'Property') })
    foreach ($property in $properties) {
        if (Test-SecretFieldName -Name $property.Name) {
            throw "Detailed health response contains secret-bearing field '$Path.$($property.Name)'."
        }
        Assert-NoObviousSecrets -Value $property.Value -Path "$Path.$($property.Name)"
    }
}

$inspectJson = (Invoke-Docker -Arguments @('image', 'inspect', $ImageRef)) -join "`n"
$inspect = @($inspectJson | ConvertFrom-Json)
if ($inspect.Count -ne 1) {
    throw "Expected one image inspection result for '$ImageRef'; found $($inspect.Count)."
}

$imageConfig = $inspect[0].Config
$containerUser = [string]$imageConfig.User
if ([string]::IsNullOrWhiteSpace($containerUser) -or $containerUser -in @('0', 'root', '0:0', 'root:root')) {
    throw "Container must declare a non-root user; found '$containerUser'."
}

$environment = @($imageConfig.Env)
Assert-Contains -Values $environment -Expected 'ASPNETCORE_ENVIRONMENT=Production' -Description 'Container environment'
Assert-Contains -Values $environment -Expected 'ASPNETCORE_URLS=http://+:8083' -Description 'Container environment'
Assert-Contains -Values $environment -Expected 'PathBase=/total-risk' -Description 'Container environment'

$exposedPorts = @($imageConfig.ExposedPorts.PSObject.Properties.Name)
Assert-Contains -Values $exposedPorts -Expected '8083/tcp' -Description 'Exposed ports'

$entrypoint = @($imageConfig.Entrypoint)
Assert-Equal -Actual ($entrypoint -join ' ') -Expected 'dotnet RMC.TotalRisk.Api.dll' -Description 'Container entrypoint'

$healthcheck = @($imageConfig.Healthcheck.Test) -join ' '
if (-not $healthcheck.Contains('http://localhost:8083/total-risk/health', [StringComparison]::Ordinal)) {
    throw "Container healthcheck must call http://localhost:8083/total-risk/health; found '$healthcheck'."
}

$labels = $imageConfig.Labels
Assert-Equal -Actual ([string]$labels.'org.opencontainers.image.source') -Expected $sourceRepository -Description 'OCI source label'
Assert-Equal -Actual ([string]$labels.'org.opencontainers.image.revision') -Expected $ExpectedSourceRevision -Description 'OCI upstream revision label'
Assert-Equal -Actual ([string]$labels.$snapshotLabelName) -Expected $ExpectedSnapshotRevision -Description 'CWBI snapshot revision label'

$filesystemContainerId = $null
$pinnedBaseContainerId = $null
$filesystemArchivePath = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-image-candidate-rootfs-$([Guid]::NewGuid().ToString('N')).tar"
$pinnedBaseArchivePath = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-image-pinned-base-rootfs-$([Guid]::NewGuid().ToString('N')).tar"
$filesystemVerificationLabel = "$filesystemVerificationLabelName=$([Guid]::NewGuid().ToString('N'))"
try {
    $filesystemContainerId = ((Invoke-Docker -Arguments @(
        'create', '--label', $filesystemVerificationLabel, '--entrypoint', '/bin/true', $ImageRef
    )) -join '').Trim()
    $pinnedBaseContainerId = ((Invoke-Docker -Arguments @(
        'create', '--label', $filesystemVerificationLabel, '--entrypoint', '/bin/true', $runtimeBase
    )) -join '').Trim()
    $null = Invoke-Docker -Arguments @('export', '--output', $filesystemArchivePath, $filesystemContainerId)
    $null = Invoke-Docker -Arguments @('export', '--output', $pinnedBaseArchivePath, $pinnedBaseContainerId)

    $candidateManifest = Get-CwbiRootFsManifest -ArchivePath $filesystemArchivePath -TrustStorePaths $approvedRuntimeTrustStorePaths
    $pinnedBaseManifest = Get-CwbiRootFsManifest -ArchivePath $pinnedBaseArchivePath -TrustStorePaths $approvedRuntimeTrustStorePaths
    foreach ($trustStorePath in $approvedRuntimeTrustStorePaths) {
        if (-not $pinnedBaseManifest.TrustEntries.ContainsKey($trustStorePath)) {
            throw "Pinned runtime base is missing trust-store entry '$trustStorePath'."
        }
    }
    Assert-CwbiRuntimeTrustStoreMatchesPinnedBase `
        -CandidateManifest $candidateManifest `
        -PinnedBaseManifest $pinnedBaseManifest `
        -TrustStorePaths $approvedRuntimeTrustStorePaths
    $rootFiles = @($candidateManifest.Paths)
}
finally {
    if ($pinnedBaseContainerId) {
        $null = & docker rm --force $pinnedBaseContainerId 2>&1
    }
    if ($filesystemContainerId) {
        $null = & docker rm --force $filesystemContainerId 2>&1
    }
    if (Test-Path -LiteralPath $pinnedBaseArchivePath) {
        Remove-Item -LiteralPath $pinnedBaseArchivePath -Force
    }
    if (Test-Path -LiteralPath $filesystemArchivePath) {
        Remove-Item -LiteralPath $filesystemArchivePath -Force
    }
}
$normalizedRootFiles = @($rootFiles | ForEach-Object {
    $normalizedPath = ([string]$_).Replace('\', '/').TrimEnd('/')
    if ($normalizedPath.StartsWith('/', [StringComparison]::Ordinal)) {
        $normalizedPath
    }
    else {
        "/$normalizedPath"
    }
})
Assert-Contains -Values $normalizedRootFiles -Expected '/app/RMC.TotalRisk.Api.dll' -Description 'Runtime filesystem'

$forbiddenPatterns = @(
    '(^|/)(AGENTS|CLAUDE)\.md$',
    '(^|/)\.(agents|claude|codex|superpowers)(/|$)',
    '(^|/)\.github(/|$)',
    '(^|/)\.git(/|$)',
    '(^|/)\.gitignore$',
    '(^|/)\.mcp\.json$',
    '(^|/)\.graphifyignore$',
    '(^|/)\.(vs|vscode)(/|$)',
    '(^|/)\.env($|\.)',
    '\.(cs|fs|vb)$',
    '\.(csproj|fsproj|vbproj)$',
    '\.(sln|slnx)$',
    '(^|/)(Directory\.(Build|Packages)\.(props|targets)|packages\.lock\.json|nuget\.config|nuget\.cwbi\.config)$',
    '(^|/)local-feed(/|$)',
    '\.nupkg$',
    '(^|/)(RMC\.TotalRisk\.(Tests|Api\.Tests|Verification)|tests|specs|test_scenarios|TestResults|coverage)(/|$)',
    '(^|/)Properties/launchSettings\.json$',
    '(^|/)appsettings\.Development\.json$',
    '\.(ps1|psm1|psd1)$',
    '(^|/)scripts/cwbi-release(/|$)',
    '\.md$',
    '(^|/)Dockerfile([^/]*)?$',
    '(^|/)\.dockerignore$',
    '(^|/)\.gitattributes$',
    '(^|/)\.editorconfig$',
    '(^|/)global\.json$'
)
$secretBearingPathPatterns = @(
    '\.(pfx|p12|pem|key|jks|keystore|cer|crt|der)$',
    '(^|/)(id_rsa|id_dsa|id_ecdsa|id_ed25519)$'
)
foreach ($rootFile in $normalizedRootFiles) {
    if ($approvedRuntimeTrustStorePaths -cnotcontains $rootFile) {
        foreach ($pattern in $secretBearingPathPatterns) {
            if ($rootFile -match $pattern) {
                throw "Release image contains forbidden credential-bearing runtime path '$rootFile'."
            }
        }
    }
    foreach ($pattern in $forbiddenPatterns) {
        if ($rootFile -match $pattern) {
            throw "Release image contains forbidden runtime path '$rootFile'."
        }
    }
}

if ($MetadataOnly) {
    "Verified RMC-TotalRisk image metadata and complete runtime filesystem: $ImageRef"
    return
}

$containerId = $null
try {
    $verificationLabel = "mil.army.usace.cwbi.image-verifier=$([Guid]::NewGuid().ToString('N'))"
    $containerId = ((Invoke-Docker -Arguments @(
        'run', '--detach', '--rm', '--label', $verificationLabel,
        '--publish', '127.0.0.1::8083', $ImageRef
    )) -join '').Trim()

    $portOutput = ((Invoke-Docker -Arguments @('port', $containerId, '8083/tcp')) -join '').Trim()
    if ($portOutput -notmatch ':(?<port>[0-9]+)$') {
        throw "Could not determine the published host port for container '$containerId'."
    }

    $hostPort = [int]$Matches.port
    $healthUri = "http://127.0.0.1:$hostPort/total-risk/health"
    $detailedHealthUri = "http://127.0.0.1:$hostPort/total-risk/health/detailed"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $healthResponse = $null

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $healthResponse = Invoke-RestMethod -Uri $healthUri -TimeoutSec 2
            break
        }
        catch {
            $running = ((Invoke-Docker -Arguments @('inspect', '--format', '{{.State.Running}}', $containerId)) -join '').Trim()
            if ($running -cne 'true') {
                throw 'Container exited before becoming healthy.'
            }
            Start-Sleep -Milliseconds 250
        }
    }

    if ($null -eq $healthResponse) {
        throw "Container did not become reachable within $StartupTimeoutSeconds seconds."
    }
    Assert-Equal -Actual ([string]$healthResponse) -Expected 'Healthy' -Description 'Health response'

    $detailedResponse = Invoke-WebRequest -Uri $detailedHealthUri -TimeoutSec 10
    try {
        $detailedHealth = [string]$detailedResponse.Content | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw 'Detailed health response must be valid JSON.'
    }
    if ($null -eq $detailedHealth -or $detailedHealth -isnot [psobject]) {
        throw 'Detailed health response must be a JSON object.'
    }
    Assert-Equal -Actual ([string]$detailedHealth.status) -Expected 'healthy' -Description 'Detailed health status'
    if ([string]::IsNullOrWhiteSpace([string]$detailedHealth.version)) {
        throw 'Detailed health response must include a non-empty version.'
    }
    $parsedTimestamp = [DateTimeOffset]::MinValue
    if ($null -eq $detailedHealth.timestamp -or
        -not [DateTimeOffset]::TryParse([string]$detailedHealth.timestamp, [ref]$parsedTimestamp)) {
        throw 'Detailed health response must include a valid timestamp.'
    }
    Assert-NoObviousSecrets -Value $detailedHealth -Path '$'

    $runtimeUserId = ((Invoke-Docker -Arguments @(
        'exec', '--user', '0:0', $containerId, '/bin/sh', '-c',
        "awk '/^Uid:/{print `$2; exit}' /proc/1/status"
    )) -join '').Trim()
    if ($runtimeUserId -cnotmatch '^[0-9]+$') {
        throw "Could not determine the running API process user ID; found '$runtimeUserId'."
    }
    if ($runtimeUserId -ceq '0') {
        throw 'Running API process PID 1 resolved to root user ID 0.'
    }
}
finally {
    if ($containerId) {
        $null = & docker rm --force $containerId 2>&1
    }
}

"Verified running RMC-TotalRisk deployment image: $ImageRef"
