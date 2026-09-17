$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modulePath = Join-Path $repositoryRoot 'scripts\cwbi-release\CwbiRelease.psm1'
$prepareScriptPath = Join-Path $repositoryRoot 'scripts\Prepare-CwbiRelease.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-cwbi-release-$([Guid]::NewGuid())"
$gitTracePath = Join-Path $testRoot 'git-trace2-events.json'
$originalAllowedProtocol = $env:GIT_ALLOW_PROTOCOL
$originalGitTrace2Event = $env:GIT_TRACE2_EVENT

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & git -c "safe.directory=$($Repository.Replace('\', '/'))" -c 'core.excludesFile=' -C $Repository @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }

    [pscustomobject]@{ ExitCode = $exitCode; Output = @($output) }
}

function New-TestRepository {
    param([Parameter(Mandatory)][string]$Path)

    New-Item -ItemType Directory -Path $Path | Out-Null
    & git init --initial-branch v2.0-development $Path *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize a release test repository.'
    }
    Invoke-Git -Repository $Path -Arguments @('config', 'user.name', 'CWBI Release Test') | Out-Null
    Invoke-Git -Repository $Path -Arguments @('config', 'user.email', 'cwbi-release-test@example.invalid') | Out-Null
}

function Add-TestCommit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Message
    )

    Invoke-Git -Repository $Repository -Arguments @('add', '--all') | Out-Null
    Invoke-Git -Repository $Repository -Arguments @('commit', '-m', $Message) | Out-Null
    return ([string](Invoke-Git -Repository $Repository -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    $failed = $false
    $caughtMessage = '<no exception>'
    try {
        & $Action
    }
    catch {
        $caughtMessage = "$($_.Exception.Message) [$($_.ScriptStackTrace)]"
        $failed = $_.Exception.Message -match $MessagePattern
    }
    Assert-Condition $failed "$FailureMessage Actual: $caughtMessage"
}

function Get-RemoteGitOperationStarts {
    param([Parameter(Mandatory)][string]$TracePath)

    if (-not (Test-Path -LiteralPath $TracePath -PathType Leaf)) {
        throw "Git trace file is missing: $TracePath"
    }
    $forbiddenCommands = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($command in @('fetch', 'push', 'pull', 'clone', 'ls-remote', 'fetch-pack', 'send-pack', 'upload-pack', 'receive-pack')) {
        $null = $forbiddenCommands.Add($command)
    }
    $starts = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $TracePath).Path)) {
        $lineNumber++
        try {
            $traceEvent = $line | ConvertFrom-Json
        }
        catch {
            throw "Git trace contains malformed JSON at line $lineNumber."
        }
        if ($traceEvent.event -ceq 'cmd_name' -and $forbiddenCommands.Contains([string]$traceEvent.name)) {
            $null = $starts.Add("git $($traceEvent.name)")
        }
        if ($traceEvent.event -notin @('start', 'child_start')) {
            continue
        }
        if ([string]$traceEvent.child_class -match 'remote') {
            $null = $starts.Add('Git remote helper child')
        }
        foreach ($argument in @($traceEvent.argv)) {
            $leafName = [System.IO.Path]::GetFileNameWithoutExtension([string]$argument)
            if ($leafName -match '^git-remote-' -or
                $leafName -in @('git-fetch-pack', 'git-send-pack', 'git-upload-pack', 'git-receive-pack')) {
                $null = $starts.Add($leafName)
            }
        }
    }
    return @($starts)
}

function Assert-NoRemoteGitOperations {
    param([Parameter(Mandatory)][string]$TracePath)

    $operationStarts = @(Get-RemoteGitOperationStarts -TracePath $TracePath)
    if ($operationStarts.Count -gt 0) {
        throw "Git trace recorded a forbidden remote Git operation start: $($operationStarts -join ', ')."
    }
}

function Set-TestPolicy {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [string[]]$AllowedFiles = @('app.txt'),
        [string[]]$AllowedDirectories = @('src/')
    )

    $policyDirectory = Join-Path $Repository 'scripts\cwbi-release'
    New-Item -ItemType Directory -Path $policyDirectory -Force | Out-Null
    $policy = [ordered]@{
        schemaVersion = 1
        allowedFiles = $AllowedFiles
        allowedDirectories = $AllowedDirectories
        excludedPaths = @('src/dev.txt')
        requiredFiles = @('app.txt', 'src/keep.txt')
        secretScanExcludedFiles = @()
        secretBearingFileExtensions = @(
            '.cer', '.crt', '.der', '.jks', '.key', '.keystore', '.p12', '.pem', '.pfx'
        )
        secretBearingFileNames = @('id_dsa', 'id_ecdsa', 'id_ed25519', 'id_rsa')
        generatedOutputExclusion = 'test_scenarios/**/csharp/output.json'
    }
    $policy | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $policyDirectory 'policy.json')
}

function Set-TestTargetSafety {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [string]$OriginUrl = 'https://github.com/USACE-RMC/RMC-TotalRisk.git',
        [string]$CwbiUrl = 'https://github.com/cwbi-apps/dst-total-risk.git'
    )

    foreach ($remote in @(
        [pscustomobject]@{ Name = 'origin'; Url = $OriginUrl },
        [pscustomobject]@{ Name = 'cwbi'; Url = $CwbiUrl }
    )) {
        $existing = Invoke-Git -Repository $Repository -Arguments @('remote', 'get-url', $remote.Name) -AllowFailure
        if ($existing.ExitCode -eq 0) {
            Invoke-Git -Repository $Repository -Arguments @('remote', 'set-url', $remote.Name, $remote.Url) | Out-Null
        }
        else {
            Invoke-Git -Repository $Repository -Arguments @('remote', 'add', $remote.Name, $remote.Url) | Out-Null
        }
    }
    Invoke-Git -Repository $Repository -Arguments @('config', '--local', 'push.default', 'nothing') | Out-Null
}

function Remove-TestJunction {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Refusing to remove a non-junction test path: $Path"
    }
    [System.IO.Directory]::Delete($item.FullName)
}

