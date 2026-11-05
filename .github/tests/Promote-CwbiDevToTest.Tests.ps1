Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$promoteScriptPath = Join-Path $repositoryRoot 'scripts\Promote-CwbiDevToTest.ps1'
$cwbiRepository = 'cwbi-apps/dst-total-risk'
$devCommit = 'd' * 40
$testCommit = 'e' * 40
$mergeCommit = 'f' * 40
$pullUrl = "https://github.com/$cwbiRepository/pull/7"
$sourceCommit = 'a' * 40
$sourceLine = "Source commit: $sourceCommit"
$snapshotMessage = "Create CWBI development snapshot`n`n$sourceLine`nSource tree: $('b' * 40)"

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

function ConvertTo-FakeJson {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object]$Value)

    return ConvertTo-Json -InputObject $Value -Depth 6 -Compress
}

function Reset-FakeGitHub {
    $global:FakeGh = @{
        Repository = $cwbiRepository
        DevCommit = $devCommit
        TestCommit = $testCommit
        MergeCommit = $mergeCommit
        PullUrl = $pullUrl
        AheadBy = 1
        TestOnlyFiles = @()
        OpenPulls = @()
        MergeParents = @($testCommit, $devCommit)
        TestAfterMerge = $mergeCommit
        DevMessage = $snapshotMessage
        MergeMessage = "Promote cwbi-dev $($devCommit.Substring(0, 12)) to cwbi-test`n`n$sourceLine"
        FailMerge = $false
        Merged = $false
    }
    $global:FakeGhCalls = [System.Collections.Generic.List[string]]::new()
    $global:FakeApproval = 'PROMOTE'
    $global:FakePromptCount = 0
}

function global:Read-Host {
    param([Parameter(Position = 0)][object]$Prompt)

    $global:FakePromptCount++
    return $global:FakeApproval
}

function global:gh {
    $state = $global:FakeGh
    $line = $args -join ' '
    $global:FakeGhCalls.Add($line)
    $global:LASTEXITCODE = 0
    $repo = [regex]::Escape($state.Repository)

    switch -Regex ($line) {
        '^auth status' { return 'Logged in to github.com' }
        "^api repos/$repo/branches/cwbi-dev$" { return ConvertTo-FakeJson @{ commit = @{ sha = $state.DevCommit } } }
        "^api repos/$repo/branches/cwbi-test$" {
            $sha = if ($state.Merged) { $state.TestAfterMerge } else { $state.TestCommit }
            return ConvertTo-FakeJson @{ commit = @{ sha = $sha } }
        }
        "^api repos/$repo/compare/cwbi-test\.\.\.$($state.DevCommit)$" {
            return ConvertTo-FakeJson @{
                ahead_by = $state.AheadBy
                commits = @(@{ sha = $state.DevCommit; commit = @{ message = $state.DevMessage } })
                files = @(@{ status = 'modified'; filename = 'TotalRisk.API/Program.cs' })
            }
        }
        "^api repos/$repo/compare/$($state.DevCommit)\.\.\.$($state.TestCommit)$" {
            return ConvertTo-FakeJson @{ files = @($state.TestOnlyFiles) }
        }
        "^api repos/$repo/commits/$($state.DevCommit)$" { return ConvertTo-FakeJson @{ commit = @{ message = $state.DevMessage } } }
        "^api repos/$repo/commits/$($state.MergeCommit)$" {
            return ConvertTo-FakeJson @{
                parents = @($state.MergeParents | ForEach-Object { @{ sha = $_ } })
                commit = @{ message = $state.MergeMessage }
            }
        }
        '^pr list' { return ConvertTo-FakeJson @($state.OpenPulls) }
        '^pr create' { return $state.PullUrl }
        '^pr merge' {
            if ($state.FailMerge) {
                $global:LASTEXITCODE = 1
                return 'Head branch was modified'
            }
            $state.Merged = $true
            return ''
        }
        '^pr view' { return ConvertTo-FakeJson @{ state = 'MERGED'; mergeCommit = @{ oid = $state.MergeCommit } } }
        default { throw "Unexpected gh call: $line" }
    }
}

function Invoke-Promotion {
    & $promoteScriptPath 6> $null
}

function Get-FakeCalls {
    param([Parameter(Mandatory)][string]$Prefix)

    return , @($global:FakeGhCalls | Where-Object { $_.StartsWith($Prefix) })
}

