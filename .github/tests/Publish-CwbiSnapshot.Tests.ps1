$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publisherScriptPath = Join-Path $repositoryRoot 'scripts\Publish-CwbiSnapshot.ps1'
$prepareScriptPath = Join-Path $repositoryRoot 'scripts\Prepare-CwbiRelease.ps1'
$modulePath = Join-Path $repositoryRoot 'scripts\cwbi-release\CwbiRelease.psm1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-cwbi-publisher-$([Guid]::NewGuid().ToString('N'))"
$tracePath = Join-Path $testRoot 'git-trace.json'
$originalAllowedProtocol = $env:GIT_ALLOW_PROTOCOL
$originalTrace = $env:GIT_TRACE2_EVENT

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -match $MessagePattern) {
            return
        }
        throw "$FailureMessage Actual: $($_.Exception.Message)"
    }
    throw $FailureMessage
}

function Invoke-TestGit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & git -c "safe.directory=$($Repository.Replace('\', '/'))" -c 'core.excludesFile=' -C $Repository @Arguments *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in $Repository."
    }
}

function New-TestSourceRepository {
    param([Parameter(Mandatory)][string]$Path)

    New-Item -ItemType Directory -Path $Path | Out-Null
    & git init --initial-branch v2.0-development $Path *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize the publisher test repository.'
    }
    Invoke-TestGit -Repository $Path -Arguments @('config', 'user.name', 'CWBI Publisher Test')
    Invoke-TestGit -Repository $Path -Arguments @('config', 'user.email', 'cwbi-publisher-test@example.invalid')
    Invoke-TestGit -Repository $Path -Arguments @('config', '--local', 'push.default', 'nothing')
    Invoke-TestGit -Repository $Path -Arguments @('remote', 'add', 'origin', 'https://github.com/USACE-RMC/RMC-TotalRisk.git')
    Invoke-TestGit -Repository $Path -Arguments @('remote', 'add', 'cwbi', 'https://github.com/cwbi-apps/dst-total-risk.git')
    Set-Content -LiteralPath (Join-Path $Path 'tracked.txt') -Value 'tracked'
    Invoke-TestGit -Repository $Path -Arguments @('add', '--all')
    Invoke-TestGit -Repository $Path -Arguments @('commit', '-m', 'Create publisher fixture')
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
$env:GIT_ALLOW_PROTOCOL = 'file'
$env:GIT_TRACE2_EVENT = $tracePath

