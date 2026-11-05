[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string]$SourceRepository = (Join-Path $PSScriptRoot '..'),
    [switch]$PrepareOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$originBranch = 'v2.0-development'
$localSnapshotBranch = 'snapshot/sanitized'
$temporaryContainer = $null
$temporaryWorktree = $null
$worktreeCreated = $false
$localBranchCreated = $false
$publicationSucceeded = $false

function Invoke-PublisherGit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $repositoryPath = [System.IO.Path]::GetFullPath($Repository)
    $safePath = $repositoryPath.Replace('\', '/')
    $output = @(& git -c "safe.directory=$safePath" -c 'core.excludesFile=' -C $repositoryPath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $details = if ($output.Count -gt 0) { " $($output -join [Environment]::NewLine)" } else { '' }
        throw "git $($Arguments -join ' ') failed in $repositoryPath.$details"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Get-PublisherCommit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Revision
    )

    $result = Invoke-PublisherGit -Repository $Repository -Arguments @('rev-parse', '--verify', "$Revision^{commit}")
    $commit = ([string]$result.Output[0]).Trim()
    if ($commit -notmatch '^[0-9a-f]{40}$') {
        throw "Revision $Revision did not resolve to an exact commit."
    }
    return $commit
}

function Get-PublisherDestinationCommit {
    param([Parameter(Mandatory)][string]$Repository)

    # Exit code 2 from `git ls-remote --exit-code` means the remote has no matching ref.
    $listing = Invoke-PublisherGit -Repository $Repository -Arguments @('ls-remote', '--exit-code', 'cwbi', 'refs/heads/cwbi-dev') -AllowFailure
    if ($listing.ExitCode -eq 2) {
        return 'unborn'
    }
    if ($listing.ExitCode -ne 0) {
        throw 'Unable to read refs/heads/cwbi-dev from the cwbi remote.'
    }
    $remoteCommit = ([string]$listing.Output[0]).Split("`t")[0].Trim()
    if ($remoteCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'The cwbi remote returned an unexpected cwbi-dev reference.'
    }
    Invoke-PublisherGit -Repository $Repository -Arguments @('fetch', '--no-tags', 'cwbi', 'refs/heads/cwbi-dev:refs/remotes/cwbi/cwbi-dev') | Out-Null
    $fetchedCommit = Get-PublisherCommit -Repository $Repository -Revision 'refs/remotes/cwbi/cwbi-dev'
    if ($fetchedCommit -cne $remoteCommit) {
        throw 'cwbi-dev changed between listing and fetching it. Run the publisher again.'
    }
    return $fetchedCommit
}

