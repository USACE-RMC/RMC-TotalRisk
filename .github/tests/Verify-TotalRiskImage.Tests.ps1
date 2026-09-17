param([switch]$TrustStoreOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$verifierPath = Join-Path $repositoryRoot '.github\scripts\Verify-TotalRiskImage.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-image-verifier-$([Guid]::NewGuid())"
$imagePrefix = "total-risk-image-verifier-test-$([Guid]::NewGuid().ToString('N'))"
$fixtureImages = [System.Collections.Generic.List[string]]::new()
$runtimeBase = 'mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8'
$sourceRevision = '1111111111111111111111111111111111111111'
$snapshotRevision = '2222222222222222222222222222222222222222'
$sourceRepository = 'https://github.com/USACE-RMC/RMC-TotalRisk'
$snapshotLabelName = 'mil.army.usace.cwbi.snapshot-revision'

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Docker {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = @(& docker @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

function Invoke-Verifier {
    param(
        [Parameter(Mandatory)][string]$ImageRef,
        [string]$ExpectedSourceRevision = $sourceRevision,
        [string]$ExpectedSnapshotRevision = $snapshotRevision,
        [switch]$MetadataOnly,
        [int]$StartupTimeoutSeconds = 5
    )

    $arguments = @(
        '-NoProfile', '-File', $verifierPath,
        '-ImageRef', $ImageRef,
        '-ExpectedSourceRevision', $ExpectedSourceRevision,
        '-ExpectedSnapshotRevision', $ExpectedSnapshotRevision,
        '-StartupTimeoutSeconds', [string]$StartupTimeoutSeconds
    )
    if ($MetadataOnly) {
        $arguments += '-MetadataOnly'
    }

    $output = @(& pwsh @arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output -join "`n"
    }
}

function Assert-NoFixtureContainers {
    param([Parameter(Mandatory)][string]$ImageRef)

    $containerIds = @(Invoke-Docker -Arguments @('ps', '--all', '--quiet', '--filter', "ancestor=$ImageRef"))
    Assert-Condition ($containerIds.Count -eq 0) "Verifier left container(s) behind for '$ImageRef': $($containerIds -join ', ')."
}

function Write-Utf8FixtureFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    # Fixture content comes from here-strings in this file, so it inherits this file's line
    # endings. On a Windows checkout (core.autocrlf) that is CRLF, and `git archive` in the
    # release publisher emits CRLF too. A shell shim starting with "#!/bin/sh<CR>" cannot be
    # executed inside the container, so every fixture file is written with LF endings.
    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    [System.IO.File]::WriteAllText($Path, $normalized, [System.Text.UTF8Encoding]::new($false))
}

function New-MetadataFixtureImage {
    param(
        [Parameter(Mandatory)][string]$Variant,
        [string]$User = 'appuser',
        [string]$EnvironmentName = 'Production',
        [string]$ListenerUrl = 'http://+:8083',
        [string]$PathBase = '/total-risk',
        [string]$ExposedPort = '8083',
        [string]$Entrypoint = '["dotnet", "RMC.TotalRisk.Api.dll"]',
        [bool]$IncludeAssembly = $true,
        [bool]$IncludeHealthcheck = $true,
        [bool]$FromScratch = $true,
        [bool]$RemoveShell = $false,
        [string]$ForbiddenPath,
        [ValidateSet('None', 'MissingOne', 'MissingAll', 'OverwriteBundle', 'ReplaceCertSymlink')]
        [string]$TrustStoreMutation = 'None',
        [string]$SourceLabel = $sourceRepository,
        [string]$RevisionLabel = $sourceRevision,
        [string]$SnapshotLabel = $snapshotRevision
    )

    $image = "${imagePrefix}:$Variant"
    $fixtureImages.Add($image)
    $contextPath = Join-Path $testRoot $Variant
    New-Item -ItemType Directory -Path $contextPath | Out-Null

    if ($IncludeAssembly) {
        Write-Utf8FixtureFile -Path (Join-Path $contextPath 'RMC.TotalRisk.Api.dll') -Content 'fixture-assembly'
    }
    if ($ForbiddenPath) {
        [System.IO.File]::WriteAllBytes(
            (Join-Path $contextPath 'forbidden-file'),
            [byte[]](0, 255, 32, 0, 17))
    }
    if ($TrustStoreMutation -ceq 'OverwriteBundle') {
        [System.IO.File]::WriteAllBytes(
            (Join-Path $contextPath 'trust-store-replacement'),
            [byte[]](65, 80, 80, 76, 73, 67, 65, 84, 73, 79, 78, 45, 66, 89, 84, 69, 83))
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add($(if ($FromScratch) { 'FROM scratch' } else { "FROM $runtimeBase" }))
    $lines.Add('WORKDIR /app')
    if (-not $FromScratch) {
        $lines.Add('RUN adduser --disabled-password --gecos "" --home /app --uid 10001 appuser')
    }
    if ($IncludeAssembly) {
        $lines.Add('COPY RMC.TotalRisk.Api.dll /app/RMC.TotalRisk.Api.dll')
    }
    if ($ForbiddenPath) {
        $lines.Add("COPY forbidden-file /$ForbiddenPath")
    }
    if ($TrustStoreMutation -ceq 'OverwriteBundle') {
        $lines.Add('COPY trust-store-replacement /etc/ssl/certs/ca-certificates.crt')
    }
    elseif ($TrustStoreMutation -ceq 'MissingOne') {
        $lines.Add('RUN rm /etc/ssl1.1/cert.pem')
    }
    elseif ($TrustStoreMutation -ceq 'MissingAll') {
        $lines.Add('RUN rm /etc/ssl/cert.pem /etc/ssl/certs/ca-certificates.crt /etc/ssl1.1/cert.pem')
    }
    elseif ($TrustStoreMutation -ceq 'ReplaceCertSymlink') {
        $lines.Add('RUN rm /etc/ssl/cert.pem && ln -s /app/RMC.TotalRisk.Api.dll /etc/ssl/cert.pem')
    }
    if ($RemoveShell) {
        $lines.Add('RUN rm /bin/sh')
    }
    $lines.Add("ENV ASPNETCORE_ENVIRONMENT=$EnvironmentName")
    $lines.Add("ENV ASPNETCORE_URLS=$ListenerUrl")
    $lines.Add("ENV PathBase=$PathBase")
    $lines.Add("EXPOSE $ExposedPort")
    $lines.Add("LABEL org.opencontainers.image.source=$SourceLabel")
    $lines.Add("LABEL org.opencontainers.image.revision=$RevisionLabel")
    $lines.Add("LABEL $snapshotLabelName=$SnapshotLabel")
    if ($IncludeHealthcheck) {
        $lines.Add('HEALTHCHECK CMD wget --quiet --tries=1 --spider http://localhost:8083/total-risk/health || exit 1')
    }
    $lines.Add("USER $User")
    $lines.Add("ENTRYPOINT $Entrypoint")

    Write-Utf8FixtureFile -Path (Join-Path $contextPath 'Dockerfile') -Content ($lines -join "`n")
    $null = Invoke-Docker -Arguments @('build', '--quiet', '--tag', $image, $contextPath)
    return $image
}

function New-RuntimeFixtureImage {
    param(
        [Parameter(Mandatory)][ValidateSet('success', 'early-exit', 'timeout', 'unhealthy', 'malformed-detailed', 'secret-field', 'secret-value', 'pid1-root')][string]$Variant
    )

    $image = "${imagePrefix}:runtime-$Variant"
    $fixtureImages.Add($image)
    $contextPath = Join-Path $testRoot "runtime-$Variant"
    New-Item -ItemType Directory -Path $contextPath | Out-Null

    Write-Utf8FixtureFile -Path (Join-Path $contextPath 'RMC.TotalRisk.Api.dll') -Content 'runnable-fixture-marker'
    $dotnetShim = @'
#!/bin/sh
set -eu
if [ "$FIXTURE_VARIANT" = "early-exit" ]; then
  exit 0
fi
if [ "$FIXTURE_VARIANT" = "timeout" ]; then
  exec sleep 300
fi
while true; do
  busybox nc -l -p 8083 -e /usr/local/bin/fixture-health-handler
done
'@
    $healthHandler = @'
#!/bin/sh
IFS=' ' read -r method path protocol || true
status='200 OK'
content_type='text/plain'
case "$path" in
  /total-risk/health)
    if [ "$FIXTURE_VARIANT" = "unhealthy" ]; then
      body='Unhealthy'
    else
      body='Healthy'
    fi
    ;;
  /total-risk/health/detailed)
    content_type='application/json'
    case "$FIXTURE_VARIANT" in
      malformed-detailed)
        body='{not-json'
        ;;
      secret-field)
        body='{"status":"healthy","version":"1.2.3","timestamp":"2026-08-28T12:00:00Z","apiKey":"classified"}'
        ;;
      secret-value)
        body='{"status":"healthy","version":"1.2.3","timestamp":"2026-08-28T12:00:00Z","notes":"password=classified"}'
        ;;
      *)
        body='{"status":"healthy","version":"1.2.3","timestamp":"2026-08-28T12:00:00Z","uptimeSeconds":1}'
        ;;
    esac
    ;;
  *)
    status='404 Not Found'
    body='Not Found'
    ;;
