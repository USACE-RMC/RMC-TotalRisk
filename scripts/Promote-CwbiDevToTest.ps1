[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = 'cwbi-apps/dst-total-risk'
$sourceBranch = 'cwbi-dev'
$targetBranch = 'cwbi-test'

function Invoke-PromotionGh {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return , @($output | ForEach-Object { [string]$_ })
}

function Invoke-PromotionGhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $text = (Invoke-PromotionGh -Arguments $Arguments) -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "gh $($Arguments -join ' ') returned no data."
    }
    return $text | ConvertFrom-Json
}

function Get-PromotionBranchCommit {
    param([Parameter(Mandatory)][string]$Branch)

    return [string](Invoke-PromotionGhJson -Arguments @('api', "repos/$repository/branches/$Branch")).commit.sha
}

function Get-PromotionFiles {
    param([Parameter(Mandatory)][object]$Comparison)

    if ($Comparison.PSObject.Properties.Name -notcontains 'files') {
        return , @()
    }
    return , @($Comparison.files)
}

function Get-PromotionSourceLine {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Message)

    $line = @($Message -split "`r?`n" | Where-Object { $_ -like 'Source commit:*' } | Select-Object -First 1)
    if ($line.Count -eq 0) {
        return 'Source commit: (not recorded)'
    }
    return $line[0].Trim()
}

Write-Host '[1/5] Check GitHub access'
if (-not (Get-Command -Name gh -ErrorAction SilentlyContinue)) {
    throw 'The GitHub CLI (gh) is not installed. Install it and run gh auth login before promoting.'
}
Invoke-PromotionGh -Arguments @('auth', 'status', '--hostname', 'github.com') | Out-Null

Write-Host "[2/5] Read $sourceBranch and $targetBranch in $repository"
$devCommit = Get-PromotionBranchCommit -Branch $sourceBranch
$testCommit = Get-PromotionBranchCommit -Branch $targetBranch
$promotion = Invoke-PromotionGhJson -Arguments @('api', "repos/$repository/compare/$targetBranch...$devCommit")
if ([int]$promotion.ahead_by -eq 0) {
    Write-Host "$targetBranch already contains $sourceBranch at $devCommit. Nothing to promote."
    return
}

$testOnly = Invoke-PromotionGhJson -Arguments @('api', "repos/$repository/compare/$devCommit...$testCommit")
$testOnlyFiles = Get-PromotionFiles -Comparison $testOnly
if ($testOnlyFiles.Count -gt 0) {
    $names = ($testOnlyFiles | Select-Object -First 10 | ForEach-Object { $_.filename }) -join ', '
    throw "$targetBranch has changes that $sourceBranch does not ($($testOnlyFiles.Count) file(s): $names). Nothing was changed. Bring those changes into $sourceBranch through a normal publish before promoting."
}

Write-Host '[3/5] Review the promotion'
$devMessage = [string](Invoke-PromotionGhJson -Arguments @('api', "repos/$repository/commits/$devCommit")).commit.message
Write-Host "Promote ${sourceBranch}: $devCommit ($(Get-PromotionSourceLine -Message $devMessage))"
Write-Host "Into ${targetBranch}:   $testCommit"
Write-Host "Snapshots to promote ($($promotion.ahead_by)):"
foreach ($commit in @($promotion.commits)) {
    $subject = ([string]$commit.commit.message -split "`r?`n")[0]
    Write-Host "  $(([string]$commit.sha).Substring(0, 12)) $subject  $(Get-PromotionSourceLine -Message ([string]$commit.commit.message))"
}
$promotedFiles = Get-PromotionFiles -Comparison $promotion
Write-Host "Files changed ($($promotedFiles.Count)):"
foreach ($file in $promotedFiles) {
    Write-Host "  $($file.status) $($file.filename)"
}

$openPulls = @(Invoke-PromotionGhJson -Arguments @(
    'pr', 'list', '--repo', $repository, '--base', $targetBranch, '--head', $sourceBranch,
    '--state', 'open', '--json', 'number,url'
))
if ($openPulls.Count -gt 0) {
    Write-Host "An open promotion pull request already exists and will be used: $($openPulls[0].url)"
}

$approval = Read-Host "Type PROMOTE to merge $sourceBranch at $($devCommit.Substring(0, 12)) into $targetBranch in $repository"
if ($approval -cne 'PROMOTE') {
    Write-Host 'Promotion cancelled. Nothing was changed.'
    return
}

Write-Host '[4/5] Open and merge the promotion pull request'
if ($openPulls.Count -gt 0) {
    $pullUrl = [string]$openPulls[0].url
}
else {
    $body = "Promotes $sourceBranch at $devCommit to $targetBranch.`n`n$(Get-PromotionSourceLine -Message $devMessage)"
    $createOutput = Invoke-PromotionGh -Arguments @(
        'pr', 'create', '--repo', $repository, '--base', $targetBranch, '--head', $sourceBranch,
        '--title', "Promote $sourceBranch to $targetBranch", '--body', $body
    )
    $pullUrl = ([string]$createOutput[-1]).Trim()
    Write-Host "Opened $pullUrl"
}
Invoke-PromotionGh -Arguments @(
    'pr', 'merge', $pullUrl, '--repo', $repository, '--merge', '--match-head-commit', $devCommit
) | Out-Null

Write-Host '[5/5] Verify the merge'
$merged = Invoke-PromotionGhJson -Arguments @('pr', 'view', $pullUrl, '--repo', $repository, '--json', 'state,mergeCommit')
if ([string]$merged.state -cne 'MERGED') {
    throw "The promotion pull request $pullUrl is $($merged.state), not merged."
}
$mergeCommit = [string]$merged.mergeCommit.oid
$parents = @((Invoke-PromotionGhJson -Arguments @('api', "repos/$repository/commits/$mergeCommit")).parents | ForEach-Object { [string]$_.sha })
if ($parents.Count -ne 2 -or $parents -cnotcontains $devCommit) {
    throw "Merge commit $mergeCommit is not a merge of $sourceBranch at $devCommit. Inspect $targetBranch in $repository."
}
$currentTestCommit = Get-PromotionBranchCommit -Branch $targetBranch
if ($currentTestCommit -cne $mergeCommit) {
    throw "$targetBranch resolves to $currentTestCommit instead of merge commit $mergeCommit. Inspect $targetBranch in $repository."
}

Write-Host "Promoted $sourceBranch at $devCommit to $targetBranch with merge commit $mergeCommit."
Write-Host "Pull request: $pullUrl"
Write-Host "Test workflow runs: https://github.com/$repository/actions?query=branch%3A$targetBranch"
