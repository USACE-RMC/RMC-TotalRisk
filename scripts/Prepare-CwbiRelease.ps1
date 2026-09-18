[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceRevision,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TargetWorktree,
    [Parameter(Mandatory)][ValidatePattern('^(cwbi-dev|snapshot/sanitized)$')][string]$ExpectedTargetBranch,
    [Parameter(Mandatory)][ValidatePattern('^([0-9a-fA-F]{40}|unborn)$')][string]$ExpectedTargetCommit,
    [string]$EvidenceDirectory,
    [switch]$KeepStaging
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceCommonGitDirectory = (& git -c "safe.directory=$($repositoryRoot.Replace('\', '/'))" -c 'core.excludesFile=' -C $repositoryRoot rev-parse --path-format=absolute --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the source common Git directory.'
}
$resolvedTargetWorktree = (Resolve-Path -LiteralPath $TargetWorktree).Path
$TargetWorktree = $resolvedTargetWorktree

$modulePath = Join-Path $PSScriptRoot 'cwbi-release\CwbiRelease.psm1'
$policyRepositoryPath = 'scripts/cwbi-release/policy.json'
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-cwbi-release-$([Guid]::NewGuid())"
$snapshotRoot = Join-Path $stagingRoot 'snapshot'
$verificationRoot = Join-Path $stagingRoot 'verification'
$imageTag = $null
$localSnapshotRevision = 'not-published'
$releaseTestProjects = @(
    'src/RMC.TotalRisk.Tests/RMC.TotalRisk.Tests.csproj',
    'src/RMC.TotalRisk.Api.Tests/RMC.TotalRisk.Api.Tests.csproj'
)
$releaseAuditProjects = @(
    'src/RMC.TotalRisk/RMC.TotalRisk.csproj',
    'src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj'
) + $releaseTestProjects
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path (Split-Path -Parent $repositoryRoot) 'cwbi-release-evidence'
}
$EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)