function Remove-TestEvidenceFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$TestRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullTestRoot = [System.IO.Path]::GetFullPath($TestRoot).TrimEnd('\', '/')
    if (-not $fullPath.StartsWith("$fullTestRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an evidence fixture outside the test root: $fullPath"
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        [System.IO.Directory]::Delete($item.FullName)
    }
    else {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function New-RawPathTestRepository {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RawPath
    )

    New-TestRepository -Path $Path
    Invoke-Git -Repository $Path -Arguments @('config', 'core.protectNTFS', 'false') | Out-Null
    Invoke-Git -Repository $Path -Arguments @('config', 'core.protectHFS', 'false') | Out-Null
    Invoke-Git -Repository $Path -Arguments @('config', 'core.quotePath', 'true') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Path 'src') | Out-Null
    Set-Content -LiteralPath (Join-Path $Path 'app.txt') -Value 'app' -NoNewline
    Set-Content -LiteralPath (Join-Path $Path 'src\keep.txt') -Value 'keep' -NoNewline
    Set-TestPolicy -Repository $Path
    Add-TestCommit -Repository $Path -Message 'Add portable source base' | Out-Null

    $payloadPath = Join-Path $Path 'raw-path-payload.txt'
    Set-Content -LiteralPath $payloadPath -Value 'raw path payload' -NoNewline
    $blob = ([string](Invoke-Git -Repository $Path -Arguments @('hash-object', '-w', 'raw-path-payload.txt')).Output[0]).Trim()
    Remove-Item -LiteralPath $payloadPath
    Invoke-Git -Repository $Path -Arguments @('update-index', '--add', '--cacheinfo', '100644', $blob, $RawPath) | Out-Null
    Invoke-Git -Repository $Path -Arguments @('commit', '-m', 'Add raw unsafe path') | Out-Null
    return ([string](Invoke-Git -Repository $Path -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
}

if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "CWBI release module is missing: $modulePath"
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
$env:GIT_ALLOW_PROTOCOL = 'file'
$env:GIT_TRACE2_EVENT = $gitTracePath

try {
    Import-Module $modulePath -Force
    Assert-Condition ($env:GIT_ALLOW_PROTOCOL -ceq 'file') 'The no-network Git protocol guard was not active before the first release function invocation.'
    Assert-Condition ($env:GIT_TRACE2_EVENT -ceq $gitTracePath) 'Run-local Git tracing was not active before the first release function invocation.'

    $traceGuardFixture = Join-Path $testRoot 'remote-trace-guard-fixture.json'
    [ordered]@{ event = 'cmd_name'; name = 'fetch' } | ConvertTo-Json -Compress | Set-Content -LiteralPath $traceGuardFixture
    try {
        Assert-Throws -MessagePattern 'remote Git operation' -FailureMessage 'The Git trace guard did not detect a synthetic fetch start.' -Action {
            Assert-NoRemoteGitOperations -TracePath $traceGuardFixture
        }
    }
    finally {
        Remove-Item -LiteralPath $traceGuardFixture -Force
    }

    $prepareCommand = Get-Command -Name $prepareScriptPath
    foreach ($mandatoryParameter in @('TargetWorktree', 'ExpectedTargetBranch', 'ExpectedTargetCommit')) {
        $mandatoryAttributes = @($prepareCommand.Parameters[$mandatoryParameter].Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory })
        Assert-Condition ($mandatoryAttributes.Count -gt 0) "Public preparation parameter $mandatoryParameter is not mandatory."
    }
    $expectedCommitPatterns = @($prepareCommand.Parameters.ExpectedTargetCommit.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidatePatternAttribute] })
    Assert-Condition ($expectedCommitPatterns.Count -eq 1 -and $expectedCommitPatterns[0].RegexPattern -ceq '^([0-9a-fA-F]{40}|unborn)$') 'ExpectedTargetCommit does not require an exact 40-character hexadecimal commit id or the unborn sentinel.'
    $publishCommand = Get-Command -Name Publish-CwbiSnapshotToWorktree
    foreach ($mandatoryParameter in @('ExpectedCurrentBranch', 'ExpectedCurrentCommit', 'EvidenceDirectory')) {
        $mandatoryAttributes = @($publishCommand.Parameters[$mandatoryParameter].Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory })
        Assert-Condition ($mandatoryAttributes.Count -gt 0) "Snapshot publisher parameter $mandatoryParameter is not mandatory."
    }
    $publishCommitPatterns = @($publishCommand.Parameters.ExpectedCurrentCommit.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidatePatternAttribute] })
    Assert-Condition ($publishCommitPatterns.Count -eq 1 -and $publishCommitPatterns[0].RegexPattern -ceq '^([0-9a-fA-F]{40}|unborn)$') 'Snapshot publisher ExpectedCurrentCommit does not require an exact commit id or the unborn sentinel.'

    $sourceRepository = Join-Path $testRoot 'source'
    New-TestRepository -Path $sourceRepository
    New-Item -ItemType Directory -Path (Join-Path $sourceRepository 'src') | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceRepository 'app.txt') -Value 'committed application' -NoNewline
    Set-Content -LiteralPath (Join-Path $sourceRepository 'src\keep.txt') -Value 'keep' -NoNewline
    Set-Content -LiteralPath (Join-Path $sourceRepository 'src\dev.txt') -Value 'development-only' -NoNewline
    Set-Content -LiteralPath (Join-Path $sourceRepository 'AGENTS.md') -Value 'agent-only' -NoNewline
    Set-TestPolicy -Repository $sourceRepository
    $sourceCommit = Add-TestCommit -Repository $sourceRepository -Message 'Add source fixture'

    $snapshotRoot = Join-Path $testRoot 'snapshot'
    $metadata = Export-CwbiReleaseTree `
        -RepositoryRoot $sourceRepository `
        -SourceRevision $sourceCommit `
        -Destination $snapshotRoot `
        -PolicyRepositoryPath 'scripts/cwbi-release/policy.json'

    Assert-Condition ($metadata.SourceCommit -ceq $sourceCommit) 'Export did not retain the exact source commit.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $snapshotRoot 'app.txt') -Raw) -ceq 'committed application') 'Export did not preserve committed content.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $snapshotRoot 'src\keep.txt')) 'An allowlisted file was omitted.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $snapshotRoot 'src\dev.txt'))) 'An explicitly excluded file survived.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $snapshotRoot 'AGENTS.md'))) 'A non-allowlisted AI file survived.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $snapshotRoot 'scripts'))) 'Source-only release tooling survived export.'
    Assert-Condition ($metadata.PolicySha256 -match '^[0-9a-f]{64}$') 'Export did not record a policy digest.'

    $manifest = @(Get-CwbiContentManifest -Root $snapshotRoot)
    Assert-Condition ($manifest.Count -eq 2) 'Manifest did not describe the exact sanitized snapshot.'
    Assert-Condition ((Get-CwbiManifestSha256 -Manifest $manifest) -match '^[0-9a-f]{64}$') 'Manifest digest is invalid.'

    $firstImageTag = New-CwbiRunUniqueImageTag -SourceCommit $sourceCommit
    $secondImageTag = New-CwbiRunUniqueImageTag -SourceCommit $sourceCommit
    Assert-Condition ($firstImageTag -match "^dst-total-risk:cwbi-release-$($sourceCommit.Substring(0, 12))-[0-9a-f]{32}$") 'Run-unique Docker image tag has an unexpected format.'
    Assert-Condition ($firstImageTag -cne $secondImageTag) 'Two release runs for one source commit reused a Docker image tag.'

    # The release path audits the four shipped projects one at a time and concatenates their output.
    $cleanNuGetAudit = @(
        '',
        'The following sources were used:',
        '   https://api.nuget.org/v3/index.json',
        'The given project `RMC.TotalRisk` has no vulnerable packages given the current sources.',
        '',
        'The following sources were used:',
        '   https://api.nuget.org/v3/index.json',
        'The given project `RMC.TotalRisk.Api` has no vulnerable packages given the current sources.',
        'The given project `RMC.TotalRisk.Tests` has no vulnerable packages given the current sources.',
        'The given project `RMC.TotalRisk.Api.Tests` has no vulnerable packages given the current sources.',
        ''
    )
    Assert-CwbiNuGetAuditClean -AuditOutput $cleanNuGetAudit
    Assert-Throws -MessagePattern 'NuGet reported vulnerable packages' -FailureMessage 'NuGet vulnerable-package output was accepted.' -Action {
        Assert-CwbiNuGetAuditClean -AuditOutput @(
            'Project RMC-TotalRisk has the following vulnerable packages',
            'package details intentionally omitted from this test'
        )
    }
    foreach ($invalidAudit in @(
        [pscustomobject]@{ Name = 'empty'; Output = [string[]]@() },
        [pscustomobject]@{ Name = 'warning-only'; Output = @('warning: audit result unavailable') },
        [pscustomobject]@{ Name = 'NU1900'; Output = @('warning NU1900: Error occurred while getting package vulnerability data') },
        [pscustomobject]@{ Name = 'advisory source failure'; Output = @('Unable to load the service index for the package advisory source.') },
        [pscustomobject]@{ Name = 'no-project'; Output = @('The following sources were used:', '   https://api.nuget.org/v3/index.json') },
        [pscustomobject]@{ Name = 'unrecognized project'; Output = @('The given project `SomethingElse` has no vulnerable packages given the current sources.') },
        [pscustomobject]@{ Name = 'missing project'; Output = @(
                'The given project `RMC.TotalRisk` has no vulnerable packages given the current sources.',
                'The given project `RMC.TotalRisk.Api` has no vulnerable packages given the current sources.',
                'The given project `RMC.TotalRisk.Tests` has no vulnerable packages given the current sources.'
            ) },
        [pscustomobject]@{ Name = 'duplicate project'; Output = @(
                'The given project `RMC.TotalRisk` has no vulnerable packages given the current sources.',
                'The given project `RMC.TotalRisk` has no vulnerable packages given the current sources.',
                'The given project `RMC.TotalRisk.Api` has no vulnerable packages given the current sources.',
                'The given project `RMC.TotalRisk.Tests` has no vulnerable packages given the current sources.',
                'The given project `RMC.TotalRisk.Api.Tests` has no vulnerable packages given the current sources.'
            ) }
    )) {
        Assert-Throws -MessagePattern 'NuGet audit' -FailureMessage "NuGet $($invalidAudit.Name) output was accepted." -Action {
            Assert-CwbiNuGetAuditClean -AuditOutput @($invalidAudit.Output)
        }
    }

    $secondSnapshotRoot = Join-Path $testRoot 'snapshot-second-export'
    $secondMetadata = Export-CwbiReleaseTree `
        -RepositoryRoot $sourceRepository `
        -SourceRevision $sourceCommit `
        -Destination $secondSnapshotRoot `
        -PolicyRepositoryPath 'scripts/cwbi-release/policy.json'
    Assert-Condition ((Compare-Object $metadata.Manifest $secondMetadata.Manifest -Property Path, Sha256).Count -eq 0) 'Two exports of one source commit produced different manifests.'
    Assert-Condition ($metadata.ManifestSha256 -ceq $secondMetadata.ManifestSha256) 'Two exports of one source commit produced different manifest digests.'

    $generatedOutputRepository = Join-Path $testRoot 'forced-generated-output'
    New-TestRepository -Path $generatedOutputRepository
    New-Item -ItemType Directory -Path (Join-Path $generatedOutputRepository 'src') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $generatedOutputRepository 'test_scenarios\Case\csharp') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $generatedOutputRepository 'app.txt') -Value 'app' -NoNewline
    Set-Content -LiteralPath (Join-Path $generatedOutputRepository 'src\keep.txt') -Value 'keep' -NoNewline
    Set-Content -LiteralPath (Join-Path $generatedOutputRepository '.gitignore') -Value 'test_scenarios/**/csharp/output.json' -NoNewline
    $forcedOutputPath = Join-Path $generatedOutputRepository 'test_scenarios\Case\csharp\output.json'
    Set-Content -LiteralPath $forcedOutputPath -Value '{"generated":true}' -NoNewline
    Set-TestPolicy `
        -Repository $generatedOutputRepository `
        -AllowedFiles @('app.txt') `
        -AllowedDirectories @('src/', 'test_scenarios/')
    Invoke-Git -Repository $generatedOutputRepository -Arguments @('add', '--force', '--', 'test_scenarios/Case/csharp/output.json') | Out-Null
    $generatedOutputCommit = Add-TestCommit -Repository $generatedOutputRepository -Message 'Force-add generated output fixture'
    Assert-Condition (@((Invoke-Git -Repository $generatedOutputRepository -Arguments @('ls-files', '--', 'test_scenarios/Case/csharp/output.json')).Output).Count -eq 1) 'Generated-output fixture was not force-added.'
    $generatedOutputSnapshot = Join-Path $testRoot 'forced-generated-output-snapshot'
    Export-CwbiReleaseTree `
        -RepositoryRoot $generatedOutputRepository `
        -SourceRevision $generatedOutputCommit `
        -Destination $generatedOutputSnapshot `
        -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $generatedOutputSnapshot 'test_scenarios\Case\csharp\output.json'))) 'A force-added generated output.json was exported.'

    $certificateRepository = Join-Path $testRoot 'binary-certificate-source'
    New-TestRepository -Path $certificateRepository
    New-Item -ItemType Directory -Path (Join-Path $certificateRepository 'src') | Out-Null
    Set-Content -LiteralPath (Join-Path $certificateRepository 'app.txt') -Value 'app' -NoNewline
    Set-Content -LiteralPath (Join-Path $certificateRepository 'src\keep.txt') -Value 'keep' -NoNewline
    [System.IO.File]::WriteAllBytes(
        (Join-Path $certificateRepository 'src\client.PfX'),
        [byte[]](0, 255, 16, 0, 42))
    Set-TestPolicy -Repository $certificateRepository
    $certificateCommit = Add-TestCommit -Repository $certificateRepository -Message 'Add binary certificate candidate'
    Assert-Throws -MessagePattern 'secret-bearing path.*src/client.PfX' -FailureMessage 'An allowlisted binary certificate path was exported.' -Action {
        Export-CwbiReleaseTree `
            -RepositoryRoot $certificateRepository `
            -SourceRevision $certificateCommit `
            -Destination (Join-Path $testRoot 'binary-certificate-snapshot') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }

    $invalidLiteralPolicyRepository = Join-Path $testRoot 'invalid-secret-path-policy'
    New-TestRepository -Path $invalidLiteralPolicyRepository
    Set-TestPolicy -Repository $invalidLiteralPolicyRepository
    $invalidLiteralPolicyPath = Join-Path $invalidLiteralPolicyRepository 'scripts\cwbi-release\policy.json'
    $invalidLiteralPolicy = Get-Content -Raw -LiteralPath $invalidLiteralPolicyPath | ConvertFrom-Json
    $invalidLiteralPolicy.secretBearingFileExtensions = @(
        '.cer', '.crt', '.der', '.jks', '.key', '.keystore', '.p12', '.pem', '.pfx', '.*'
    )
    $invalidLiteralPolicy | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $invalidLiteralPolicyPath
    $invalidLiteralPolicySha256 = (Get-FileHash -LiteralPath $invalidLiteralPolicyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Throws -MessagePattern 'invalid literal' -FailureMessage 'A regex-like user-controlled secret path pattern was accepted.' -Action {
        Get-CwbiReleasePolicy `
            -PolicyPath $invalidLiteralPolicyPath `
            -ExpectedPolicySha256 $invalidLiteralPolicySha256 | Out-Null
    }

    Invoke-Git -Repository $sourceRepository -Arguments @('tag', '-a', 'annotated-source', '-m', 'Annotated source fixture', $sourceCommit) | Out-Null
    $annotatedTagObject = ([string](Invoke-Git -Repository $sourceRepository -Arguments @('rev-parse', 'annotated-source^{tag}')).Output[0]).Trim()
    Assert-Throws -MessagePattern 'commit object' -FailureMessage 'An annotated-tag object SHA was accepted as SourceRevision.' -Action {
        Export-CwbiReleaseTree `
            -RepositoryRoot $sourceRepository `
            -SourceRevision $annotatedTagObject `
            -Destination (Join-Path $testRoot 'annotated-tag') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }

    Invoke-Git -Repository $sourceRepository -Arguments @('commit', '--allow-empty', '-m', 'Advance approved v2.0-development') | Out-Null
    $mainTipCommit = ([string](Invoke-Git -Repository $sourceRepository -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
    Assert-Throws -MessagePattern 'HEAD' -FailureMessage 'A source revision different from checkout HEAD was accepted.' -Action {
        Export-CwbiReleaseTree `
            -RepositoryRoot $sourceRepository `
            -SourceRevision $sourceCommit `
            -Destination (Join-Path $testRoot 'head-mismatch') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    Invoke-Git -Repository $sourceRepository -Arguments @('switch', '--detach', $sourceCommit) | Out-Null
    $ancestorMetadata = Export-CwbiReleaseTree `
        -RepositoryRoot $sourceRepository `
        -SourceRevision $sourceCommit `
        -Destination (Join-Path $testRoot 'approved-ancestor') `
        -PolicyRepositoryPath 'scripts/cwbi-release/policy.json'
    Assert-Condition ($ancestorMetadata.SourceCommit -ceq $sourceCommit) 'An approved older v2.0-development ancestor could not be exported at exact HEAD.'
    Invoke-Git -Repository $sourceRepository -Arguments @('switch', 'v2.0-development') | Out-Null

    $danglingTree = ([string](Invoke-Git -Repository $sourceRepository -Arguments @('rev-parse', "$mainTipCommit^{tree}")).Output[0]).Trim()
    $danglingCommit = ([string](Invoke-Git -Repository $sourceRepository -Arguments @('commit-tree', $danglingTree, '-m', 'Dangling source fixture')).Output[0]).Trim()
    Invoke-Git -Repository $sourceRepository -Arguments @('switch', '--detach', $danglingCommit) | Out-Null
    Assert-Throws -MessagePattern 'approved v2.0-development' -FailureMessage 'A dangling source commit was accepted.' -Action {
        Export-CwbiReleaseTree `
            -RepositoryRoot $sourceRepository `
            -SourceRevision $danglingCommit `
            -Destination (Join-Path $testRoot 'dangling-source') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    Invoke-Git -Repository $sourceRepository -Arguments @('switch', 'v2.0-development') | Out-Null

    Set-Content -LiteralPath (Join-Path $sourceRepository 'app.txt') -Value 'uncommitted mutation' -NoNewline
    Assert-Throws -MessagePattern 'not clean' -FailureMessage 'A dirty source checkout was accepted.' -Action {
        Export-CwbiReleaseTree `
            -RepositoryRoot $sourceRepository `
            -SourceRevision $mainTipCommit `
            -Destination (Join-Path $testRoot 'dirty-source') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    Invoke-Git -Repository $sourceRepository -Arguments @('restore', '--', 'app.txt') | Out-Null

    $rawPathFixtures = [ordered]@{
        'control character' = "src/control$([char]1).txt"
        'backslash' = 'src/back\slash.txt'
        'colon' = 'src/alternate:stream.txt'
        'trailing dot' = 'src/trailing-dot.'
        'trailing space' = 'src/trailing-space '
        'Windows device name' = 'src/CON.txt'
        'non-NFC Unicode' = "src/cafe$([char]0x0301).txt"
    }
    $rawFixtureIndex = 0
    foreach ($rawFixture in $rawPathFixtures.GetEnumerator()) {
        $rawRepository = Join-Path $testRoot "raw-path-$rawFixtureIndex"
        $rawCommit = New-RawPathTestRepository -Path $rawRepository -RawPath ([string]$rawFixture.Value)
        Assert-Throws -MessagePattern 'non-portable' -FailureMessage "A source path containing $($rawFixture.Key) was accepted." -Action {
            Export-CwbiReleaseTree `
                -RepositoryRoot $rawRepository `
                -SourceRevision $rawCommit `
                -Destination (Join-Path $testRoot "raw-path-snapshot-$rawFixtureIndex") `
                -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
        }
        $rawFixtureIndex++
    }

    $rawDiagnosticRepository = Join-Path $testRoot 'raw-path-diagnostic'
    $rawDiagnosticPath = "src/line`nbreak$([char]27).txt"
    $rawDiagnosticCommit = New-RawPathTestRepository -Path $rawDiagnosticRepository -RawPath $rawDiagnosticPath
    $rawDiagnosticFailed = $false
    $rawDiagnosticMessage = ''
    try {
        Export-CwbiReleaseTree `
            -RepositoryRoot $rawDiagnosticRepository `
            -SourceRevision $rawDiagnosticCommit `
            -Destination (Join-Path $testRoot 'raw-path-diagnostic-snapshot') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    catch {
        $rawDiagnosticFailed = $true
        $rawDiagnosticMessage = $_.Exception.Message
    }
    Assert-Condition $rawDiagnosticFailed 'A source path with diagnostic control bytes was accepted.'
    Assert-Condition (-not $rawDiagnosticMessage.Contains("`n", [StringComparison]::Ordinal)) 'An unsafe source-path diagnostic emitted a raw newline.'
    Assert-Condition (-not $rawDiagnosticMessage.Contains([string][char]27, [StringComparison]::Ordinal)) 'An unsafe source-path diagnostic emitted a raw ESC character.'
    Assert-Condition ($rawDiagnosticMessage.Contains('\n', [StringComparison]::Ordinal) -and $rawDiagnosticMessage.Contains('\u001B', [StringComparison]::OrdinalIgnoreCase)) 'Unsafe source-path controls were not visibly escaped in the diagnostic.'

    $abbreviatedFailed = $false
    try {
        Export-CwbiReleaseTree `
            -RepositoryRoot $sourceRepository `
            -SourceRevision $sourceCommit.Substring(0, 12) `
            -Destination (Join-Path $testRoot 'abbreviated') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    catch {
        $abbreviatedFailed = $_.Exception.Message -match '40-character'
    }
    Assert-Condition $abbreviatedFailed 'An abbreviated source revision was accepted.'

    $missingPolicyFailed = $false
    try {
        Export-CwbiReleaseTree `
            -RepositoryRoot $sourceRepository `
            -SourceRevision $mainTipCommit `
            -Destination (Join-Path $testRoot 'missing-policy') `
            -PolicyRepositoryPath 'scripts/cwbi-release/missing-policy.json' | Out-Null
    }
    catch {
        $missingPolicyFailed = $_.Exception.Message -match 'does not contain the release policy'
    }
    Assert-Condition $missingPolicyFailed 'A source commit without the requested pinned policy was accepted.'

    $syntheticSecret = 'AKIA' + '1234567890ABCDEF'
    Set-Content -LiteralPath (Join-Path $snapshotRoot 'secret.txt') -Value $syntheticSecret -NoNewline
    $secretFailed = $false
    $secretMessage = ''
    try {
        Assert-CwbiSecretsAbsent -Root $snapshotRoot -PolicyPath $metadata.PolicyPath -ExpectedPolicySha256 $metadata.PolicySha256
    }
    catch {
        $secretFailed = $true
        $secretMessage = $_.Exception.Message
    }
    Assert-Condition $secretFailed 'A high-signal secret was accepted.'
    Assert-Condition ($secretMessage -match 'secret.txt') 'Secret scan did not identify the affected path.'
    Assert-Condition (-not $secretMessage.Contains($syntheticSecret, [StringComparison]::Ordinal)) 'Secret scan exposed the matched value.'
    Remove-Item -LiteralPath (Join-Path $snapshotRoot 'secret.txt')

    $arbitraryBinaryPath = Join-Path $snapshotRoot 'src\arbitrary-data.bin'
    [System.IO.File]::WriteAllBytes($arbitraryBinaryPath, [byte[]](0, 255, 16, 0, 42))
    Assert-CwbiReleasePathPolicy `
        -Root $snapshotRoot `
        -PolicyPath $metadata.PolicyPath `
        -ExpectedPolicySha256 $metadata.PolicySha256
    Assert-CwbiSecretsAbsent `
        -Root $snapshotRoot `
        -PolicyPath $metadata.PolicyPath `
        -ExpectedPolicySha256 $metadata.PolicySha256
    Remove-Item -LiteralPath $arbitraryBinaryPath -Force

    $binarySnapshotPath = Join-Path $snapshotRoot 'src\snapshot-client.P12'
    [System.IO.File]::WriteAllBytes($binarySnapshotPath, [byte[]](0, 128, 255, 0, 7))
    Assert-Throws -MessagePattern 'secret-bearing path.*src/snapshot-client.P12' -FailureMessage 'The current snapshot scanner accepted a binary keystore path.' -Action {
        Assert-CwbiSecretsAbsent `
            -Root $snapshotRoot `
            -PolicyPath $metadata.PolicyPath `
            -ExpectedPolicySha256 $metadata.PolicySha256
    }
    Remove-Item -LiteralPath $binarySnapshotPath -Force

    $packageCredentialsElement = 'packageSource' + 'Credentials'
    $clearCredentialKey = 'ClearText' + 'Password'
    $passwordKey = 'Pass' + 'word'
    $nugetSecretValue = 'never-print-this-nuget-password'
    $nugetConfigPath = Join-Path $snapshotRoot 'src\NuGet.Config'
    $nugetConfigContent = "<configuration><$packageCredentialsElement><private><add key=`"$clearCredentialKey`" value=`"$nugetSecretValue`" /></private></$packageCredentialsElement></configuration>"
    Set-Content -LiteralPath $nugetConfigPath -Value $nugetConfigContent -NoNewline
    $nugetSecretFailed = $false
    $nugetSecretMessage = ''
    try {
        Assert-CwbiSecretsAbsent -Root $snapshotRoot -PolicyPath $metadata.PolicyPath -ExpectedPolicySha256 $metadata.PolicySha256
    }
    catch {
        $nugetSecretFailed = $true
        $nugetSecretMessage = $_.Exception.Message
    }
    Assert-Condition $nugetSecretFailed 'NuGet package-source credentials were accepted.'
    Assert-Condition ($nugetSecretMessage -match 'nuget-[^:]+:src/NuGet.Config') 'NuGet secret scan did not report rule:path.'
    Assert-Condition (-not $nugetSecretMessage.Contains($nugetSecretValue, [StringComparison]::Ordinal)) 'NuGet secret scan exposed the matched value.'
    Remove-Item -LiteralPath $nugetConfigPath

    Set-Content -LiteralPath $nugetConfigPath -Value "<configuration><add value=`"$nugetSecretValue`" key=`"$passwordKey`" /></configuration>" -NoNewline
    $reorderedNugetFailed = $false
    $reorderedNugetMessage = ''
    try {
        Assert-CwbiSecretsAbsent -Root $snapshotRoot -PolicyPath $metadata.PolicyPath -ExpectedPolicySha256 $metadata.PolicySha256
    }
    catch {
        $reorderedNugetFailed = $true
        $reorderedNugetMessage = $_.Exception.Message
    }
    Assert-Condition $reorderedNugetFailed 'A NuGet Password entry with reordered XML attributes was accepted.'
    Assert-Condition ($reorderedNugetMessage -match 'nuget-password-value:src/NuGet.Config') 'Reordered NuGet Password detection did not report rule:path.'
    Assert-Condition (-not $reorderedNugetMessage.Contains($nugetSecretValue, [StringComparison]::Ordinal)) 'Reordered NuGet Password detection exposed the matched value.'
    Remove-Item -LiteralPath $nugetConfigPath

    $historyRepository = Join-Path $testRoot 'history-secret'
    New-TestRepository -Path $historyRepository
    Set-Content -LiteralPath (Join-Path $historyRepository 'history-only.txt') -Value $syntheticSecret -NoNewline
    Add-TestCommit -Repository $historyRepository -Message 'Add historical secret fixture' | Out-Null
    Remove-Item -LiteralPath (Join-Path $historyRepository 'history-only.txt')
    $cleanHistoryCommit = Add-TestCommit -Repository $historyRepository -Message 'Remove historical secret fixture'
    $historyFailed = $false
    $historyMessage = ''
    try {
        Assert-CwbiGitHistorySecretsAbsent -RepositoryRoot $historyRepository -SourceRevision $cleanHistoryCommit
    }
    catch {
        $historyFailed = $true
        $historyMessage = $_.Exception.Message
    }
    Assert-Condition $historyFailed 'A secret retained only in reachable history was accepted.'
    Assert-Condition ($historyMessage -match 'history-only.txt') 'History scan did not identify the affected path.'
    Assert-Condition (-not $historyMessage.Contains($syntheticSecret, [StringComparison]::Ordinal)) 'History scan exposed the matched value.'

    $nugetHistoryRepository = Join-Path $testRoot 'history-nuget-secret'
    New-TestRepository -Path $nugetHistoryRepository
    $nugetHistoryContent = "<$packageCredentialsElement><private><add key=`"$passwordKey`" value=`"$nugetSecretValue`" /></private></$packageCredentialsElement>"
    Set-Content -LiteralPath (Join-Path $nugetHistoryRepository 'NuGet.Config') -Value $nugetHistoryContent -NoNewline
    Add-TestCommit -Repository $nugetHistoryRepository -Message 'Add historical NuGet secret fixture' | Out-Null
    Remove-Item -LiteralPath (Join-Path $nugetHistoryRepository 'NuGet.Config')
    $cleanNugetHistoryCommit = Add-TestCommit -Repository $nugetHistoryRepository -Message 'Remove historical NuGet secret fixture'
    $nugetHistoryFailed = $false
    $nugetHistoryMessage = ''
    try {
        Assert-CwbiGitHistorySecretsAbsent -RepositoryRoot $nugetHistoryRepository -SourceRevision $cleanNugetHistoryCommit
    }
    catch {
        $nugetHistoryFailed = $true
        $nugetHistoryMessage = $_.Exception.Message
    }
    Assert-Condition $nugetHistoryFailed 'NuGet credentials retained only in reachable history were accepted.'
    Assert-Condition ($nugetHistoryMessage -match 'nuget-[^:]+:NuGet.Config@[0-9a-f]{12}') 'NuGet history scan did not report rule:path@commit.'
    Assert-Condition (-not $nugetHistoryMessage.Contains($nugetSecretValue, [StringComparison]::Ordinal)) 'NuGet history scan exposed the matched value.'

    $credentialHistoryRepository = Join-Path $testRoot 'history-credential-path'
    New-TestRepository -Path $credentialHistoryRepository
    New-Item -ItemType Directory -Path (Join-Path $credentialHistoryRepository 'src') | Out-Null
    $historyKeyPayload = 'NEVER-LOG-HISTORY-PRIVATE-KEY'
    $historyKeyBytes = [System.Collections.Generic.List[byte]]::new()
    $historyKeyBytes.Add(0)
    $historyKeyBytes.AddRange([System.Text.Encoding]::UTF8.GetBytes($historyKeyPayload))
    [System.IO.File]::WriteAllBytes(
        (Join-Path $credentialHistoryRepository 'src\id_ed25519'),
        $historyKeyBytes.ToArray())
    Add-TestCommit -Repository $credentialHistoryRepository -Message 'Add historical binary private key path' | Out-Null
    Remove-Item -LiteralPath (Join-Path $credentialHistoryRepository 'src\id_ed25519') -Force
    $cleanCredentialHistoryCommit = Add-TestCommit -Repository $credentialHistoryRepository -Message 'Remove historical binary private key path'
    $credentialHistoryMessage = ''
    try {
        Assert-CwbiGitHistorySecretsAbsent `
            -RepositoryRoot $credentialHistoryRepository `
            -SourceRevision $cleanCredentialHistoryCommit
    }
    catch {
        $credentialHistoryMessage = $_.Exception.Message
    }
    Assert-Condition ($credentialHistoryMessage -match 'secret-bearing path.*src/id_ed25519') 'A conventional private-key path in reachable history was accepted.'
    Assert-Condition (-not $credentialHistoryMessage.Contains($historyKeyPayload, [StringComparison]::Ordinal)) 'History path rejection exposed binary credential contents.'

    $scannerSelfTestRepository = Join-Path $testRoot 'scanner-self-test'
    New-TestRepository -Path $scannerSelfTestRepository
    Copy-Item -LiteralPath $modulePath -Destination (Join-Path $scannerSelfTestRepository 'CwbiRelease.psm1')
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $scannerSelfTestRepository 'Prepare-CwbiRelease.Tests.ps1')
    $scannerSelfTestCommit = Add-TestCommit -Repository $scannerSelfTestRepository -Message 'Add release scanner source fixtures'
    Assert-CwbiGitHistorySecretsAbsent -RepositoryRoot $scannerSelfTestRepository -SourceRevision $scannerSelfTestCommit

    $unsafeRepository = Join-Path $testRoot 'unsafe-policy'
    New-TestRepository -Path $unsafeRepository
    New-Item -ItemType Directory -Path (Join-Path $unsafeRepository 'src') | Out-Null
    Set-Content -LiteralPath (Join-Path $unsafeRepository 'app.txt') -Value 'app' -NoNewline
    Set-Content -LiteralPath (Join-Path $unsafeRepository 'src\keep.txt') -Value 'keep' -NoNewline
    Set-TestPolicy -Repository $unsafeRepository -AllowedFiles @('../escape.txt')
    $unsafeCommit = Add-TestCommit -Repository $unsafeRepository -Message 'Add unsafe policy fixture'
    $unsafeFailed = $false
    try {
        Export-CwbiReleaseTree `
            -RepositoryRoot $unsafeRepository `
            -SourceRevision $unsafeCommit `
            -Destination (Join-Path $testRoot 'unsafe-snapshot') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    catch {
        $unsafeFailed = $_.Exception.Message -match 'unsafe path'
    }
    Assert-Condition $unsafeFailed 'A policy path traversal was accepted.'

    $collisionRepository = Join-Path $testRoot 'case-collision'
    New-TestRepository -Path $collisionRepository
    New-Item -ItemType Directory -Path (Join-Path $collisionRepository 'src') | Out-Null
    Set-Content -LiteralPath (Join-Path $collisionRepository 'app.txt') -Value 'app' -NoNewline
    Set-Content -LiteralPath (Join-Path $collisionRepository 'src\keep.txt') -Value 'keep' -NoNewline
    Set-TestPolicy -Repository $collisionRepository
    Add-TestCommit -Repository $collisionRepository -Message 'Add collision base' | Out-Null
    Set-Content -LiteralPath (Join-Path $collisionRepository 'alternate.txt') -Value 'alternate' -NoNewline
    $alternateBlob = ([string](Invoke-Git -Repository $collisionRepository -Arguments @('hash-object', '-w', 'alternate.txt')).Output[0]).Trim()
    Invoke-Git -Repository $collisionRepository -Arguments @('update-index', '--add', '--cacheinfo', '100644', $alternateBlob, 'src/KEEP.txt') | Out-Null
    Invoke-Git -Repository $collisionRepository -Arguments @('commit', '-m', 'Add case collision') | Out-Null
    $collisionCommit = ([string](Invoke-Git -Repository $collisionRepository -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
    $collisionFailed = $false
    try {
        Export-CwbiReleaseTree `
            -RepositoryRoot $collisionRepository `
            -SourceRevision $collisionCommit `
            -Destination (Join-Path $testRoot 'collision-snapshot') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    catch {
        $collisionFailed = $_.Exception.Message -match 'case-colliding'
    }
    Assert-Condition $collisionFailed 'Case-colliding source paths were accepted.'

    $symlinkRepository = Join-Path $testRoot 'symlink-mode'
    New-TestRepository -Path $symlinkRepository
    New-Item -ItemType Directory -Path (Join-Path $symlinkRepository 'src') | Out-Null
    Set-Content -LiteralPath (Join-Path $symlinkRepository 'app.txt') -Value 'app' -NoNewline
    Set-Content -LiteralPath (Join-Path $symlinkRepository 'src\keep.txt') -Value 'keep' -NoNewline
    Set-TestPolicy -Repository $symlinkRepository
    Add-TestCommit -Repository $symlinkRepository -Message 'Add symlink base' | Out-Null
    Set-Content -LiteralPath (Join-Path $symlinkRepository 'symlink-target.txt') -Value 'src/keep.txt' -NoNewline
    $symlinkBlob = ([string](Invoke-Git -Repository $symlinkRepository -Arguments @('hash-object', '-w', 'symlink-target.txt')).Output[0]).Trim()
    Invoke-Git -Repository $symlinkRepository -Arguments @('update-index', '--add', '--cacheinfo', '120000', $symlinkBlob, 'src/link') | Out-Null
    Invoke-Git -Repository $symlinkRepository -Arguments @('commit', '-m', 'Add symlink mode') | Out-Null
    $symlinkCommit = ([string](Invoke-Git -Repository $symlinkRepository -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
    $symlinkFailed = $false
    try {
        Export-CwbiReleaseTree `
            -RepositoryRoot $symlinkRepository `
            -SourceRevision $symlinkCommit `
            -Destination (Join-Path $testRoot 'symlink-snapshot') `
            -PolicyRepositoryPath 'scripts/cwbi-release/policy.json' | Out-Null
    }
    catch {
        $symlinkFailed = $_.Exception.Message -match 'symlinks'
    }
    Assert-Condition $symlinkFailed 'A source tree containing a Git symlink was accepted.'

    $commonDirectory = ([string](Invoke-Git -Repository $sourceRepository -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output[0]).Trim()

    $wrongCommonRepository = Join-Path $testRoot 'wrong-common-source'
    New-TestRepository -Path $wrongCommonRepository
    Set-Content -LiteralPath (Join-Path $wrongCommonRepository 'legacy.txt') -Value 'wrong common' -NoNewline
    $wrongCommonCommit = Add-TestCommit -Repository $wrongCommonRepository -Message 'Add wrong-common target'
    Set-TestTargetSafety -Repository $wrongCommonRepository
    Invoke-Git -Repository $wrongCommonRepository -Arguments @('branch', 'bootstrap-target') | Out-Null
    $wrongCommonWorktree = Join-Path $testRoot 'wrong-common-worktree'
    Invoke-Git -Repository $wrongCommonRepository -Arguments @('worktree', 'add', $wrongCommonWorktree, 'bootstrap-target') | Out-Null

    $targetRepository = $sourceRepository
    Set-TestTargetSafety -Repository $targetRepository -CwbiUrl 'https://invalid.example/dst-total-risk.git'
    Invoke-Git -Repository $targetRepository -Arguments @('branch', 'bootstrap-target', $mainTipCommit) | Out-Null
    $targetWorktree = Join-Path $testRoot 'target-worktree'
    $evidenceDirectory = Join-Path $testRoot 'release-evidence'
    Invoke-Git -Repository $targetRepository -Arguments @('worktree', 'add', $targetWorktree, 'bootstrap-target') | Out-Null
    Set-Content -LiteralPath (Join-Path $targetWorktree 'legacy.txt') -Value 'legacy' -NoNewline
    $targetBootstrapCommit = Add-TestCommit -Repository $targetWorktree -Message 'Add legacy target'
    $targetCommonDirectory = ([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output[0]).Trim()
    $wrongCommonFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $wrongCommonWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $wrongCommonCommit `
            -ExpectedCommonGitDirectory $commonDirectory | Out-Null
    }
    catch {
        $wrongCommonFailed = $_.Exception.Message -match 'unexpected Git directory'
    }
    Assert-Condition $wrongCommonFailed 'A target from the wrong Git common directory was accepted.'

    Assert-Throws -MessagePattern 'cwbi.*URL|remote.*cwbi' -FailureMessage 'An unexpected CWBI remote URL was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $targetBootstrapCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }

    Set-TestTargetSafety -Repository $targetRepository -CwbiUrl 'https://github.com/cwbi-apps/%64st-total-risk.git'
    Assert-Throws -MessagePattern 'non-canonical|cwbi.*URL' -FailureMessage 'A percent-encoded CWBI remote URL was accepted as canonical.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $targetBootstrapCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Set-TestTargetSafety -Repository $targetRepository

    Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', 'url.https://invalid.example/.insteadOf', 'https://github.com/cwbi-apps/') | Out-Null
    Assert-Throws -MessagePattern 'cwbi.*URL|remote.*cwbi' -FailureMessage 'A Git URL rewrite that changes the effective CWBI destination was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $targetBootstrapCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', '--unset-all', 'url.https://invalid.example/.insteadOf') | Out-Null

    Assert-Throws -MessagePattern 'evidence.*outside' -FailureMessage 'A case-variant deployment-worktree path was accepted as the evidence directory.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $targetWorktree.ToUpperInvariant() `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $targetBootstrapCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }

    $preexistingJunctionEvidence = Join-Path $testRoot 'junction-evidence-preexisting'
    New-Item -ItemType Junction -Path $preexistingJunctionEvidence -Target $targetWorktree | Out-Null
    try {
        Assert-Throws -MessagePattern 'reparse|junction|symbolic' -FailureMessage 'An external-looking evidence junction into the target worktree was accepted.' -Action {
            Publish-CwbiSnapshotToWorktree `
                -TargetWorktree $targetWorktree `
                -SnapshotRoot $snapshotRoot `
                -BranchName 'cwbi-dev' `
                -SourceCommit $sourceCommit `
                -SourceTree $metadata.SourceTree `
                -PolicySha256 $metadata.PolicySha256 `
                -PolicyPath $metadata.PolicyPath `
                -EvidenceDirectory $preexistingJunctionEvidence `
                -ManifestSha256 $metadata.ManifestSha256 `
                -ExpectedCurrentBranch 'bootstrap-target' `
                -ExpectedCurrentCommit $targetBootstrapCommit `
                -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
        }
        Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $targetBootstrapCommit) 'Preexisting evidence-junction rejection changed the target ref.'
        Assert-Condition (@(Get-ChildItem -LiteralPath $targetWorktree -Filter 'cwbi-release-*.json' -File -Force).Count -eq 0) 'Preexisting evidence-junction rejection left evidence inside the target.'
        Assert-Condition (@((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output).Count -eq 0) 'Preexisting evidence-junction rejection left the target dirty.'
    }
    finally {
        Remove-TestJunction -Path $preexistingJunctionEvidence
    }

    $explicitPushUrlFixtures = @(
        [pscustomobject]@{ Remote = 'origin'; Url = 'https://github.com/USACE-RMC/RMC-TotalRisk.git'; Kind = 'same-destination' },
        [pscustomobject]@{ Remote = 'origin'; Url = 'https://invalid.example/RMC-TotalRisk.git'; Kind = 'wrong-destination' },
        [pscustomobject]@{ Remote = 'cwbi'; Url = 'https://github.com/cwbi-apps/dst-total-risk.git'; Kind = 'same-destination' },
        [pscustomobject]@{ Remote = 'cwbi'; Url = 'https://invalid.example/dst-total-risk.git'; Kind = 'wrong-destination' }
    )
    foreach ($pushUrlFixture in $explicitPushUrlFixtures) {
        $pushUrlKey = "remote.$($pushUrlFixture.Remote).pushurl"
        Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', $pushUrlKey, $pushUrlFixture.Url) | Out-Null
        try {
            Assert-Throws -MessagePattern 'explicit.*push.*URL|pushurl' -FailureMessage "An explicit $($pushUrlFixture.Kind) push URL for $($pushUrlFixture.Remote) was accepted." -Action {
                Publish-CwbiSnapshotToWorktree `
                    -TargetWorktree $targetWorktree `
                    -SnapshotRoot $snapshotRoot `
                    -BranchName 'cwbi-dev' `
                    -SourceCommit $sourceCommit `
                    -SourceTree $metadata.SourceTree `
                    -PolicySha256 $metadata.PolicySha256 `
                    -PolicyPath $metadata.PolicyPath `
                    -EvidenceDirectory $evidenceDirectory `
                    -ManifestSha256 $metadata.ManifestSha256 `
                    -ExpectedCurrentBranch 'bootstrap-target' `
                    -ExpectedCurrentCommit $targetBootstrapCommit `
                    -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
            }
        }
        finally {
            Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', '--unset-all', $pushUrlKey) -AllowFailure | Out-Null
        }
    }

    Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', 'push.default', 'simple') | Out-Null
    Assert-Throws -MessagePattern 'push.default' -FailureMessage 'An unsafe repository-local push.default was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $targetBootstrapCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', 'push.default', 'nothing') | Out-Null

    Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', 'remote.pushDefault', 'cwbi') | Out-Null
    Assert-Throws -MessagePattern 'remote.pushDefault' -FailureMessage 'An unsafe remote.pushDefault was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $targetBootstrapCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', '--unset-all', 'remote.pushDefault') | Out-Null

    $branchMetadataFixtures = [ordered]@{
        remote = 'cwbi'
        merge = 'refs/heads/cwbi-dev'
        pushRemote = 'cwbi'
    }
    foreach ($branchMetadata in $branchMetadataFixtures.GetEnumerator()) {
        $branchKey = [string]$branchMetadata.Key
        Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', "branch.cwbi-dev.$branchKey", [string]$branchMetadata.Value) | Out-Null
        Assert-Throws -MessagePattern "branch\.cwbi-dev\.$([regex]::Escape($branchKey))" -FailureMessage "Stale cwbi-dev $branchKey metadata was accepted." -Action {
            Publish-CwbiSnapshotToWorktree `
                -TargetWorktree $targetWorktree `
                -SnapshotRoot $snapshotRoot `
                -BranchName 'cwbi-dev' `
                -SourceCommit $sourceCommit `
                -SourceTree $metadata.SourceTree `
                -PolicySha256 $metadata.PolicySha256 `
                -PolicyPath $metadata.PolicyPath `
                -EvidenceDirectory $evidenceDirectory `
                -ManifestSha256 $metadata.ManifestSha256 `
                -ExpectedCurrentBranch 'bootstrap-target' `
                -ExpectedCurrentCommit $targetBootstrapCommit `
                -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
        }
        Invoke-Git -Repository $targetRepository -Arguments @('config', '--local', '--unset-all', "branch.cwbi-dev.$branchKey") | Out-Null
    }

    $pollutedRepository = Join-Path $testRoot 'polluted-target-source'
    New-TestRepository -Path $pollutedRepository
    Set-Content -LiteralPath (Join-Path $pollutedRepository 'unrelated.txt') -Value 'unrelated history' -NoNewline
    $pollutedCommit = Add-TestCommit -Repository $pollutedRepository -Message 'Unrelated RMC-TotalRisk history'
    Set-TestTargetSafety -Repository $pollutedRepository
    Invoke-Git -Repository $pollutedRepository -Arguments @('branch', 'cwbi-dev') | Out-Null
    $pollutedWorktree = Join-Path $testRoot 'polluted-target-worktree'
    Invoke-Git -Repository $pollutedRepository -Arguments @('worktree', 'add', $pollutedWorktree, 'cwbi-dev') | Out-Null
    $pollutedCommonDirectory = ([string](Invoke-Git -Repository $pollutedWorktree -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output[0]).Trim()
    Assert-Throws -MessagePattern 'generated snapshot history|CWBI snapshot history' -FailureMessage 'A clean cwbi-dev branch rooted in unrelated history was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $pollutedWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $pollutedCommit `
            -ExpectedCommonGitDirectory $pollutedCommonDirectory | Out-Null
    }

    # An unborn target: a linked worktree created with `git worktree add --orphan`, which is how
    # the very first snapshot is published when the destination has no cwbi-dev branch yet.
    # The worktree belongs to the source repository, exactly as the publisher creates it, so the
    # snapshot-history check can resolve the source commit.
    $unbornWorktree = Join-Path $testRoot 'unborn-worktree'
    Invoke-Git -Repository $sourceRepository -Arguments @('worktree', 'add', '--orphan', '-b', 'snapshot/sanitized', $unbornWorktree) | Out-Null
    $unbornCommon = ([string](Invoke-Git -Repository $unbornWorktree -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output[0]).Trim()
    Assert-Condition ((Invoke-Git -Repository $unbornWorktree -Arguments @('rev-parse', '--verify', '--quiet', 'HEAD') -AllowFailure).ExitCode -ne 0) 'Unborn fixture unexpectedly has a HEAD commit.'

    Assert-Throws -MessagePattern 'Target commit changed' -FailureMessage 'An unborn target was accepted against a concrete expected commit.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $unbornWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'snapshot/sanitized' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'snapshot/sanitized' `
            -ExpectedCurrentCommit $sourceCommit `
            -ExpectedCommonGitDirectory $unbornCommon | Out-Null
    }

    $releaseModuleForUnborn = Get-Module CwbiRelease
    & $releaseModuleForUnborn { $script:CwbiTestFaultPoint = 'after-commit' }
    $unbornRollbackFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $unbornWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'snapshot/sanitized' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'snapshot/sanitized' `
            -ExpectedCurrentCommit 'unborn' `
            -ExpectedCommonGitDirectory $unbornCommon | Out-Null
    }
    catch {
        $unbornRollbackFailed = $_.Exception.Message -match 'Injected CWBI test failure after-commit'
    }
    finally {
        & $releaseModuleForUnborn { $script:CwbiTestFaultPoint = $null }
    }
    Assert-Condition $unbornRollbackFailed 'A deterministic failure after the first unborn commit was not observed.'
    Assert-Condition ((Invoke-Git -Repository $unbornWorktree -Arguments @('rev-parse', '--verify', '--quiet', 'HEAD') -AllowFailure).ExitCode -ne 0) 'Unborn rollback left a commit behind.'
    Assert-Condition (([string](Invoke-Git -Repository $unbornWorktree -Arguments @('branch', '--show-current')).Output[0]).Trim() -ceq 'snapshot/sanitized') 'Unborn rollback did not keep the orphan branch checked out.'
    Assert-Condition (@(Get-ChildItem -LiteralPath $unbornWorktree -Force | Where-Object { $_.Name -cne '.git' }).Count -eq 0) 'Unborn rollback did not empty the worktree.'
    Assert-Condition ((Invoke-Git -Repository $unbornWorktree -Arguments @('show-ref', '--verify', '--quiet', 'refs/heads/snapshot/sanitized') -AllowFailure).ExitCode -ne 0) 'Unborn rollback left the snapshot/sanitized ref installed.'

    $unbornResult = Publish-CwbiSnapshotToWorktree `
        -TargetWorktree $unbornWorktree `
        -SnapshotRoot $snapshotRoot `
        -BranchName 'snapshot/sanitized' `
        -SourceCommit $sourceCommit `
        -SourceTree $metadata.SourceTree `
        -PolicySha256 $metadata.PolicySha256 `
        -PolicyPath $metadata.PolicyPath `
        -EvidenceDirectory $evidenceDirectory `
        -ManifestSha256 $metadata.ManifestSha256 `
        -ExpectedCurrentBranch 'snapshot/sanitized' `
        -ExpectedCurrentCommit 'unborn' `
        -ExpectedCommonGitDirectory $unbornCommon
    Assert-Condition ($unbornResult.CreatedRootCommit -eq $true) 'The first unborn publication did not report a root commit.'
    Assert-Condition ($unbornResult.Changed -eq $true) 'The first unborn publication did not report a change.'
    $unbornParents = ([string](Invoke-Git -Repository $unbornWorktree -Arguments @('rev-list', '--parents', '-n', '1', 'HEAD')).Output[0]).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
    Assert-Condition ($unbornParents.Count -eq 1 -and $unbornParents[0] -ceq $unbornResult.Commit) 'The first unborn publication is not a parentless root commit.'
    Assert-Condition (([string](Invoke-Git -Repository $unbornWorktree -Arguments @('branch', '--show-current')).Output[0]).Trim() -ceq 'snapshot/sanitized') 'The first unborn publication left the wrong branch checked out.'
    Assert-Condition ((Compare-Object $metadata.Manifest (Get-CwbiContentManifest -Root $unbornWorktree) -Property Path, Sha256).Count -eq 0) 'The first unborn publication content does not match the snapshot.'
    Assert-Condition (Test-Path -LiteralPath $unbornResult.EvidencePath -PathType Leaf) 'The first unborn publication did not write evidence.'
    Assert-Condition ($unbornResult.Branch -ceq 'cwbi-dev' -and $unbornResult.LocalBranch -ceq 'snapshot/sanitized') 'The first unborn publication reported the wrong branches.'
    Invoke-Git -Repository $sourceRepository -Arguments @('worktree', 'remove', '--force', $unbornWorktree) | Out-Null
    Invoke-Git -Repository $sourceRepository -Arguments @('branch', '-D', 'snapshot/sanitized') | Out-Null

    $orphanRollbackRepository = Join-Path $testRoot 'orphan-rollback-source'
    New-TestRepository -Path $orphanRollbackRepository
    Set-Content -LiteralPath (Join-Path $orphanRollbackRepository 'before.txt') -Value 'before orphan publication' -NoNewline
    $orphanRollbackCommit = Add-TestCommit -Repository $orphanRollbackRepository -Message 'Add orphan rollback base'
    Set-TestTargetSafety -Repository $orphanRollbackRepository
    Invoke-Git -Repository $orphanRollbackRepository -Arguments @('branch', 'bootstrap-target') | Out-Null
    $orphanRollbackWorktree = Join-Path $testRoot 'orphan-rollback-worktree'
    Invoke-Git -Repository $orphanRollbackRepository -Arguments @('worktree', 'add', $orphanRollbackWorktree, 'bootstrap-target') | Out-Null
    $orphanRollbackCommon = ([string](Invoke-Git -Repository $orphanRollbackWorktree -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output[0]).Trim()
    $orphanRollbackManifest = @(Get-CwbiContentManifest -Root $orphanRollbackWorktree)
    $releaseModule = Get-Module CwbiRelease
    & $releaseModule { $script:CwbiTestFaultPoint = 'after-commit' }
    $orphanRollbackFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $orphanRollbackWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'bootstrap-target' `
            -ExpectedCurrentCommit $orphanRollbackCommit `
            -ExpectedCommonGitDirectory $orphanRollbackCommon | Out-Null
    }
    catch {
        $orphanRollbackFailed = $_.Exception.Message -match 'Injected CWBI test failure after-commit'
    }
    finally {
        & $releaseModule { $script:CwbiTestFaultPoint = $null }
    }
    Assert-Condition $orphanRollbackFailed 'A deterministic failure after first orphan commit creation was not observed.'
    Assert-Condition (([string](Invoke-Git -Repository $orphanRollbackWorktree -Arguments @('branch', '--show-current')).Output[0]).Trim() -ceq 'bootstrap-target') 'First-orphan rollback did not restore the original branch.'
    Assert-Condition (([string](Invoke-Git -Repository $orphanRollbackWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $orphanRollbackCommit) 'First-orphan rollback did not restore the original commit.'
    Assert-Condition ((Compare-Object $orphanRollbackManifest (Get-CwbiContentManifest -Root $orphanRollbackWorktree) -Property Path, Sha256).Count -eq 0) 'First-orphan rollback did not restore original content.'
    Assert-Condition ((Invoke-Git -Repository $orphanRollbackWorktree -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output.Count -eq 0) 'First-orphan rollback left the target dirty.'
    $orphanBranchRef = Invoke-Git -Repository $orphanRollbackWorktree -Arguments @('show-ref', '--verify', '--quiet', 'refs/heads/cwbi-dev') -AllowFailure
    Assert-Condition ($orphanBranchRef.ExitCode -ne 0) 'First-orphan rollback left the new cwbi-dev ref installed.'

    $targetMetadata = Publish-CwbiSnapshotToWorktree `
        -TargetWorktree $targetWorktree `
        -SnapshotRoot $snapshotRoot `
        -BranchName 'cwbi-dev' `
        -SourceCommit $sourceCommit `
        -SourceTree $metadata.SourceTree `
        -PolicySha256 $metadata.PolicySha256 `
        -PolicyPath $metadata.PolicyPath `
        -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
        -EvidenceDirectory $evidenceDirectory `
        -ExpectedCurrentBranch 'bootstrap-target' `
        -ExpectedCurrentCommit $targetBootstrapCommit `
        -ExpectedCommonGitDirectory $targetCommonDirectory

    Assert-Condition $targetMetadata.CreatedRootCommit 'First snapshot was not an orphan root commit.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-list', '--count', 'HEAD')).Output[0]).Trim() -ceq '1') 'First snapshot retained unrelated history.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $targetWorktree 'legacy.txt'))) 'Legacy target content survived replacement.'
    Assert-Condition ((Compare-Object $manifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Committed target differs from the snapshot.'
    Assert-Condition ((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1')).Output.Count -eq 0) 'Committed target is dirty.'
    $upstream = Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', '--abbrev-ref', '@{upstream}') -AllowFailure
    Assert-Condition ($upstream.ExitCode -ne 0) 'CWBI branch unexpectedly has an upstream.'
    Assert-Condition (Test-Path -LiteralPath $targetMetadata.EvidencePath -PathType Leaf) 'Release evidence manifest was not persisted.'
    $resolvedEvidencePath = [System.IO.Path]::GetFullPath($targetMetadata.EvidencePath)
    Assert-Condition ($resolvedEvidencePath.StartsWith("$([System.IO.Path]::GetFullPath($evidenceDirectory).TrimEnd('\'))\", [StringComparison]::OrdinalIgnoreCase)) 'Release evidence was written outside its designated external directory.'
    Assert-Condition (-not $resolvedEvidencePath.StartsWith("$([System.IO.Path]::GetFullPath($targetWorktree).TrimEnd('\'))\", [StringComparison]::OrdinalIgnoreCase)) 'Release evidence entered the deployment worktree.'
    $evidence = Get-Content -LiteralPath $targetMetadata.EvidencePath -Raw | ConvertFrom-Json
    Assert-Condition ($evidence.source.commit -ceq $sourceCommit) 'Evidence source commit is incorrect.'
    Assert-Condition ($evidence.source.tree -ceq $metadata.SourceTree) 'Evidence source tree is incorrect.'
    Assert-Condition ($evidence.policySha256 -ceq $metadata.PolicySha256) 'Evidence policy digest is incorrect.'
    Assert-Condition ($evidence.manifest.sha256 -ceq $metadata.ManifestSha256) 'Evidence manifest digest is incorrect.'
    Assert-Condition (@($evidence.manifest.entries).Count -eq $manifest.Count) 'Evidence manifest entries are incomplete.'
    Assert-Condition ($evidence.target.commit -ceq $targetMetadata.Commit) 'Evidence target commit is incorrect.'

    $temporaryBranchWorktree = Join-Path $testRoot 'temporary-snapshot-worktree'
    Invoke-Git -Repository $sourceRepository -Arguments @(
        'worktree', 'add', '-b', 'snapshot/sanitized', $temporaryBranchWorktree, $targetMetadata.Commit
    ) | Out-Null
    $temporaryBranchResult = Publish-CwbiSnapshotToWorktree `
        -TargetWorktree $temporaryBranchWorktree `
        -SnapshotRoot $snapshotRoot `
        -BranchName 'snapshot/sanitized' `
        -DestinationBranchName 'cwbi-dev' `
        -SourceCommit $sourceCommit `
        -SourceTree $metadata.SourceTree `
        -PolicySha256 $metadata.PolicySha256 `
        -PolicyPath $metadata.PolicyPath `
        -ManifestSha256 $metadata.ManifestSha256 `
        -EvidenceDirectory $evidenceDirectory `
        -ExpectedCurrentBranch 'snapshot/sanitized' `
        -ExpectedCurrentCommit $targetMetadata.Commit `
        -ExpectedCommonGitDirectory $targetCommonDirectory
    Assert-Condition (-not $temporaryBranchResult.Changed) 'An unchanged temporary snapshot branch created a new commit.'
    Assert-Condition ($temporaryBranchResult.LocalBranch -ceq 'snapshot/sanitized') 'Temporary local branch metadata is incorrect.'
    Assert-Condition ($temporaryBranchResult.Branch -ceq 'cwbi-dev') 'Temporary publication metadata did not retain cwbi-dev as the destination.'
    $temporaryEvidence = Get-Content -LiteralPath $temporaryBranchResult.EvidencePath -Raw | ConvertFrom-Json
    Assert-Condition ($temporaryEvidence.target.branch -ceq 'cwbi-dev') 'Temporary-branch evidence recorded the local holding branch as the destination.'
    Invoke-Git -Repository $sourceRepository -Arguments @('worktree', 'remove', '--force', $temporaryBranchWorktree) | Out-Null
    Invoke-Git -Repository $sourceRepository -Arguments @('branch', '-D', 'snapshot/sanitized') | Out-Null

    $validRootCommit = $targetMetadata.Commit
    $incorrectManifestDigest = '0' * 64
    Invoke-Git -Repository $targetWorktree -Arguments @(
        'commit', '--amend',
        '-m', 'Create CWBI development snapshot',
        '-m', "Source commit: $sourceCommit`nSource tree: $($metadata.SourceTree)`nPolicy SHA-256: $($metadata.PolicySha256)`nManifest SHA-256: $incorrectManifestDigest"
    ) | Out-Null
    $invalidProvenanceCommit = ([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
    Assert-Throws -MessagePattern 'recorded manifest provenance' -FailureMessage 'A prior snapshot whose tree disagreed with its manifest footer was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $invalidProvenanceCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $invalidProvenanceCommit) 'Invalid prior provenance rejection changed the target ref.'

    $invalidRootTree = ([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', "$invalidProvenanceCommit^{tree}")).Output[0]).Trim()
    $validChildOfInvalidRoot = ([string](Invoke-Git -Repository $targetWorktree -Arguments @(
        'commit-tree', $invalidRootTree,
        '-p', $invalidProvenanceCommit,
        '-m', 'Create CWBI development snapshot',
        '-m', "Source commit: $sourceCommit`nSource tree: $($metadata.SourceTree)`nPolicy SHA-256: $($metadata.PolicySha256)`nManifest SHA-256: $($metadata.ManifestSha256)"
    )).Output[0]).Trim()
    Invoke-Git -Repository $targetWorktree -Arguments @('update-ref', 'refs/heads/cwbi-dev', $validChildOfInvalidRoot, $invalidProvenanceCommit) | Out-Null
    Assert-Throws -MessagePattern 'recorded manifest provenance' -FailureMessage 'An invalid root manifest footer hidden behind a valid child snapshot was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $validChildOfInvalidRoot `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $validChildOfInvalidRoot) 'Invalid-root history rejection changed the target ref.'
    Invoke-Git -Repository $targetWorktree -Arguments @('update-ref', 'refs/heads/cwbi-dev', $validRootCommit, $validChildOfInvalidRoot) | Out-Null
    Invoke-Git -Repository $targetWorktree -Arguments @('restore', "--source=$validRootCommit", '--staged', '--worktree', '--', '.') | Out-Null

    $pinnedPolicyBytes = [System.IO.File]::ReadAllBytes($metadata.PolicyPath)
    try {
        [System.IO.File]::WriteAllText($metadata.PolicyPath, '{"schemaVersion":999}', [System.Text.UTF8Encoding]::new($false))
        Assert-Throws -MessagePattern 'policy changed' -FailureMessage 'A pinned policy mutation after the initial digest was accepted.' -Action {
            Publish-CwbiSnapshotToWorktree `
                -TargetWorktree $targetWorktree `
                -SnapshotRoot $snapshotRoot `
                -BranchName 'cwbi-dev' `
                -SourceCommit $sourceCommit `
                -SourceTree $metadata.SourceTree `
                -PolicySha256 $metadata.PolicySha256 `
                -PolicyPath $metadata.PolicyPath `
                -EvidenceDirectory $evidenceDirectory `
                -ManifestSha256 $metadata.ManifestSha256 `
                -ExpectedCurrentBranch 'cwbi-dev' `
                -ExpectedCurrentCommit $validRootCommit `
                -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($metadata.PolicyPath, $pinnedPolicyBytes)
    }
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $validRootCommit) 'Policy mutation rejection changed the target ref.'

    $validRootTree = ([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', "$validRootCommit^{tree}")).Output[0]).Trim()
    $mergeHistoryCommit = ([string](Invoke-Git -Repository $targetWorktree -Arguments @(
        'commit-tree', $validRootTree,
        '-p', $validRootCommit,
        '-p', $mainTipCommit,
        '-m', 'Create CWBI development snapshot',
        '-m', "Source commit: $sourceCommit`nSource tree: $($metadata.SourceTree)`nPolicy SHA-256: $($metadata.PolicySha256)`nManifest SHA-256: $($metadata.ManifestSha256)"
    )).Output[0]).Trim()
    Invoke-Git -Repository $targetWorktree -Arguments @('update-ref', 'refs/heads/cwbi-dev', $mergeHistoryCommit, $validRootCommit) | Out-Null
    Assert-Throws -MessagePattern 'linear|merge-free|snapshot history' -FailureMessage 'A merge commit in existing cwbi-dev history was accepted.' -Action {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 $metadata.ManifestSha256 `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $mergeHistoryCommit `
            -ExpectedCommonGitDirectory $targetCommonDirectory | Out-Null
    }
    Invoke-Git -Repository $targetWorktree -Arguments @('update-ref', 'refs/heads/cwbi-dev', $validRootCommit, $mergeHistoryCommit) | Out-Null
    Invoke-Git -Repository $targetWorktree -Arguments @('restore', "--source=$validRootCommit", '--staged', '--worktree', '--', '.') | Out-Null

    $wrongRootFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree (Join-Path $targetWorktree 'src') `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $targetMetadata.Commit | Out-Null
    }
    catch {
        $wrongRootFailed = $_.Exception.Message -match 'exact Git worktree root'
    }
    Assert-Condition $wrongRootFailed 'A nested directory was accepted as the target worktree root.'

    $wrongBranchFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'unexpected-branch' `
            -ExpectedCurrentCommit $targetMetadata.Commit | Out-Null
    }
    catch {
        $wrongBranchFailed = $_.Exception.Message -match 'Target branch changed'
    }
    Assert-Condition $wrongBranchFailed 'An unexpected target branch was accepted.'

    $wrongCommitFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit ('0' * 40) | Out-Null
    }
    catch {
        $wrongCommitFailed = $_.Exception.Message -match 'Target commit changed'
    }
    Assert-Condition $wrongCommitFailed 'An unexpected prior target commit was accepted.'

    $unchanged = Publish-CwbiSnapshotToWorktree `
        -TargetWorktree $targetWorktree `
        -SnapshotRoot $snapshotRoot `
        -BranchName 'cwbi-dev' `
        -SourceCommit $sourceCommit `
        -SourceTree $metadata.SourceTree `
        -PolicySha256 $metadata.PolicySha256 `
        -PolicyPath $metadata.PolicyPath `
        -EvidenceDirectory $evidenceDirectory `
        -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
        -ExpectedCurrentBranch 'cwbi-dev' `
        -ExpectedCurrentCommit $targetMetadata.Commit `
        -ExpectedCommonGitDirectory $targetCommonDirectory
    Assert-Condition (-not $unchanged.Changed) 'An identical snapshot created an unnecessary commit.'

    Invoke-Git -Repository $sourceRepository -Arguments @('restore', "--source=$sourceCommit", '--staged', '--worktree', '--', '.') | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceRepository 'app.txt') -Value 'follow-up application' -NoNewline
    $followUpSourceCommit = Add-TestCommit -Repository $sourceRepository -Message 'Add follow-up source change'
    $followUpSnapshotRoot = Join-Path $testRoot 'follow-up-snapshot'
    $followUpMetadata = Export-CwbiReleaseTree `
        -RepositoryRoot $sourceRepository `
        -SourceRevision $followUpSourceCommit `
        -Destination $followUpSnapshotRoot `
        -PolicyRepositoryPath 'scripts/cwbi-release/policy.json'
    $followUpManifest = @($followUpMetadata.Manifest)
    $followUpTarget = Publish-CwbiSnapshotToWorktree `
        -TargetWorktree $targetWorktree `
        -SnapshotRoot $followUpSnapshotRoot `
        -BranchName 'cwbi-dev' `
        -SourceCommit $followUpSourceCommit `
        -SourceTree $followUpMetadata.SourceTree `
        -PolicySha256 $followUpMetadata.PolicySha256 `
        -PolicyPath $followUpMetadata.PolicyPath `
        -EvidenceDirectory $evidenceDirectory `
        -ManifestSha256 $followUpMetadata.ManifestSha256 `
        -ExpectedCurrentBranch 'cwbi-dev' `
        -ExpectedCurrentCommit $targetMetadata.Commit `
        -ExpectedCommonGitDirectory $targetCommonDirectory
    Assert-Condition $followUpTarget.Changed 'A changed follow-up source snapshot did not create a commit.'
    Assert-Condition (-not $followUpTarget.CreatedRootCommit) 'A follow-up snapshot unexpectedly created another root commit.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-list', '--count', 'HEAD')).Output[0]).Trim() -ceq '2') 'Follow-up snapshot history contains an unexpected commit count.'
    Assert-Condition ((Compare-Object $followUpManifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Follow-up target differs from its immutable source snapshot.'

    $sourceCommit = $followUpSourceCommit
    $snapshotRoot = $followUpSnapshotRoot
    $metadata = $followUpMetadata
    $manifest = $followUpManifest
    $targetMetadata = $followUpTarget

    Set-Content -LiteralPath (Join-Path $targetWorktree 'untracked.txt') -Value 'preserve me' -NoNewline
    $dirtyFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $manifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $targetMetadata.Commit | Out-Null
    }
    catch {
        $dirtyFailed = $_.Exception.Message -match 'not completely clean'
    }
    Assert-Condition $dirtyFailed 'Dirty target replacement was accepted.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $targetWorktree 'untracked.txt')) 'Dirty preflight destroyed the untracked file.'
    Remove-Item -LiteralPath (Join-Path $targetWorktree 'untracked.txt')

    $stableCommit = ([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
    $stableManifest = @(Get-CwbiContentManifest -Root $targetWorktree)
    Set-Content -LiteralPath (Join-Path $snapshotRoot 'app.txt') -Value 'changed snapshot' -NoNewline
    $changedManifest = @(Get-CwbiContentManifest -Root $snapshotRoot)
    Invoke-Git -Repository $targetWorktree -Arguments @('config', 'user.name', ' ') | Out-Null
    Invoke-Git -Repository $targetWorktree -Arguments @('config', 'user.email', ' ') | Out-Null
    Invoke-Git -Repository $targetWorktree -Arguments @('config', 'user.useConfigOnly', 'true') | Out-Null
    $commitFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $changedManifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $stableCommit | Out-Null
    }
    catch {
        $commitFailed = $_.Exception.Message -match 'git commit'
    }
    Assert-Condition $commitFailed 'A failed commit did not fail closed.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $stableCommit) 'Failed commit changed the target ref.'
    Assert-Condition ((Compare-Object $stableManifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Failed commit did not restore target content.'
    Assert-Condition ((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1')).Output.Count -eq 0) 'Failed commit left the target dirty.'

    Invoke-Git -Repository $targetWorktree -Arguments @('config', 'user.name', 'CWBI Release Test') | Out-Null
    Invoke-Git -Repository $targetWorktree -Arguments @('config', 'user.email', 'cwbi-release-test@example.invalid') | Out-Null
    Invoke-Git -Repository $targetWorktree -Arguments @('config', '--unset', 'user.useConfigOnly') -AllowFailure | Out-Null
    Set-Content -LiteralPath (Join-Path $snapshotRoot 'app.txt') -Value 'post-commit rollback fixture' -NoNewline
    $postCommitManifest = @(Get-CwbiContentManifest -Root $snapshotRoot)
    $releaseModule = Get-Module CwbiRelease

    $junctionRaceEvidence = Join-Path $testRoot 'junction-evidence-race'
    & $releaseModule {
        param($EvidencePath, $TargetPath)
        $script:CwbiTestFaultEvidencePath = $EvidencePath
        $script:CwbiTestFaultTargetPath = $TargetPath
        $script:CwbiTestFaultAction = {
            param($Point)
            if ($Point -ceq 'after-evidence-precreate-check') {
                [System.IO.Directory]::Delete($script:CwbiTestFaultEvidencePath)
                New-Item -ItemType Junction -Path $script:CwbiTestFaultEvidencePath -Target $script:CwbiTestFaultTargetPath | Out-Null
            }
        }
    } $junctionRaceEvidence $targetWorktree
    $junctionRaceFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $junctionRaceEvidence `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $postCommitManifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $stableCommit | Out-Null
    }
    catch {
        $junctionRaceFailed = $_.Exception.Message -match 'reparse|junction|symbolic'
    }
    finally {
        & $releaseModule {
            $script:CwbiTestFaultAction = $null
            $script:CwbiTestFaultEvidencePath = $null
            $script:CwbiTestFaultTargetPath = $null
        }
        Remove-TestEvidenceFixture -Path $junctionRaceEvidence -TestRoot $testRoot
    }
    Assert-Condition $junctionRaceFailed 'A junction swap immediately before evidence creation was not rejected.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $stableCommit) 'Evidence-junction race failure did not restore the original target ref.'
    Assert-Condition ((Compare-Object $stableManifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Evidence-junction race failure did not restore target content.'
    Assert-Condition (@((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output).Count -eq 0) 'Evidence-junction race failure left the target dirty.'
    Assert-Condition (@(Get-ChildItem -LiteralPath $targetWorktree -Filter 'cwbi-release-*.json' -File -Force).Count -eq 0) 'Evidence-junction race failure left evidence inside the target.'

    $postWriteEvidence = Join-Path $testRoot 'post-write-evidence'
    & $releaseModule { $script:CwbiTestFaultPoint = 'after-evidence-write' }
    $postWriteEvidenceFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $postWriteEvidence `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $postCommitManifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $stableCommit | Out-Null
    }
    catch {
        $postWriteEvidenceFailed = $_.Exception.Message -match 'Injected CWBI test failure after-evidence-write'
    }
    finally {
        & $releaseModule { $script:CwbiTestFaultPoint = $null }
    }
    Assert-Condition $postWriteEvidenceFailed 'A deterministic failure after evidence writing was not observed.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $stableCommit) 'Post-write evidence failure did not restore the original target ref.'
    Assert-Condition ((Compare-Object $stableManifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Post-write evidence failure did not restore target content.'
    Assert-Condition (@((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output).Count -eq 0) 'Post-write evidence failure left the target dirty.'
    Assert-Condition (-not (Test-Path -LiteralPath $postWriteEvidence) -or @(Get-ChildItem -LiteralPath $postWriteEvidence -File -Force).Count -eq 0) 'Post-write evidence failure left a created evidence file behind.'
    Remove-TestEvidenceFixture -Path $postWriteEvidence -TestRoot $testRoot

    & $releaseModule {
        param($TargetRepository)
        $script:CwbiTestFaultRepository = $TargetRepository
        $script:CwbiTestFaultAction = {
            param($Point)
            if ($Point -ceq 'after-commit') {
                Invoke-CwbiGit -Repository $script:CwbiTestFaultRepository -Arguments @('config', '--local', 'push.default', 'simple') | Out-Null
            }
        }
    } $targetWorktree
    $topologyRecheckFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $postCommitManifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $stableCommit | Out-Null
    }
    catch {
        $topologyRecheckFailed = $_.Exception.Message -match 'push.default'
    }
    finally {
        & $releaseModule {
            $script:CwbiTestFaultAction = $null
            $script:CwbiTestFaultRepository = $null
        }
        Invoke-Git -Repository $targetWorktree -Arguments @('config', '--local', 'push.default', 'nothing') | Out-Null
    }
    Assert-Condition $topologyRecheckFailed 'A push-topology mutation after commit creation was not rejected.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $stableCommit) 'Topology recheck failure did not restore the original target ref.'
    Assert-Condition ((Compare-Object $stableManifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Topology recheck failure did not restore target content.'
    Assert-Condition ((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1')).Output.Count -eq 0) 'Topology recheck failure left the target dirty.'

    & $releaseModule { $script:CwbiTestFaultPoint = 'after-commit' }
    $postCommitFailed = $false
    try {
        Publish-CwbiSnapshotToWorktree `
            -TargetWorktree $targetWorktree `
            -SnapshotRoot $snapshotRoot `
            -BranchName 'cwbi-dev' `
            -SourceCommit $sourceCommit `
            -SourceTree $metadata.SourceTree `
            -PolicySha256 $metadata.PolicySha256 `
            -PolicyPath $metadata.PolicyPath `
            -EvidenceDirectory $evidenceDirectory `
            -ManifestSha256 (Get-CwbiManifestSha256 -Manifest $postCommitManifest) `
            -ExpectedCurrentBranch 'cwbi-dev' `
            -ExpectedCurrentCommit $stableCommit | Out-Null
    }
    catch {
        $postCommitFailed = $_.Exception.Message -match 'Injected CWBI test failure after-commit'
    }
    finally {
        & $releaseModule { $script:CwbiTestFaultPoint = $null }
    }
    Assert-Condition $postCommitFailed 'A deterministic failure after commit creation was not observed.'
    Assert-Condition (([string](Invoke-Git -Repository $targetWorktree -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim() -ceq $stableCommit) 'Post-commit failure did not restore the original target ref.'
    Assert-Condition ((Compare-Object $stableManifest (Get-CwbiContentManifest -Root $targetWorktree) -Property Path, Sha256).Count -eq 0) 'Post-commit failure did not restore target content.'
    Assert-Condition ((Invoke-Git -Repository $targetWorktree -Arguments @('status', '--porcelain=v1')).Output.Count -eq 0) 'Post-commit failure left the target dirty.'

    Assert-NoRemoteGitOperations -TracePath $gitTracePath

    'All RMC-TotalRisk CWBI release-preparation tests passed.'
}
finally {
    if ($null -eq $originalAllowedProtocol) {
        Remove-Item Env:GIT_ALLOW_PROTOCOL -ErrorAction SilentlyContinue
    }
    else {
        $env:GIT_ALLOW_PROTOCOL = $originalAllowedProtocol
    }
    if ($null -eq $originalGitTrace2Event) {
        Remove-Item Env:GIT_TRACE2_EVENT -ErrorAction SilentlyContinue
    }
    else {
        $env:GIT_TRACE2_EVENT = $originalGitTrace2Event
    }
    if (Test-Path -LiteralPath $gitTracePath) {
        Remove-Item -LiteralPath $gitTracePath -Force
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