try {
    Assert-Condition (Test-Path -LiteralPath $promoteScriptPath -PathType Leaf) 'The CWBI dev-to-test promotion script is missing.'

    <#
        Protects: the promotion always targets this repository's own cwbi-apps repository and never
        uses a merge method or option that would rewrite or delete a CWBI branch.
        If broken: a promotion could squash or rebase into cwbi-test, making every later promotion
        conflict, or delete cwbi-dev after the merge.
    #>
    $promoteText = Get-Content -LiteralPath $promoteScriptPath -Raw
    Assert-Condition ($promoteText -match [regex]::Escape("`$repository = '$cwbiRepository'")) "The promotion script does not target $cwbiRepository."
    foreach ($forbidden in @('--squash', '--rebase', '--delete-branch', '--admin', '--auto', '--force')) {
        Assert-Condition ($promoteText -notmatch [regex]::Escape($forbidden)) "The promotion script uses the forbidden option $forbidden."
    }

    <#
        Protects: a normal promotion opens a pull request from cwbi-dev into cwbi-test and merges it
        with a merge commit pinned to the reviewed cwbi-dev commit.
        If broken: the promotion would not reach the test environment, or could merge a cwbi-dev
        commit that was published after the review.
    #>
    Reset-FakeGitHub
    Invoke-Promotion
    $createCalls = Get-FakeCalls -Prefix 'pr create'
    Assert-Condition ($createCalls.Count -eq 1) 'A promotion did not open exactly one pull request.'
    Assert-Condition ($createCalls[0] -match "--repo $([regex]::Escape($cwbiRepository)) --base cwbi-test --head cwbi-dev") 'The promotion pull request does not go from cwbi-dev into cwbi-test.'
    $mergeCalls = Get-FakeCalls -Prefix 'pr merge'
    Assert-Condition ($mergeCalls.Count -eq 1) 'A promotion did not merge exactly once.'
    Assert-Condition ($mergeCalls[0] -match '--merge') 'The promotion did not merge with a merge commit.'
    Assert-Condition ($mergeCalls[0] -match "--match-head-commit $devCommit") 'The promotion merge is not pinned to the reviewed cwbi-dev commit.'
    Assert-Condition ($global:FakePromptCount -eq 1) 'The promotion did not ask for confirmation exactly once.'

    <#
        Protects: the merge commit message carries the cwbi-dev snapshot's Source commit line.
        If broken: the test workflow stops at "Resolve upstream source provenance" and nothing
        reaches the test environment.
    #>
    Reset-FakeGitHub
    Invoke-Promotion
    $mergeCall = (Get-FakeCalls -Prefix 'pr merge')[0]
    Assert-Condition ($mergeCall.EndsWith("--body $sourceLine")) 'The promotion merge does not set the Source commit line as the merge commit body.'
    Assert-Condition ($mergeCall -match "--subject Promote cwbi-dev $($devCommit.Substring(0, 12)) to cwbi-test") 'The promotion merge does not name the promoted cwbi-dev commit in its subject.'

    <#
        Protects: a cwbi-dev snapshot without a Source commit line is never promoted.
        If broken: the user would confirm a promotion whose test build is certain to fail.
    #>
    Reset-FakeGitHub
    $global:FakeGh.DevMessage = 'Create CWBI development snapshot'
    Assert-Throws -MessagePattern "does not carry exactly one 'Source commit: <sha>' line" -FailureMessage 'A cwbi-dev snapshot without a Source commit line was offered for promotion.' -Action { Invoke-Promotion }
    Assert-Condition ($global:FakePromptCount -eq 0) 'The user was asked to confirm a promotion of a snapshot without a Source commit line.'
    Assert-Condition ((Get-FakeCalls -Prefix 'pr ').Count -eq 0) 'A pull request action ran for a snapshot without a Source commit line.'

    <#
        Protects: the run fails when the merge commit lacks the Source commit line, for example
        when GitHub's default merge message was used.
        If broken: the script would report success while the test build fails.
    #>
    Reset-FakeGitHub
    $global:FakeGh.MergeMessage = "Merge pull request #7 from $cwbiRepository/cwbi-dev`n`nPromote cwbi-dev to cwbi-test"
    Assert-Throws -MessagePattern 'does not carry exactly the line' -FailureMessage 'A merge commit without the Source commit line was reported as a successful promotion.' -Action { Invoke-Promotion }

    <#
        Protects: a promotion pull request left open by an earlier, interrupted run is reused.
        If broken: a rerun would fail trying to open a second pull request for the same branches.
    #>
    Reset-FakeGitHub
    $global:FakeGh.OpenPulls = @(@{ number = 7; url = $pullUrl })
    Invoke-Promotion
    Assert-Condition ((Get-FakeCalls -Prefix 'pr create').Count -eq 0) 'An existing open promotion pull request was not reused.'
    Assert-Condition ((Get-FakeCalls -Prefix "pr merge $pullUrl").Count -eq 1) 'The existing promotion pull request was not merged.'

    <#
        Protects: nothing happens when cwbi-test already contains cwbi-dev.
        If broken: the user would be asked to confirm, and an empty pull request would be opened.
    #>
    Reset-FakeGitHub
    $global:FakeGh.AheadBy = 0
    Invoke-Promotion
    Assert-Condition ($global:FakePromptCount -eq 0) 'The user was asked to confirm a promotion with nothing to promote.'
    Assert-Condition ((Get-FakeCalls -Prefix 'pr ').Count -eq 0) 'A pull request action ran with nothing to promote.'

    <#
        Protects: the promotion stops when cwbi-test holds file changes that cwbi-dev lacks.
        If broken: test-only changes would be silently merged with the promotion, and cwbi-dev and
        cwbi-test would keep drifting apart.
    #>
    Reset-FakeGitHub
    $global:FakeGh.TestOnlyFiles = @(@{ status = 'modified'; filename = 'nuget.config' })
    Assert-Throws -MessagePattern 'cwbi-test has changes that cwbi-dev does not' -FailureMessage 'A promotion continued although cwbi-test had changes cwbi-dev lacks.' -Action { Invoke-Promotion }
    Assert-Condition ((Get-FakeCalls -Prefix 'pr ').Count -eq 0) 'A pull request action ran although the branches had diverged.'

    <#
        Protects: only the exact word PROMOTE approves a promotion.
        If broken: any keypress at the prompt could promote an unreviewed snapshot to test.
    #>
    Reset-FakeGitHub
    $global:FakeApproval = 'promote'
    Invoke-Promotion
    Assert-Condition ((Get-FakeCalls -Prefix 'pr create').Count -eq 0) 'A promotion opened a pull request without the exact PROMOTE confirmation.'
    Assert-Condition ((Get-FakeCalls -Prefix 'pr merge').Count -eq 0) 'A promotion merged without the exact PROMOTE confirmation.'

    <#
        Protects: a refused merge (for example, cwbi-dev moved after the review) stops the run with an error.
        If broken: the script would report a promotion that never happened.
    #>
    Reset-FakeGitHub
    $global:FakeGh.FailMerge = $true
    Assert-Throws -MessagePattern 'pr merge .* failed' -FailureMessage 'A refused merge was reported as a successful promotion.' -Action { Invoke-Promotion }

    <#
        Protects: the run checks that the merge commit really merges the reviewed cwbi-dev commit.
        If broken: a squash or a merge of a different commit would be reported as the reviewed promotion.
    #>
    Reset-FakeGitHub
    $global:FakeGh.MergeParents = @($testCommit)
    Assert-Throws -MessagePattern 'is not a merge of cwbi-dev' -FailureMessage 'A merge commit without the reviewed cwbi-dev parent was accepted.' -Action { Invoke-Promotion }

    <#
        Protects: the run checks that cwbi-test ends at the promotion merge commit.
        If broken: a promotion overtaken by another change to cwbi-test would be reported as deployed.
    #>
    Reset-FakeGitHub
    $global:FakeGh.TestAfterMerge = '1' * 40
    Assert-Throws -MessagePattern 'instead of merge commit' -FailureMessage 'A cwbi-test branch that does not end at the merge commit was accepted.' -Action { Invoke-Promotion }

    'All CWBI dev-to-test promotion tests passed.'
}
finally {
    Remove-Item -Path Function:\gh, Function:\Read-Host -ErrorAction SilentlyContinue
    Remove-Variable -Name FakeGh, FakeGhCalls, FakeApproval, FakePromptCount -Scope Global -ErrorAction SilentlyContinue
}
