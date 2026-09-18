[CmdletBinding()]
param(
    # A local clone of https://github.com/USACE-RMC/Numerics that contains the commit to package.
    [ValidateNotNullOrEmpty()][string]$NumericsRepository = 'C:\GIT\numerics',
    # The commit to package. Defaults to the checkout's current HEAD. Only the packaged commit is
    # read; the checkout's working tree is never built or modified.
    [ValidatePattern('^([0-9a-fA-F]{7,40}|HEAD)$')][string]$Commit = 'HEAD'
)

# Packs the in-development RMC.Numerics library into this repository's local NuGet feed.
#
# Why this exists: RMC-TotalRisk depends on Numerics 2.2.0 APIs that are not yet published on
# nuget.org. Until that release lands, the library is consumed as a prerelease package
# (2.2.0-dev.<commit>) restored from local-feed/, which keeps every clone, the Docker image, and
# the CWBI workflow buildable without a sibling Numerics checkout. When 2.2.0 is published:
# delete local-feed/, remove the two local-feed package sources (NuGet.config and
# nuget.cwbi.config), pin RMC.Numerics to 2.2.0 in Directory.Packages.props, and regenerate the
# packages.lock.json files.
#
# The package is built from a throwaway clone at the exact commit, with the clone's origin pointed
# at the public GitHub repository so the embedded Source Link records the true commit.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$feedDirectory = Join-Path $repositoryRoot 'local-feed'
$sourceUrl = 'https://github.com/USACE-RMC/Numerics'

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$sourcePath = (Resolve-Path -LiteralPath $NumericsRepository).Path
$fullCommit = (& git -C $sourcePath rev-parse --verify "$Commit^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $fullCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Unable to resolve commit '$Commit' in $sourcePath."
}
$shortCommit = $fullCommit.Substring(0, 7)
$cloneRoot = Join-Path ([System.IO.Path]::GetTempPath()) "totalrisk-numerics-pack-$([Guid]::NewGuid().ToString('N'))"

try {
    Write-Host "[1/4] Clone Numerics at $fullCommit"
    Invoke-Checked -Command 'git' -Arguments @('clone', '--quiet', '--no-hardlinks', $sourcePath, $cloneRoot)
    Invoke-Checked -Command 'git' -Arguments @('-C', $cloneRoot, 'checkout', '--quiet', '--detach', $fullCommit)
    Invoke-Checked -Command 'git' -Arguments @('-C', $cloneRoot, 'remote', 'set-url', 'origin', "$sourceUrl.git")

    $projectXml = [xml](Get-Content -LiteralPath (Join-Path $cloneRoot 'Numerics\Numerics.csproj') -Raw)
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw 'Numerics.csproj does not declare a <Version>.'
    }
    $baseVersion = $versionNode.InnerText.Trim()
    $packageVersion = "$baseVersion-dev.$shortCommit"

    Write-Host "[2/4] Pack RMC.Numerics $packageVersion (net10.0, Release)"
    New-Item -ItemType Directory -Force -Path $feedDirectory | Out-Null
    Invoke-Checked -Command 'dotnet' -Arguments @(
        'pack', (Join-Path $cloneRoot 'Numerics\Numerics.csproj'),
        '--configuration', 'Release',
        '--output', $feedDirectory,
        "-p:Version=$packageVersion",
        '-p:TargetFrameworks=net10.0',
        '-p:ContinuousIntegrationBuild=true',
        "-p:RepositoryUrl=$sourceUrl",
        '-p:PublishRepositoryUrl=true',
        '--nologo', '--verbosity', 'minimal'
    )

    Write-Host '[3/4] Remove superseded local packages'
    foreach ($stale in Get-ChildItem -LiteralPath $feedDirectory -Filter 'RMC.Numerics.*.nupkg' -File) {
        if ($stale.Name -cne "RMC.Numerics.$packageVersion.nupkg") {
            Remove-Item -LiteralPath $stale.FullName -Force
            Write-Host "  removed $($stale.Name)"
        }
    }

    Write-Host '[4/4] Done'
    Write-Host "Package: local-feed/RMC.Numerics.$packageVersion.nupkg"
    Write-Host "Pin this version in Directory.Packages.props, then run 'dotnet restore --force-evaluate' to refresh the lock files."
}
finally {
    if (Test-Path -LiteralPath $cloneRoot) {
        Remove-Item -LiteralPath $cloneRoot -Recurse -Force
    }
}