function Assert-PublisherClean {
    param([Parameter(Mandatory)][string]$Repository)

    $status = @((Invoke-PublisherGit -Repository $Repository -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output)
    if ($status.Count -gt 0) {
        throw 'The RMC-TotalRisk checkout must be clean before a CWBI snapshot is prepared.'
    }
}

function Assert-TemporaryContainerSafe {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ([System.IO.Path]::GetDirectoryName($resolvedPath) -cne $temporaryRoot -or
        [System.IO.Path]::GetFileName($resolvedPath) -notmatch '^total-risk-cwbi-publish-[0-9a-f]{32}$') {
        throw "Refusing to manage an unexpected temporary publication path: $resolvedPath."
    }
}

$sourcePath = (Resolve-Path -LiteralPath $SourceRepository).Path
$sourceTopLevel = ([string](Invoke-PublisherGit -Repository $sourcePath -Arguments @('rev-parse', '--show-toplevel')).Output[0]).Trim()
if ([System.IO.Path]::GetFullPath($sourceTopLevel) -cne $sourcePath) {
    throw "SourceRepository must be the exact RMC-TotalRisk checkout root: $sourceTopLevel."
}

try {
    Write-Host '[1/6] Check the local RMC-TotalRisk checkout'
    $currentBranch = ([string](Invoke-PublisherGit -Repository $sourcePath -Arguments @('branch', '--show-current')).Output[0]).Trim()
    if ($currentBranch -cne $originBranch) {
        throw "Switch to v2.0-development before publishing. Current branch: $currentBranch."
    }
    Assert-PublisherClean -Repository $sourcePath

    $modulePath = Join-Path $sourcePath 'scripts\cwbi-release\CwbiRelease.psm1'
    $prepareScriptPath = Join-Path $sourcePath 'scripts\Prepare-CwbiRelease.ps1'
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $prepareScriptPath -PathType Leaf)) {
        throw 'The clean RMC-TotalRisk checkout does not contain the CWBI release scripts.'
    }
    Import-Module $modulePath -Force
    Assert-CwbiPushTopology -Repository $sourcePath -BranchName $localSnapshotBranch

    $existingSnapshotBranch = Invoke-PublisherGit -Repository $sourcePath -Arguments @('show-ref', '--verify', '--quiet', "refs/heads/$localSnapshotBranch") -AllowFailure
    if ($existingSnapshotBranch.ExitCode -eq 0) {
        throw "Local branch $localSnapshotBranch already exists. Remove or rename it before publishing."
    }

    Write-Host '[2/6] Update v2.0-development and read the current CWBI branch'
    Invoke-PublisherGit -Repository $sourcePath -Arguments @('fetch', '--no-tags', 'origin', 'refs/heads/v2.0-development:refs/remotes/origin/v2.0-development') | Out-Null
    Invoke-PublisherGit -Repository $sourcePath -Arguments @('merge', '--ff-only', 'refs/remotes/origin/v2.0-development') | Out-Null
    Assert-PublisherClean -Repository $sourcePath
    $sourceCommit = Get-PublisherCommit -Repository $sourcePath -Revision 'HEAD'
    $originCommit = Get-PublisherCommit -Repository $sourcePath -Revision 'refs/remotes/origin/v2.0-development'
    if ($sourceCommit -cne $originCommit) {
        throw 'Local v2.0-development does not exactly match origin/v2.0-development. Push or remove local-only commits before publishing.'
    }

    # The destination may not have a cwbi-dev branch yet (a brand-new cwbi-apps repository).
    # In that case the snapshot becomes the root commit of cwbi-dev and the local workspace
    # starts as an empty orphan worktree. 'unborn' is the sentinel the preparation script and
    # release module accept for that state.
    $previousDestinationCommit = Get-PublisherDestinationCommit -Repository $sourcePath
    if ($previousDestinationCommit -ceq 'unborn') {
        Write-Host 'cwbi-dev does not exist yet on cwbi-apps/dst-total-risk. This run will create it from a root snapshot commit.'
    }

    Write-Host '[3/6] Create a disposable local snapshot workspace'
    $temporaryContainer = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-cwbi-publish-$([Guid]::NewGuid().ToString('N'))"
    Assert-TemporaryContainerSafe -Path $temporaryContainer
    New-Item -ItemType Directory -Path $temporaryContainer | Out-Null
    $temporaryWorktree = Join-Path $temporaryContainer 'snapshot'
    if ($previousDestinationCommit -ceq 'unborn') {
        Invoke-PublisherGit -Repository $sourcePath -Arguments @(
            'worktree', 'add', '--orphan', '-b', $localSnapshotBranch, $temporaryWorktree
        ) | Out-Null
    }
    else {
        Invoke-PublisherGit -Repository $sourcePath -Arguments @(
            'worktree', 'add', '-b', $localSnapshotBranch, $temporaryWorktree, $previousDestinationCommit
        ) | Out-Null
    }
    $worktreeCreated = $true
    $localBranchCreated = $true

    Write-Host '[4/6] Sanitize, build, test, scan, and prepare the snapshot'
    $evidenceDirectory = Join-Path (Split-Path -Parent $sourcePath) 'cwbi-release-evidence'
    & $prepareScriptPath `
        -SourceRevision $sourceCommit `
        -TargetWorktree $temporaryWorktree `
        -ExpectedTargetBranch $localSnapshotBranch `
        -ExpectedTargetCommit $previousDestinationCommit `
        -EvidenceDirectory $evidenceDirectory

    $snapshotCommit = Get-PublisherCommit -Repository $temporaryWorktree -Revision 'HEAD'
    $preparedBranch = ([string](Invoke-PublisherGit -Repository $temporaryWorktree -Arguments @('branch', '--show-current')).Output[0]).Trim()
    if ($preparedBranch -cne $localSnapshotBranch) {
        throw "The prepared snapshot moved to an unexpected branch: $preparedBranch."
    }

    Write-Host '[5/6] Review the prepared result'
    Write-Host "Source v2.0-development:       $sourceCommit"
    Write-Host "Previous cwbi-dev: $previousDestinationCommit"
    Write-Host "Prepared snapshot: $snapshotCommit"
    if ($previousDestinationCommit -ceq 'unborn') {
        (Invoke-PublisherGit -Repository $temporaryWorktree -Arguments @('show', '--stat', '--format=', $snapshotCommit)).Output | Write-Host
    }
    else {
        (Invoke-PublisherGit -Repository $temporaryWorktree -Arguments @('diff', '--stat', $previousDestinationCommit, $snapshotCommit)).Output | Write-Host
    }
    (Invoke-PublisherGit -Repository $temporaryWorktree -Arguments @('log', '-1', '--oneline', '--decorate=no', $snapshotCommit)).Output | Write-Host

    if ($snapshotCommit -ceq $previousDestinationCommit) {
        Write-Host 'cwbi-dev already contains this exact sanitized snapshot. Nothing needs to be pushed.'
        return
    }
    if ($PrepareOnly) {
        Write-Host 'PrepareOnly was requested. Nothing was pushed.'
        return
    }

    $approval = Read-Host 'Type PUBLISH to push this exact snapshot to cwbi-apps/dst-total-risk cwbi-dev'
    if ($approval -cne 'PUBLISH') {
        Write-Host 'Publication cancelled. Nothing was pushed.'
        return
    }

    Write-Host '[6/6] Recheck the destination and publish the exact snapshot'
    Assert-PublisherClean -Repository $sourcePath
    if ((Get-PublisherCommit -Repository $sourcePath -Revision 'HEAD') -cne $sourceCommit) {
        throw 'Local v2.0-development changed while the snapshot was being prepared. Run the publisher again.'
    }
    Assert-CwbiPushTopology -Repository $temporaryWorktree -BranchName $localSnapshotBranch
    $currentDestinationCommit = Get-PublisherDestinationCommit -Repository $sourcePath
    if ($currentDestinationCommit -cne $previousDestinationCommit) {
        throw 'cwbi-dev changed while the snapshot was being prepared. Nothing was pushed; run the publisher again.'
    }

    Invoke-PublisherGit -Repository $temporaryWorktree -Arguments @(
        'push', '--porcelain', 'cwbi', "$snapshotCommit`:refs/heads/cwbi-dev"
    ) | Out-Null
    $remoteResult = Invoke-PublisherGit -Repository $temporaryWorktree -Arguments @(
        'ls-remote', '--exit-code', 'cwbi', 'refs/heads/cwbi-dev'
    )
    $publishedCommit = ([string]$remoteResult.Output[0]).Split("`t")[0].Trim()
    if ($publishedCommit -cne $snapshotCommit) {
        throw "Push returned successfully, but cwbi-dev resolves to $publishedCommit instead of $snapshotCommit."
    }

    $publicationSucceeded = $true
    Write-Host "Published and verified cwbi-dev at $snapshotCommit."
}
finally {
    $cleanupProblems = [System.Collections.Generic.List[string]]::new()
    if ($worktreeCreated -and $temporaryWorktree -and (Test-Path -LiteralPath $temporaryWorktree)) {
        $removeResult = Invoke-PublisherGit -Repository $sourcePath -Arguments @('worktree', 'remove', '--force', $temporaryWorktree) -AllowFailure
        if ($removeResult.ExitCode -ne 0) {
            $cleanupProblems.Add("temporary worktree remains at $temporaryWorktree")
        }
    }
    if ($localBranchCreated) {
        $branchResult = Invoke-PublisherGit -Repository $sourcePath -Arguments @('branch', '-D', $localSnapshotBranch) -AllowFailure
        if ($branchResult.ExitCode -ne 0) {
            $cleanupProblems.Add("temporary branch $localSnapshotBranch remains")
        }
    }
    if ($temporaryContainer) {
        Assert-TemporaryContainerSafe -Path $temporaryContainer
        if (Test-Path -LiteralPath $temporaryContainer) {
            if ($cleanupProblems.Count -eq 0) {
                Remove-Item -LiteralPath $temporaryContainer -Recurse -Force
            }
            else {
                $cleanupProblems.Add("temporary container remains at $temporaryContainer")
            }
        }
    }
    if ($cleanupProblems.Count -gt 0) {
        Write-Warning "CWBI publication cleanup was incomplete: $($cleanupProblems -join '; ')."
    }
    elseif ($temporaryContainer) {
        $outcome = if ($publicationSucceeded) { 'after publication' } else { 'without publishing' }
        Write-Host "Temporary snapshot workspace removed $outcome."
    }
}