function Invoke-ReleaseCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "[$Name]"
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Name failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Remove-VerificationArtifacts {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][object[]]$SnapshotManifest
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $artifactDirectories = @(
        Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse -Force |
            Where-Object { $_.Name -in @('bin', 'obj', 'TestResults') } |
            Select-Object -ExpandProperty FullName
    )
    foreach ($artifactDirectory in $artifactDirectories | Sort-Object Length -Descending) {
        $resolvedArtifact = [System.IO.Path]::GetFullPath($artifactDirectory)
        if (-not $resolvedArtifact.StartsWith("$resolvedRoot\", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove verification artifact outside staging: $resolvedArtifact."
        }
        if (Test-Path -LiteralPath $resolvedArtifact) {
            Remove-Item -LiteralPath $resolvedArtifact -Recurse -Force
        }
    }

    $snapshotPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $SnapshotManifest) {
        $null = $snapshotPaths.Add([string]$entry.Path)
    }
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force) {
        $relativePath = [System.IO.Path]::GetRelativePath($resolvedRoot, $file.FullName).Replace('\', '/')
        if (-not $snapshotPaths.Contains($relativePath) -and
            $relativePath -match '^test_scenarios/.+/csharp/output\.json$') {
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}

Import-Module $modulePath -Force
$imageTag = New-CwbiRunUniqueImageTag -SourceCommit $SourceRevision
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

try {
    Write-Host '[Export immutable, allowlisted source commit]'
    $export = Export-CwbiReleaseTree `
        -RepositoryRoot $repositoryRoot `
        -SourceRevision $SourceRevision `
        -Destination $snapshotRoot `
        -PolicyRepositoryPath $policyRepositoryPath

    $releasePolicy = $export.Policy
    Assert-CwbiPolicyDigest -PolicyPath $export.PolicyPath -ExpectedPolicySha256 $export.PolicySha256
    Assert-CwbiGitHistorySecretsAbsent `
        -RepositoryRoot $repositoryRoot `
        -SourceRevision $export.SourceCommit `
        -ExcludedFiles @($releasePolicy.secretScanExcludedFiles) `
        -Policy $releasePolicy
    Assert-CwbiReleasePathPolicy `
        -Root $snapshotRoot `
        -PolicyPath $export.PolicyPath `
        -ExpectedPolicySha256 $export.PolicySha256
    Assert-CwbiSecretsAbsent `
        -Root $snapshotRoot `
        -PolicyPath $export.PolicyPath `
        -ExpectedPolicySha256 $export.PolicySha256
    $snapshotManifest = @($export.Manifest)

    Copy-Item -LiteralPath $snapshotRoot -Destination $verificationRoot -Recurse

    Invoke-ReleaseCommand -Name 'CWBI release-module adversarial tests' -WorkingDirectory $repositoryRoot -Command 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.github/tests/Prepare-CwbiRelease.Tests.ps1'
    )
    Invoke-ReleaseCommand -Name 'CWBI local-publisher contract tests' -WorkingDirectory $repositoryRoot -Command 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.github/tests/Publish-CwbiSnapshot.Tests.ps1'
    )
    Invoke-ReleaseCommand -Name 'CWBI workflow contract tests' -WorkingDirectory $verificationRoot -Command 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.github/tests/CwbiApiWorkflow.Tests.ps1'
    )
    Invoke-ReleaseCommand -Name 'Image verifier fixture tests' -WorkingDirectory $verificationRoot -Command 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.github/tests/Verify-TotalRiskImage.Tests.ps1'
    )
    # The snapshot carries the library, the API, and the two fast test projects but not the
    # solution's Verification project, so every dotnet command targets the two test projects
    # (which restore and build the library and the API transitively) rather than the .sln.
    foreach ($testProject in $releaseTestProjects) {
        Invoke-ReleaseCommand -Name "Locked project restore ($testProject)" -WorkingDirectory $verificationRoot -Command 'dotnet' -Arguments @(
            'restore', $testProject, '--locked-mode', '--configfile', 'nuget.cwbi.config'
        )
    }
    foreach ($testProject in $releaseTestProjects) {
        Invoke-ReleaseCommand -Name "Release project build ($testProject)" -WorkingDirectory $verificationRoot -Command 'dotnet' -Arguments @(
            'build', $testProject, '--configuration', 'Release', '--no-restore'
        )
    }
    foreach ($testProject in $releaseTestProjects) {
        Invoke-ReleaseCommand -Name "Complete test suite ($testProject)" -WorkingDirectory $verificationRoot -Command 'dotnet' -Arguments @(
            'test', $testProject, '--configuration', 'Release', '--no-build'
        )
    }
    Write-Host '[NuGet vulnerability audit]'
    Push-Location $verificationRoot
    try {
        # Each shipped project is audited on its own (the command reports only the named project);
        # the shared parser then requires a clean result from every one of them.
        $auditOutput = [System.Collections.Generic.List[string]]::new()
        foreach ($auditProject in $releaseAuditProjects) {
            $projectOutput = @(& dotnet list $auditProject package --vulnerable --include-transitive --no-restore 2>&1)
            $auditExitCode = $LASTEXITCODE
            $projectOutput | Write-Host
            if ($auditExitCode -ne 0) {
                throw "NuGet vulnerability audit of $auditProject failed with exit code $auditExitCode."
            }
            foreach ($line in $projectOutput) {
                $auditOutput.Add([string]$line)
            }
        }
        Assert-CwbiNuGetAuditClean -AuditOutput @($auditOutput)
    }
    finally {
        Pop-Location
    }
    Invoke-ReleaseCommand -Name 'CWBI API image build' -WorkingDirectory $verificationRoot -Command 'docker' -Arguments @(
        'build',
        '--file', 'Dockerfile',
        '--tag', $imageTag,
        '--build-arg', 'SOURCE_REPOSITORY=https://github.com/USACE-RMC/RMC-TotalRisk',
        '--build-arg', "SOURCE_REVISION=$($export.SourceCommit)",
        '--build-arg', "SNAPSHOT_REVISION=$localSnapshotRevision",
        '.'
    )
    Invoke-ReleaseCommand -Name 'CWBI API image verification' -WorkingDirectory $verificationRoot -Command 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.github/scripts/Verify-TotalRiskImage.ps1',
        '-ImageRef', $imageTag,
        '-ExpectedSourceRevision', $export.SourceCommit,
        '-ExpectedSnapshotRevision', $localSnapshotRevision
    )

    Remove-VerificationArtifacts -Root $verificationRoot -SnapshotManifest $snapshotManifest
    Assert-CwbiReleasePathPolicy `
        -Root $verificationRoot `
        -PolicyPath $export.PolicyPath `
        -ExpectedPolicySha256 $export.PolicySha256
    Assert-CwbiSecretsAbsent `
        -Root $verificationRoot `
        -PolicyPath $export.PolicyPath `
        -ExpectedPolicySha256 $export.PolicySha256
    $verifiedManifest = @(Get-CwbiContentManifest -Root $verificationRoot)
    $manifestDifference = @(Compare-Object $snapshotManifest $verifiedManifest -Property Path, Sha256)
    if ($manifestDifference.Count -gt 0) {
        $changedPaths = @($manifestDifference | ForEach-Object { $_.Path } | Sort-Object -Unique)
        throw "Verification changed release inputs: $($changedPaths -join ', ')."
    }
    $verifiedManifestSha256 = Get-CwbiManifestSha256 -Manifest $verifiedManifest
    if ($verifiedManifestSha256 -cne $export.ManifestSha256) {
        throw 'Verified manifest digest does not match the immutable export.'
    }

    Write-Host '[Create local CWBI snapshot commit]'
    Assert-CwbiPolicyDigest -PolicyPath $export.PolicyPath -ExpectedPolicySha256 $export.PolicySha256
    $targetResult = Publish-CwbiSnapshotToWorktree `
        -TargetWorktree $TargetWorktree `
        -SnapshotRoot $snapshotRoot `
        -BranchName $ExpectedTargetBranch `
        -SourceCommit $export.SourceCommit `
        -SourceTree $export.SourceTree `
        -PolicySha256 $export.PolicySha256 `
        -PolicyPath $export.PolicyPath `
        -ManifestSha256 $export.ManifestSha256 `
        -EvidenceDirectory $EvidenceDirectory `
        -ExpectedCurrentBranch $ExpectedTargetBranch `
        -ExpectedCurrentCommit $ExpectedTargetCommit `
        -DestinationBranchName 'cwbi-dev' `
        -ExpectedCommonGitDirectory $sourceCommonGitDirectory

    Write-Host "Prepared local $($targetResult.LocalBranch) commit $($targetResult.Commit) for $($targetResult.Branch) from $($export.SourceCommit)."
    Write-Host "Manifest SHA-256: $($export.ManifestSha256)"
    Write-Host "Evidence manifest: $($targetResult.EvidencePath)"
    Write-Host 'No remote was changed and nothing was pushed.'
    Write-Host 'Review locally with these non-mutating commands:'
    Write-Host "git -C `"$TargetWorktree`" status --short --branch"
    Write-Host "git -C `"$TargetWorktree`" show --stat --oneline $($targetResult.Commit)"
    Write-Host "git -C `"$TargetWorktree`" diff --stat $ExpectedTargetCommit $($targetResult.Commit)"
    Write-Host "git -C `"$TargetWorktree`" log --format=fuller --no-decorate -1 $($targetResult.Commit)"
    Write-Host 'No push was performed. The calling publisher must explicitly approve and push this exact commit.'
}
finally {
    if ($imageTag) {
        & docker image inspect $imageTag *> $null
        if ($LASTEXITCODE -eq 0) {
            & docker image rm --force $imageTag *> $null
        }
    }

    if (-not $KeepStaging -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    elseif ($KeepStaging) {
        Write-Host "Staging retained at $stagingRoot."
    }
}