esac
length="$(printf '%s' "$body" | wc -c | tr -d ' ')"
printf 'HTTP/1.1 %s\r\nContent-Type: %s\r\nContent-Length: %s\r\nConnection: close\r\n\r\n%s' "$status" "$content_type" "$length" "$body"
'@
    Write-Utf8FixtureFile -Path (Join-Path $contextPath 'dotnet') -Content $dotnetShim
    Write-Utf8FixtureFile -Path (Join-Path $contextPath 'fixture-health-handler') -Content $healthHandler

    $userSetup = if ($Variant -ceq 'pid1-root') {
        "RUN printf '%s\n' 'fixture-root:x:0:0:fixture root:/app:/bin/sh' >> /etc/passwd`nUSER fixture-root"
    }
    else {
        'RUN adduser --disabled-password --gecos "" --home /app --uid 10001 appuser' + "`nUSER appuser"
    }
    $dockerfile = @"
FROM $runtimeBase
WORKDIR /app
COPY RMC.TotalRisk.Api.dll /app/RMC.TotalRisk.Api.dll
COPY dotnet /usr/local/bin/dotnet
COPY fixture-health-handler /usr/local/bin/fixture-health-handler
RUN chmod 755 /usr/local/bin/dotnet /usr/local/bin/fixture-health-handler
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8083
ENV PathBase=/total-risk
ENV FIXTURE_VARIANT=$Variant
EXPOSE 8083
LABEL org.opencontainers.image.source=$sourceRepository
LABEL org.opencontainers.image.revision=$sourceRevision
LABEL $snapshotLabelName=$snapshotRevision
HEALTHCHECK CMD wget --quiet --tries=1 --spider http://localhost:8083/total-risk/health || exit 1
$userSetup
ENTRYPOINT ["dotnet", "RMC.TotalRisk.Api.dll"]
"@
    Write-Utf8FixtureFile -Path (Join-Path $contextPath 'Dockerfile') -Content $dockerfile
    $null = Invoke-Docker -Arguments @('build', '--quiet', '--tag', $image, $contextPath)
    return $image
}