try {
    Assert-Condition (Test-Path -LiteralPath $publisherScriptPath -PathType Leaf) 'The one-command CWBI publisher script is missing.'

    $publisherCommand = Get-Command -Name $publisherScriptPath
    Assert-Condition $publisherCommand.Parameters.ContainsKey('PrepareOnly') 'The publisher lacks the non-publishing PrepareOnly mode.'
    Assert-Condition $publisherCommand.Parameters.ContainsKey('SourceRepository') 'The publisher lacks an explicit source-repository input for controlled execution.'
    $environmentParameter = $publisherCommand.Parameters['Environment']
    Assert-Condition ($null -ne $environmentParameter) 'The publisher lacks the -Environment selector.'
    $environmentMandatory = @($environmentParameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory })
    Assert-Condition ($environmentMandatory.Count -gt 0) 'The publisher -Environment selector must be mandatory so no publish defaults to an environment.'
    $environmentSet = @($environmentParameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] })
    Assert-Condition ($environmentSet.Count -eq 1) 'The publisher -Environment selector lacks one exact value set.'
    Assert-Condition ((@($environmentSet[0].ValidValues) -join ',') -ceq 'dev,test') 'The publisher -Environment selector must accept exactly dev and test.'
    Assert-Condition (-not $environmentSet[0].IgnoreCase) 'The publisher -Environment selector must be case-sensitive.'

    $publisherText = Get-Content -LiteralPath $publisherScriptPath -Raw
    Assert-Condition ($publisherText -match [regex]::Escape('snapshot/sanitized')) 'The publisher does not use the temporary snapshot/sanitized branch.'
    Assert-Condition ($publisherText -match [regex]::Escape('$destinationBranch = "cwbi-$Environment"')) 'The publisher does not derive its destination branch from the selected environment.'
    Assert-Condition ($publisherText -match [regex]::Escape('"$snapshotCommit`:refs/heads/$destinationBranch"')) 'The publisher does not name the exact destination branch in its explicit push.'
    Assert-Condition ($publisherText -notmatch [regex]::Escape('refs/heads/cwbi-dev')) 'The publisher still hard-codes the dev destination branch.'
    Assert-Condition ($publisherText -match "Read-Host") 'The publisher does not require interactive publication approval.'
    Assert-Condition ($publisherText -match "-cne 'PUBLISH'") 'The publisher approval is not an exact PUBLISH confirmation.'
    Assert-Condition ($publisherText -notmatch 'RMC-TotalRisk-Snapshot') 'The publisher still depends on the discarded permanent snapshot worktree.'

    $prepareText = Get-Content -LiteralPath $prepareScriptPath -Raw
    Assert-Condition ($prepareText -notmatch 'configuredTargetWorktree') 'The preparation script still requires the old permanent dst-total-risk worktree.'
    Assert-Condition ($prepareText -match '-BranchName\s+\$ExpectedTargetBranch') 'The preparation script does not publish into the caller-verified temporary branch.'
    Assert-Condition ($prepareText -match '-DestinationBranchName\s+\$DestinationBranch\b') 'The preparation script does not record the caller-selected destination branch.'
    $prepareCommand = Get-Command -Name $prepareScriptPath
    $destinationParameter = $prepareCommand.Parameters['DestinationBranch']
    Assert-Condition ($null -ne $destinationParameter) 'The preparation script lacks a DestinationBranch input.'
    Assert-Condition (@($destinationParameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }).Count -gt 0) 'The preparation script DestinationBranch input must be mandatory.'
    $destinationPattern = @($destinationParameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidatePatternAttribute] })
    Assert-Condition ($destinationPattern.Count -eq 1) 'The preparation DestinationBranch input lacks one exact validation rule.'
    foreach ($acceptedDestination in @('cwbi-dev', 'cwbi-test')) {
        Assert-Condition ($acceptedDestination -cmatch $destinationPattern[0].RegexPattern) "The preparation script rejects the $acceptedDestination destination."
    }
    foreach ($rejectedDestination in @('cwbi-prod', 'main', 'snapshot/sanitized', 'cwbi-test2')) {
        Assert-Condition ($rejectedDestination -cnotmatch $destinationPattern[0].RegexPattern) "The preparation script accepts the unapproved $rejectedDestination destination."
    }

    Import-Module $modulePath -Force
    $publishCommand = Get-Command -Name Publish-CwbiSnapshotToWorktree
    $branchPattern = @($publishCommand.Parameters.BranchName.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidatePatternAttribute] })
    Assert-Condition ($branchPattern.Count -eq 1) 'The local snapshot branch lacks one exact validation rule.'
    Assert-Condition ('cwbi-dev' -match $branchPattern[0].RegexPattern) 'The legacy cwbi-dev preparation path is no longer accepted.'
    Assert-Condition ('snapshot/sanitized' -match $branchPattern[0].RegexPattern) 'The temporary snapshot/sanitized branch is not accepted.'
    Assert-Condition ('snapshot/unsafe' -notmatch $branchPattern[0].RegexPattern) 'An unapproved local snapshot branch is accepted.'
    Assert-Condition ((Get-CwbiSnapshotCommitSubject -DestinationBranchName 'cwbi-dev') -ceq 'Create CWBI development snapshot') 'Dev snapshots no longer carry the established development subject.'
    Assert-Condition ((Get-CwbiSnapshotCommitSubject -DestinationBranchName 'cwbi-test') -ceq 'Create CWBI test snapshot') 'Test snapshots do not name the test environment in their subject.'
    Assert-Throws -MessagePattern 'validation|does not match' -FailureMessage 'A snapshot subject was produced for an unapproved destination.' -Action {
        Get-CwbiSnapshotCommitSubject -DestinationBranchName 'cwbi-prod' | Out-Null
    }

    $dirtyRepository = Join-Path $testRoot 'dirty-source'
    New-TestSourceRepository -Path $dirtyRepository
    Set-Content -LiteralPath (Join-Path $dirtyRepository 'uncommitted.txt') -Value 'do not ignore'
    Assert-Throws -MessagePattern 'clean' -FailureMessage 'The publisher did not stop before network access when the source checkout was dirty.' -Action {
        & $publisherScriptPath -Environment test -SourceRepository $dirtyRepository -PrepareOnly
    }

    $traceEvents = if (Test-Path -LiteralPath $tracePath) { Get-Content -LiteralPath $tracePath -Raw } else { '' }
    Assert-Condition ($traceEvents -notmatch '"name":"fetch"') 'The dirty-source preflight performed a fetch before failing.'
    Assert-Condition ($traceEvents -notmatch '"name":"push"') 'The dirty-source preflight attempted a push.'

    Write-Host 'CWBI one-command publisher tests passed.'
}
finally {
    $env:GIT_ALLOW_PROTOCOL = $originalAllowedProtocol
    $env:GIT_TRACE2_EVENT = $originalTrace
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