function Assert-VerifierRejected {
    param(
        [Parameter(Mandatory)][string]$ImageRef,
        [switch]$MetadataOnly,
        [string]$ExpectedSourceRevision = $sourceRevision,
        [string]$ExpectedSnapshotRevision = $snapshotRevision,
        [int]$StartupTimeoutSeconds = 5
    )

    $result = Invoke-Verifier -ImageRef $ImageRef -ExpectedSourceRevision $ExpectedSourceRevision -ExpectedSnapshotRevision $ExpectedSnapshotRevision -MetadataOnly:$MetadataOnly -StartupTimeoutSeconds $StartupTimeoutSeconds
    Assert-Condition ($result.ExitCode -ne 0) "Verifier accepted invalid image fixture '$ImageRef'."
    Assert-NoFixtureContainers -ImageRef $ImageRef
}

function Assert-BuildContextHygiene {
    $image = "${imagePrefix}:build-context"
    $fixtureImages.Add($image)
    $dockerfilePath = Join-Path $testRoot 'BuildContext.Dockerfile'
    Write-Utf8FixtureFile -Path $dockerfilePath -Content "FROM $runtimeBase`nCOPY . /context`n"
    # The production Dockerfile builds from the repository root.
    $contextPath = $repositoryRoot
    $null = Invoke-Docker -Arguments @('build', '--quiet', '--file', $dockerfilePath, '--tag', $image, $contextPath)
    $contextFiles = @(Invoke-Docker -Arguments @(
        'run', '--rm', '--entrypoint', '/bin/sh', $image,
        '-c', 'find /context -mindepth 1 -print | sort'
    ) | ForEach-Object { ([string]$_).Replace('\', '/') })

    foreach ($requiredPath in @(
        '/context/nuget.cwbi.config',
        '/context/Directory.Build.props',
        '/context/Directory.Packages.props',
        '/context/global.json',
        '/context/src/RMC.TotalRisk/RMC.TotalRisk.csproj',
        '/context/src/RMC.TotalRisk/packages.lock.json',
        '/context/src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj',
        '/context/src/RMC.TotalRisk.Api/packages.lock.json',
        '/context/src/RMC.TotalRisk.Api/appsettings.json'
    )) {
        Assert-Condition ($contextFiles -ccontains $requiredPath) "Docker context excluded locked production input '$requiredPath'."
    }
    Assert-Condition (@($contextFiles | Where-Object { $_ -cmatch '^/context/local-feed/RMC\.Numerics\.[^/]+\.nupkg$' }).Count -eq 1) 'Docker context must carry exactly one local RMC.Numerics package.'
    Assert-Condition (-not ($contextFiles -ccontains '/context/NuGet.config')) 'Docker context must not carry the developer NuGet.config.'

    foreach ($contextFile in $contextFiles) {
        Assert-Condition ($contextFile -notmatch '(^|/)appsettings\.Development\.json$') "Docker context includes development settings '$contextFile'."
        Assert-Condition ($contextFile -notmatch '(^|/)Properties/launchSettings\.json$') "Docker context includes launch profiles '$contextFile'."
        Assert-Condition ($contextFile -notmatch '\.md$') "Docker context includes Markdown documentation '$contextFile'."
        Assert-Condition ($contextFile -notmatch '(^|/)RMC\.TotalRisk\.(Tests|Api\.Tests|Verification)(/|$)') "Docker context includes the test project '$contextFile'."
        Assert-Condition ($contextFile -notmatch '\.(sln|slnx)$') "Docker context includes a solution file '$contextFile'."
        Assert-Condition ($contextFile -notmatch '(^|/)(bin|obj|TestResults)(/|$)') "Docker context includes build/test output '$contextFile'."
    }
}

if (-not (Test-Path -LiteralPath $verifierPath)) {
    throw "Image verifier is missing: $verifierPath"
}

New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $validMetadata = New-MetadataFixtureImage -Variant 'untouched-pinned-trust-store' -FromScratch $false
    $validMetadataResult = Invoke-Verifier -ImageRef $validMetadata -MetadataOnly
    Assert-Condition ($validMetadataResult.ExitCode -eq 0) "Verifier rejected untouched pinned-runtime trust store: $($validMetadataResult.Output)"

    $missingOneTrustStore = New-MetadataFixtureImage -Variant 'missing-one-trust-store-entry' -FromScratch $false -TrustStoreMutation 'MissingOne'
    Assert-VerifierRejected -ImageRef $missingOneTrustStore -MetadataOnly

    $missingAllTrustStore = New-MetadataFixtureImage -Variant 'missing-all-trust-store-entries' -FromScratch $false -TrustStoreMutation 'MissingAll'
    Assert-VerifierRejected -ImageRef $missingAllTrustStore -MetadataOnly

    $replacedTrustStoreSymlink = New-MetadataFixtureImage -Variant 'replaced-trust-store-symlink' -FromScratch $false -TrustStoreMutation 'ReplaceCertSymlink'
    Assert-VerifierRejected -ImageRef $replacedTrustStoreSymlink -MetadataOnly

    $overwrittenTrustStore = New-MetadataFixtureImage -Variant 'overwritten-trust-store' -FromScratch $false -TrustStoreMutation 'OverwriteBundle'
    Assert-VerifierRejected -ImageRef $overwrittenTrustStore -MetadataOnly

    if ($TrustStoreOnly) {
        'All pinned-runtime trust-store verifier fixtures passed.'
        return
    }

    $localMarker = New-MetadataFixtureImage -Variant 'local-marker' -FromScratch $false -SnapshotLabel 'not-published'
    $localMarkerResult = Invoke-Verifier -ImageRef $localMarker -ExpectedSnapshotRevision 'not-published' -MetadataOnly
    Assert-Condition ($localMarkerResult.ExitCode -eq 0) "Verifier rejected explicit local snapshot marker: $($localMarkerResult.Output)"

    $exportOnlyMetadata = New-MetadataFixtureImage -Variant 'export-only-metadata' -FromScratch $false -RemoveShell $true
    $exportOnlyResult = Invoke-Verifier -ImageRef $exportOnlyMetadata -MetadataOnly
    Assert-Condition ($exportOnlyResult.ExitCode -eq 0) "Verifier must inspect exported rootfs without requiring image shell tools: $($exportOnlyResult.Output)"

    Assert-BuildContextHygiene

    $invalidMetadata = @(
        (New-MetadataFixtureImage -Variant 'root-user' -User 'root'),
        (New-MetadataFixtureImage -Variant 'development-environment' -EnvironmentName 'Development'),
        (New-MetadataFixtureImage -Variant 'wrong-listener' -ListenerUrl 'http://+:9090'),
        (New-MetadataFixtureImage -Variant 'wrong-path-base' -PathBase '/api'),
        (New-MetadataFixtureImage -Variant 'wrong-port' -ExposedPort '9090'),
        (New-MetadataFixtureImage -Variant 'wrong-entrypoint' -Entrypoint '["/bin/sh"]'),
        (New-MetadataFixtureImage -Variant 'missing-assembly' -IncludeAssembly $false),
        (New-MetadataFixtureImage -Variant 'missing-healthcheck' -IncludeHealthcheck $false),
        (New-MetadataFixtureImage -Variant 'wrong-source-label' -SourceLabel 'https://example.invalid/repository'),
        (New-MetadataFixtureImage -Variant 'wrong-source-revision' -RevisionLabel '3333333333333333333333333333333333333333'),
        (New-MetadataFixtureImage -Variant 'invalid-source-revision' -RevisionLabel 'not-a-full-commit'),
        (New-MetadataFixtureImage -Variant 'wrong-snapshot-revision' -SnapshotLabel '3333333333333333333333333333333333333333'),
        (New-MetadataFixtureImage -Variant 'invalid-snapshot-revision' -SnapshotLabel 'not-a-snapshot')
    )
    foreach ($invalidImage in $invalidMetadata) {
        Assert-VerifierRejected -ImageRef $invalidImage -MetadataOnly
    }

    $forbiddenPaths = [ordered]@{
        'ai-instruction-file' = 'opt/source/AGENTS.md'
        'ai-agent-directory' = 'opt/source/.agents/skill.md'
        'ai-claude-directory' = 'opt/source/.claude/settings.json'
        'ai-codex-directory' = 'opt/source/.codex/config.toml'
        'ai-plan-directory' = 'opt/source/.superpowers/plan.md'
        'github-directory' = 'opt/source/.github/workflow.yml'
        'git-directory' = 'opt/source/.git/config'
        'gitignore-file' = 'opt/source/.gitignore'
        'visual-studio-directory' = 'opt/source/.vs/config/applicationhost.config'
        'vscode-directory' = 'opt/source/.vscode/settings.json'
        'environment-file' = 'opt/source/.env.local'
        'mixed-case-certificate' = 'opt/source/client.PfX'
        'conventional-private-key' = 'opt/source/id_rsa'
        'mcp-config' = 'opt/source/.mcp.json'
        'graphify-config' = 'opt/source/.graphifyignore'
        'source-file' = 'opt/source/Program.cs'
        'project-file' = 'opt/source/RMC.TotalRisk.Api.csproj'
        'solution-file' = 'opt/source/RMC-TotalRisk.sln'
        'build-props' = 'opt/source/Directory.Build.props'
        'package-lock' = 'opt/source/packages.lock.json'
        'nuget-config' = 'opt/source/nuget.config'
        'test-project' = 'opt/source/RMC.TotalRisk.Tests/test.dll'
        'test-scenarios' = 'opt/source/test_scenarios/case.json'
        'test-results' = 'opt/source/TestResults/results.trx'
        'launch-profile' = 'opt/source/Properties/launchSettings.json'
        'development-settings' = 'opt/source/appsettings.Development.json'
        'release-script' = 'opt/source/scripts/Prepare-CwbiRelease.ps1'
        'release-module' = 'opt/source/scripts/cwbi-release/CwbiRelease.psm1'
        'release-policy' = 'opt/source/scripts/cwbi-release/policy.json'
        'markdown-doc' = 'opt/source/README.md'
        'dockerfile' = 'opt/source/Dockerfile'
        'dockerignore' = 'opt/source/.dockerignore'
        'gitattributes' = 'opt/source/.gitattributes'
        'editorconfig' = 'opt/source/.editorconfig'
        'global-json' = 'opt/source/global.json'
    }
    foreach ($fixtureName in $forbiddenPaths.Keys) {
        $image = New-MetadataFixtureImage -Variant "forbidden-$fixtureName" -ForbiddenPath $forbiddenPaths[$fixtureName]
        Assert-VerifierRejected -ImageRef $image -MetadataOnly
    }

    Assert-VerifierRejected -ImageRef $validMetadata -ExpectedSourceRevision 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' -MetadataOnly
    Assert-VerifierRejected -ImageRef $validMetadata -ExpectedSnapshotRevision 'not-a-marker' -MetadataOnly

    $runtimeSuccess = New-RuntimeFixtureImage -Variant 'success'
    $runtimeSuccessResult = Invoke-Verifier -ImageRef $runtimeSuccess
    Assert-Condition ($runtimeSuccessResult.ExitCode -eq 0) "Verifier rejected runnable success fixture: $($runtimeSuccessResult.Output)"
    Assert-NoFixtureContainers -ImageRef $runtimeSuccess

    foreach ($runtimeFailure in @('early-exit', 'timeout', 'unhealthy', 'malformed-detailed', 'secret-field', 'secret-value', 'pid1-root')) {
        $image = New-RuntimeFixtureImage -Variant $runtimeFailure
        Assert-VerifierRejected -ImageRef $image -StartupTimeoutSeconds 5
    }

    'All RMC-TotalRisk image verifier metadata, runtime, and cleanup tests passed.'
}
finally {
    foreach ($image in $fixtureImages) {
        & docker image rm --force $image *> $null
    }

    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
