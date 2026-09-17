param(
    [string]$WorkflowRevision,
    [string]$WorkflowPath,
    [switch]$ContractOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workflowRepositoryPath = '.github/workflows/cwbi-build-push-api.yml'
$workflowSourcePath = Join-Path $repositoryRoot $workflowRepositoryPath
$nuGetAuditHelperPath = Join-Path $repositoryRoot '.github\scripts\CwbiNuGetAudit.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-api-workflow-$([Guid]::NewGuid())"
$testWorkflowPath = Join-Path $testRoot 'workflow.yml'
$yqImage = 'mikefarah/yq@sha256:11a1f0b604b13dbbdc662260d8db6f644b22d8553122a25c1b5b2e8713ca6977'
$bashPath = if ($IsWindows) { 'C:\Program Files\Git\bin\bash.exe' } else { (Get-Command bash -ErrorAction Stop).Source }

function Assert-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-StepIndex {
    param(
        [Parameter(Mandatory)][object[]]$Steps,
        [Parameter(Mandatory)][string]$Name
    )

    for ($index = 0; $index -lt $Steps.Count; $index++) {
        if ($Steps[$index].name -ceq $Name) {
            return $index
        }
    }
    throw "Workflow step '$Name' is missing."
}

function ConvertFrom-WorkflowYaml {
    param([Parameter(Mandatory)][string]$Path)

    $workflowDirectory = Split-Path -Parent $Path
    $workflowFileName = Split-Path -Leaf $Path
    $workflowJson = & docker run --rm --volume "${workflowDirectory}:/work:ro" $yqImage -o=json '.' "/work/$workflowFileName"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to parse '$Path' as YAML."
    }

    return ($workflowJson | ConvertFrom-Json)
}

function Assert-CwbiWorkflowContract {
    param([Parameter(Mandatory)][string]$Path)

    $workflow = ConvertFrom-WorkflowYaml -Path $Path

    $triggerNames = @($workflow.on.PSObject.Properties.Name)
    Assert-Condition ($triggerNames -ccontains 'push') 'Workflow must run on pushes.'
    Assert-Condition ($triggerNames -ccontains 'workflow_dispatch') 'Workflow must support manual dispatch.'
    $pushBranches = @($workflow.on.push.branches)
    Assert-Condition ($pushBranches.Count -eq 1 -and $pushBranches[0] -ceq 'cwbi-dev') 'Push trigger must be limited to cwbi-dev.'
    Assert-Condition (-not ($workflow.on.push.PSObject.Properties.Name -ccontains 'paths')) 'Every push to cwbi-dev must trigger; push path filters are forbidden.'

    Assert-Condition ($workflow.concurrency.group -ceq 'cwbi-total-risk-dev') 'Concurrency must serialize dst-total-risk deployments.'
    Assert-Condition ($workflow.concurrency.'cancel-in-progress' -eq $false) 'An in-progress deployment must not be cancelled.'
    Assert-Condition ($workflow.permissions.'id-token' -ceq 'write') 'Workflow must permit OIDC token issuance.'
    Assert-Condition ($workflow.permissions.contents -ceq 'read') 'Workflow contents permission must be read-only.'
    $permissionNames = @($workflow.permissions.PSObject.Properties.Name | Sort-Object)
    Assert-Condition (($permissionNames -join ',') -ceq 'contents,id-token') 'Workflow must not request additional GitHub permissions.'

    Assert-Condition ($workflow.env.AWS_ACCOUNT_ID -ceq '718787032875') 'Workflow AWS account is incorrect.'
    Assert-Condition ($workflow.env.AWS_REGION -ceq 'us-gov-west-1') 'Workflow AWS region is incorrect.'
    Assert-Condition ($workflow.env.ROLE_TO_ASSUME -ceq 'arn:aws-us-gov:iam::718787032875:role/github-actions-ecr-dst-total-risk') 'Workflow must assume the dst-total-risk ECR role.'
    Assert-Condition ($workflow.env.ECR_REPOSITORY -ceq 'dst-total-risk') 'Workflow ECR repository is incorrect.'
    Assert-Condition ($workflow.env.DEPLOYMENT_TAG -ceq 'dev') 'CWBI deployment tag must remain dev.'
    Assert-Condition ($workflow.env.LOCAL_IMAGE -ceq 'dst-total-risk:deployment-candidate') 'Local candidate image name is incorrect.'

    $jobProperties = @($workflow.jobs.PSObject.Properties)
    $job = $workflow.jobs.'build-push-dev'
    Assert-Condition ($job.if -ceq "github.ref == 'refs/heads/cwbi-dev'") 'Manual dispatch must also be guarded to cwbi-dev.'
    Assert-Condition ($job.'timeout-minutes' -gt 0 -and $job.'timeout-minutes' -le 60) 'Job timeout must be between 1 and 60 minutes.'
    $steps = @($job.steps)

    $checkoutIndex = Get-StepIndex -Steps $steps -Name 'Check out repository'
    $setupDotnetIndex = Get-StepIndex -Steps $steps -Name 'Set up .NET SDK'
    $workflowTestIndex = Get-StepIndex -Steps $steps -Name 'Verify deployment workflow contract'
    $imageTestIndex = Get-StepIndex -Steps $steps -Name 'Test image verifier'
    $restoreIndex = Get-StepIndex -Steps $steps -Name 'Restore locked dependencies'
    $buildSolutionIndex = Get-StepIndex -Steps $steps -Name 'Build Release projects'
    $testIndex = Get-StepIndex -Steps $steps -Name 'Run complete test suite'
    $auditIndex = Get-StepIndex -Steps $steps -Name 'Audit NuGet dependencies'
    $sourceIndex = Get-StepIndex -Steps $steps -Name 'Resolve upstream source provenance'
    $imageBuildIndex = Get-StepIndex -Steps $steps -Name 'Build Release image'
    $imageVerifyIndex = Get-StepIndex -Steps $steps -Name 'Verify deployment image'
    $credentialsIndex = Get-StepIndex -Steps $steps -Name 'Configure short-lived AWS credentials'
    $repositoryIndex = Get-StepIndex -Steps $steps -Name 'Verify ECR repository'
    $loginIndex = Get-StepIndex -Steps $steps -Name 'Log in to Amazon ECR'
    $publishIndex = Get-StepIndex -Steps $steps -Name 'Publish commit and deployment tags'
    $digestIndex = Get-StepIndex -Steps $steps -Name 'Verify published image digest'

    Assert-Condition ($checkoutIndex -lt $setupDotnetIndex -and $setupDotnetIndex -lt $workflowTestIndex) 'Checkout and .NET setup must precede contract verification.'
    Assert-Condition ($workflowTestIndex -lt $imageTestIndex -and $imageTestIndex -lt $restoreIndex) 'Contract tests must run before dependency restore.'
    Assert-Condition ($restoreIndex -lt $buildSolutionIndex -and $buildSolutionIndex -lt $testIndex) 'Locked restore, build, and tests are out of order.'
    Assert-Condition ($testIndex -lt $auditIndex -and $auditIndex -lt $sourceIndex -and $sourceIndex -lt $imageBuildIndex -and $imageBuildIndex -lt $imageVerifyIndex) 'Tests, audit, provenance, image build, and image verification are out of order.'
    Assert-Condition ($imageVerifyIndex -lt $credentialsIndex) 'All local verification must finish before AWS credentials are requested.'
    Assert-Condition ($credentialsIndex -lt $repositoryIndex -and $repositoryIndex -lt $loginIndex -and $loginIndex -lt $publishIndex -and $publishIndex -lt $digestIndex) 'OIDC, ECR repository preflight, login, publication, and digest verification are out of order.'

    $reviewedActions = @{
        'actions/checkout' = 'de0fac2e4500dabe0009e67214ff5f5447ce83dd'
        'actions/setup-dotnet' = 'a98b56852c35b8e3190ac28c8c2271da59106c68'
        'aws-actions/configure-aws-credentials' = 'e6de054238d6b7531b4efff3b6587d9aade6a06c'
        'aws-actions/amazon-ecr-login' = 'd539f0932e70871a027e9d5a9d8fc38589180a64'
    }
    $usesSteps = @(
        foreach ($jobProperty in $jobProperties) {
            foreach ($candidateStep in @($jobProperty.Value.steps)) {
                if ($candidateStep.PSObject.Properties.Name -ccontains 'uses') {
                    $candidateStep
                }
            }
        }
    )
    $actionOccurrences = @{}
    foreach ($usesStep in $usesSteps) {
        $uses = [string]$usesStep.uses
        Assert-Condition ($uses -cmatch '^(?<action>[a-z0-9_.-]+/[a-z0-9_.-]+)@(?<revision>[0-9a-f]{40})$') "Action '$uses' must use owner/repository@<40 lowercase hex>."
        $action = $Matches.action
        $revision = $Matches.revision
        Assert-Condition ($reviewedActions.ContainsKey($action)) "Action '$action' is not in the reviewed allowlist."
        Assert-Condition ($revision -ceq $reviewedActions[$action]) "Action '$action' is not pinned to its reviewed revision."
        if (-not $actionOccurrences.ContainsKey($action)) {
            $actionOccurrences[$action] = 0
        }
        $actionOccurrences[$action]++
    }
    Assert-Condition ($usesSteps.Count -eq $reviewedActions.Count) 'Workflow action steps do not exactly match the reviewed allowlist.'
    foreach ($reviewedAction in $reviewedActions.Keys) {
        Assert-Condition ($actionOccurrences.ContainsKey($reviewedAction) -and $actionOccurrences[$reviewedAction] -eq 1) "Reviewed action '$reviewedAction' must appear exactly once."
    }
    Assert-Condition ($jobProperties.Count -eq 1 -and $jobProperties[0].Name -ceq 'build-push-dev') 'Workflow must contain only the reviewed build-push-dev job.'

    Assert-Condition ($steps[$setupDotnetIndex].with.'dotnet-version' -ceq '10.0.x') 'Workflow must install the .NET 10 SDK.'
    $restoreRun = [string]$steps[$restoreIndex].run
    Assert-Condition ($restoreRun.Contains('--locked-mode', [StringComparison]::Ordinal)) 'Restore must use locked mode.'
    foreach ($testProject in @('src/RMC.TotalRisk.Tests/RMC.TotalRisk.Tests.csproj', 'src/RMC.TotalRisk.Api.Tests/RMC.TotalRisk.Api.Tests.csproj')) {
        Assert-Condition ($restoreRun.Contains("dotnet restore $testProject --locked-mode --configfile nuget.cwbi.config", [StringComparison]::Ordinal)) "Restore must lock $testProject against nuget.cwbi.config."
        Assert-Condition (([string]$steps[$buildSolutionIndex].run).Contains("dotnet build $testProject --configuration Release --no-restore", [StringComparison]::Ordinal)) "Build must cover $testProject."
        Assert-Condition (([string]$steps[$testIndex].run).Contains("dotnet test $testProject --configuration Release --no-build", [StringComparison]::Ordinal)) "Workflow must run the complete $testProject suite."
    }
    $auditRun = [string]$steps[$auditIndex].run
    foreach ($requiredFragment in @(
        '. ./.github/scripts/CwbiNuGetAudit.ps1',
        "@('src/RMC.TotalRisk.Tests/RMC.TotalRisk.Tests.csproj', 'src/RMC.TotalRisk.Api.Tests/RMC.TotalRisk.Api.Tests.csproj')",
        'dotnet list $project package --vulnerable --include-transitive --no-restore',
        'if ($LASTEXITCODE -ne 0)',
        'Assert-CwbiNuGetAuditClean -AuditOutput'
    )) {
        Assert-Condition ($auditRun.Contains($requiredFragment, [StringComparison]::Ordinal)) "NuGet audit step is missing '$requiredFragment'."
    }
    Assert-Condition (-not $auditRun.Contains("-match 'has the following vulnerable packages'", [StringComparison]::Ordinal)) 'Workflow must not duplicate the shared NuGet audit parser with weaker inline semantics.'

    $sourceStep = $steps[$sourceIndex]
    Assert-Condition ($sourceStep.id -ceq 'source-provenance') 'Source provenance step must expose a stable output id.'
    Assert-Condition ($sourceStep.shell -ceq 'bash') 'Source provenance parsing must use bash.'
    $sourceRun = [string]$sourceStep.run
    foreach ($requiredFragment in @(
        'git show -s --format=%B "$GITHUB_SHA"',
        "grep -Ei '^[[:space:]]*source[[:space:]]+commit[[:space:]]*:'",
        '"${#source_lines[@]}" -ne 1',
        '^Source\ commit:\ ([0-9a-f]{40})$',
        'source_revision=${BASH_REMATCH[1]}',
        'source_revision=%s',
        '$GITHUB_OUTPUT'
    )) {
        Assert-Condition ($sourceRun.Contains($requiredFragment, [StringComparison]::Ordinal)) "Source provenance parser is missing '$requiredFragment'."
    }

    $imageBuildRun = [string]$steps[$imageBuildIndex].run
    Assert-Condition ($imageBuildRun.Contains('--file Dockerfile', [StringComparison]::Ordinal)) 'Image build must use the repository Dockerfile.'
    Assert-Condition ($imageBuildRun.Contains('--build-arg "SOURCE_REPOSITORY=https://github.com/USACE-RMC/RMC-TotalRisk"', [StringComparison]::Ordinal)) 'Image build must label the upstream source repository.'
    Assert-Condition ($imageBuildRun.Contains('--build-arg "SOURCE_REVISION=${{ steps.source-provenance.outputs.source_revision }}"', [StringComparison]::Ordinal)) 'Image build must label the extracted upstream source revision.'
    Assert-Condition ($imageBuildRun.Contains('--build-arg "SNAPSHOT_REVISION=$GITHUB_SHA"', [StringComparison]::Ordinal)) 'Image build must label the exact CWBI snapshot revision.'
    Assert-Condition ($imageBuildRun.TrimEnd().EndsWith(' .', [StringComparison]::Ordinal)) 'Image build must use the repository root as its context.'

    $imageVerifyStep = $steps[$imageVerifyIndex]
    $imageVerifyRun = [string]$imageVerifyStep.run
    Assert-Condition ($imageVerifyStep.shell -ceq 'pwsh') 'Image verification must run in PowerShell.'
    Assert-Condition ($imageVerifyRun.Contains('./.github/scripts/Verify-TotalRiskImage.ps1', [StringComparison]::Ordinal)) 'Workflow must invoke the repository image verifier.'
    Assert-Condition ($imageVerifyRun.Contains('-ExpectedSourceRevision $env:EXPECTED_SOURCE_REVISION', [StringComparison]::Ordinal)) 'Image verification must pass the expected upstream revision.'
    Assert-Condition ($imageVerifyRun.Contains('-ExpectedSnapshotRevision $env:EXPECTED_SNAPSHOT_REVISION', [StringComparison]::Ordinal)) 'Image verification must pass the expected snapshot revision.'
    Assert-Condition ($imageVerifyStep.env.EXPECTED_SOURCE_REVISION -ceq '${{ steps.source-provenance.outputs.source_revision }}') 'Image verification upstream revision must come from the strict parser.'
    Assert-Condition ($imageVerifyStep.env.EXPECTED_SNAPSHOT_REVISION -ceq '${{ github.sha }}') 'Production image verification must require the GitHub snapshot SHA.'

    $repositoryStep = $steps[$repositoryIndex]
    $repositoryRun = [string]$repositoryStep.run
    Assert-Condition ($repositoryStep.shell -ceq 'pwsh') 'ECR preflight must use the executable PowerShell fixture contract.'
    Assert-Condition ($repositoryRun.Contains('aws ecr describe-repositories', [StringComparison]::Ordinal)) 'ECR preflight must inspect the configured repository.'
    Assert-Condition ($repositoryRun.Contains('--repository-names $env:ECR_REPOSITORY', [StringComparison]::Ordinal)) 'ECR preflight must inspect only the configured repository.'
    Assert-Condition ($repositoryRun.Contains('ConvertFrom-Json -ErrorAction Stop', [StringComparison]::Ordinal)) 'ECR preflight must parse the AWS response as JSON.'
    Assert-Condition ($repositoryRun.Contains('$repositories.Count -ne 1', [StringComparison]::Ordinal)) 'ECR preflight must require exactly one repository result.'
    Assert-Condition ($repositoryRun.Contains('$repository.repositoryName -cne $env:ECR_REPOSITORY', [StringComparison]::Ordinal)) 'ECR preflight must require the configured repository name.'
    Assert-Condition (-not $repositoryRun.Contains('imageTagMutability', [StringComparison]::OrdinalIgnoreCase)) 'ECR preflight must not reject the current mutable repository policy.'
    Assert-Condition (-not $repositoryRun.Contains('put-image-tag-mutability', [StringComparison]::OrdinalIgnoreCase)) 'Workflow must not mutate ECR repository settings.'

    $publishStep = $steps[$publishIndex]
    $publishRun = [string]$publishStep.run
    Assert-Condition ($publishStep.shell -ceq 'bash') 'Image publication must use the executable Bash fixture contract.'
    Assert-Condition ($publishRun.Contains('sha-$GITHUB_SHA', [StringComparison]::Ordinal)) 'Publishing must include a commit-specific tag.'
    Assert-Condition ($publishRun.Contains('run-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}', [StringComparison]::Ordinal)) 'Publishing must start with a unique run tag.'
    Assert-Condition ($publishRun.Contains('existing_commit_digest', [StringComparison]::Ordinal)) 'Publishing must inspect an existing commit tag.'
    Assert-Condition ($publishRun.Contains('existing commit tag resolves to a different image digest', [StringComparison]::OrdinalIgnoreCase)) 'Publishing must reject an observed commit-tag collision.'
    Assert-Condition ($publishRun.Contains('^sha256:[0-9a-f]{64}$', [StringComparison]::Ordinal)) 'Publishing must require a complete lowercase sha256 digest.'
    Assert-Condition ($publishRun.Contains('$image_uri:$DEPLOYMENT_TAG', [StringComparison]::Ordinal)) 'Publishing must update the watched deployment tag.'
    Assert-Condition (-not $publishRun.Contains(':latest', [StringComparison]::OrdinalIgnoreCase)) 'Publishing must not update latest.'
    $candidatePushIndex = $publishRun.IndexOf('docker push "$image_uri:$candidate_tag"', [StringComparison]::Ordinal)
    $existingDigestIndex = $publishRun.IndexOf('existing_commit_digest=', [StringComparison]::Ordinal)
    $deploymentPushIndex = $publishRun.IndexOf('docker push "$image_uri:$DEPLOYMENT_TAG"', [StringComparison]::Ordinal)
    Assert-Condition ($candidatePushIndex -ge 0 -and $candidatePushIndex -lt $existingDigestIndex) 'Candidate push must precede commit-tag collision checks.'
    Assert-Condition ($existingDigestIndex -lt $deploymentPushIndex) 'Collision checks must precede the deployment-tag push.'

    $digestStep = $steps[$digestIndex]
    $digestRun = [string]$digestStep.run
    Assert-Condition ($digestStep.shell -ceq 'bash') 'Digest verification must use the executable Bash fixture contract.'
    Assert-Condition (([regex]::Matches($digestRun, 'aws ecr describe-images')).Count -eq 2) 'Digest verification must query both commit-specific and deployment tags.'
    Assert-Condition ($digestRun.Contains('test "$commit_digest" = "$CANDIDATE_DIGEST"', [StringComparison]::Ordinal)) 'Commit tag must resolve to the candidate digest.'
    Assert-Condition ($digestRun.Contains('test "$deployment_digest" = "$CANDIDATE_DIGEST"', [StringComparison]::Ordinal)) 'Deployment tag must resolve to the candidate digest.'

    $structuredWorkflow = $workflow | ConvertTo-Json -Depth 100
    Assert-Condition ($structuredWorkflow -notmatch '(?i)AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY|AWS_SESSION_TOKEN|secrets\.') 'Workflow must not use static AWS credentials or stored secrets.'
    Assert-Condition ($structuredWorkflow -notmatch '(?i)aws\s+ecs') 'Workflow must leave ECS rollout to CWBI CodePipeline.'
    Assert-Condition ($structuredWorkflow -notmatch '(?i)put-image-tag-mutability') 'Workflow must never mutate ECR tag mutability.'
    Assert-Condition ($structuredWorkflow -notmatch '(?i):latest') 'Workflow must never publish a latest tag.'

    return $workflow
}

function Assert-ProvenanceParserFixture {
    param(
        [Parameter(Mandatory)][string]$RunScript,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$CommitMessage,
        [Parameter(Mandatory)][bool]$ShouldSucceed,
        [string]$ExpectedRevision
    )

    $fixtureDirectory = Join-Path $testRoot "provenance-$Name"
    New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
    $scriptPath = Join-Path $fixtureDirectory 'run.sh'
    $outputPath = Join-Path $fixtureDirectory 'github-output.txt'
    $gitShim = @'
git() {
  printf '%s' "$FAKE_COMMIT_MESSAGE"
}
'@
    [System.IO.File]::WriteAllText($scriptPath, "$gitShim`n$RunScript`n", [System.Text.UTF8Encoding]::new($false))

    $savedMessage = $env:FAKE_COMMIT_MESSAGE
    $savedSha = $env:GITHUB_SHA
    $savedOutput = $env:GITHUB_OUTPUT
    try {
        $env:FAKE_COMMIT_MESSAGE = $CommitMessage
        $env:GITHUB_SHA = '2222222222222222222222222222222222222222'
        $env:GITHUB_OUTPUT = 'github-output.txt'
        Push-Location $fixtureDirectory
        try {
            $parserOutput = @(& $bashPath './run.sh' 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        if ($ShouldSucceed) {
            Assert-Condition ($exitCode -eq 0) "Provenance parser rejected valid fixture '$Name': $($parserOutput -join ' ')"
            $output = (Get-Content -Raw -LiteralPath $outputPath).Trim()
            Assert-Condition ($output -ceq "source_revision=$ExpectedRevision") "Provenance parser emitted an incorrect revision for '$Name'."
        }
        else {
            Assert-Condition ($exitCode -ne 0) "Provenance parser accepted invalid fixture '$Name'."
        }
    }
    finally {
        $env:FAKE_COMMIT_MESSAGE = $savedMessage
        $env:GITHUB_SHA = $savedSha
        $env:GITHUB_OUTPUT = $savedOutput
    }
}

function Assert-EcrPreflightFixture {
    param(
        [Parameter(Mandatory)][string]$RunScript,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$RepositoryJson,
        [int]$AwsExitCode = 0,
        [Parameter(Mandatory)][bool]$ShouldSucceed,
        [bool]$RequireProcessFailure = $true
    )

    $fixtureDirectory = Join-Path $testRoot "ecr-$Name"
    New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
    $scriptPath = Join-Path $fixtureDirectory 'run.ps1'
    $responsePath = Join-Path $fixtureDirectory 'response.json'
    $callLogPath = Join-Path $fixtureDirectory 'calls.txt'
    [System.IO.File]::WriteAllText($responsePath, $RepositoryJson, [System.Text.UTF8Encoding]::new($false))
    $awsShim = @'
$ErrorActionPreference = 'Stop'
function aws {
    $capturedArguments = @($args)
    [System.IO.File]::AppendAllText(
        $env:FAKE_CALL_LOG,
        (($capturedArguments -join "`t") + "`n"),
        [System.Text.UTF8Encoding]::new($false))
    $global:LASTEXITCODE = [int]$env:FAKE_AWS_EXIT_CODE
    if ($global:LASTEXITCODE -eq 0) {
        [System.IO.File]::ReadAllText($env:FAKE_AWS_RESPONSE)
    }
}
'@
    [System.IO.File]::WriteAllText($scriptPath, "$awsShim`n$RunScript`n", [System.Text.UTF8Encoding]::new($false))

    $fixtureEnvironment = @{
        ECR_REPOSITORY = 'dst-total-risk'
        DEPLOYMENT_TAG = 'dev'
        FAKE_AWS_EXIT_CODE = [string]$AwsExitCode
        FAKE_AWS_RESPONSE = $responsePath
        FAKE_CALL_LOG = $callLogPath
    }
    $savedEnvironment = @{}
    try {
        foreach ($entry in $fixtureEnvironment.GetEnumerator()) {
            $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
        $processOutput = @(& pwsh -NoProfile -File $scriptPath 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    $calls = @()
    if (Test-Path -LiteralPath $callLogPath) {
        $calls = @(Get-Content -LiteralPath $callLogPath)
    }
    $expectedCall = "ecr`tdescribe-repositories`t--repository-names`tdst-total-risk`t--output`tjson"
    $semanticSuccess = $exitCode -eq 0 -and $calls.Count -eq 1 -and $calls[0] -ceq $expectedCall
    foreach ($call in $calls) {
        Assert-Condition ($call -ceq $expectedCall) "ECR preflight fixture '$Name' made a non-describe or incorrectly scoped call: $call"
        Assert-Condition ($call -notmatch '(?i)put-image-tag-mutability|create-repository|delete-repository|tag-resource|untag-resource') "ECR preflight fixture '$Name' attempted to mutate ECR."
    }
    if ($ShouldSucceed) {
        Assert-Condition $semanticSuccess "ECR preflight rejected valid fixture '$Name': $($processOutput -join ' ')"
    }
    else {
        Assert-Condition (-not $semanticSuccess) "ECR preflight accepted invalid fixture '$Name'."
        if ($RequireProcessFailure) {
            Assert-Condition ($exitCode -ne 0) "ECR preflight did not fail closed for invalid fixture '$Name'."
        }
    }
}

function Invoke-BashStepFixture {
    param(
        [Parameter(Mandatory)][string]$RunScript,
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Environment = @{}
    )

    $fixtureDirectory = Join-Path $testRoot "bash-$Name"
    New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
    $scriptPath = Join-Path $fixtureDirectory 'run.sh'
    $callLogPath = Join-Path $fixtureDirectory 'calls.txt'
    $outputPath = Join-Path $fixtureDirectory 'github-output.txt'
    $summaryPath = Join-Path $fixtureDirectory 'github-summary.txt'
    $commandShims = @'
log_call() {
  local command_name="$1"
  shift
  {
    printf '%s' "$command_name"
    printf '|%s' "$@"
    printf '\n'
  } >> "$FAKE_CALL_LOG"
}
docker() {
  log_call docker "$@"
  if [[ -n "${FAKE_DOCKER_FAIL_MATCH:-}" && "$*" == *"$FAKE_DOCKER_FAIL_MATCH"* ]]; then
    return 17
  fi
}
aws() {
  log_call aws "$@"
  if [[ -n "${FAKE_AWS_FAIL_MATCH:-}" && "$*" == *"$FAKE_AWS_FAIL_MATCH"* ]]; then
    return 18
  fi
  case "$*" in
    *"--filter tagStatus=TAGGED"*) printf '%s\n' "$FAKE_EXISTING_DIGEST" ;;
    *"imageTag=run-"*) printf '%s\n' "$FAKE_CANDIDATE_DIGEST" ;;
    *"imageTag=sha-"*) printf '%s\n' "$FAKE_COMMIT_DIGEST" ;;
    *"imageTag=dev"*) printf '%s\n' "$FAKE_DEPLOYMENT_DIGEST" ;;
    *) printf 'None\n' ;;
  esac
}
'@
    [System.IO.File]::WriteAllText($scriptPath, "$commandShims`n$RunScript`n", [System.Text.UTF8Encoding]::new($false))

    $candidateDigest = "sha256:$('a' * 64)"
    $fixtureEnvironment = @{
        ECR_REGISTRY = 'registry.example'
        ECR_REPOSITORY = 'dst-total-risk'
        LOCAL_IMAGE = 'dst-total-risk:deployment-candidate'
        GITHUB_SHA = '2222222222222222222222222222222222222222'
        GITHUB_RUN_ID = '12345'
        GITHUB_RUN_ATTEMPT = '2'
        DEPLOYMENT_TAG = 'dev'
        IMAGE_URI = 'registry.example/dst-total-risk'
        COMMIT_TAG = 'sha-2222222222222222222222222222222222222222'
        CANDIDATE_DIGEST = $candidateDigest
        FAKE_CANDIDATE_DIGEST = $candidateDigest
        FAKE_EXISTING_DIGEST = 'None'
        FAKE_COMMIT_DIGEST = $candidateDigest
        FAKE_DEPLOYMENT_DIGEST = $candidateDigest
        FAKE_AWS_FAIL_MATCH = ''
        FAKE_DOCKER_FAIL_MATCH = ''
        FAKE_CALL_LOG = 'calls.txt'
        GITHUB_OUTPUT = 'github-output.txt'
        GITHUB_STEP_SUMMARY = 'github-summary.txt'
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $fixtureEnvironment[$entry.Key] = [string]$entry.Value
    }

    $savedEnvironment = @{}
    try {
        foreach ($entry in $fixtureEnvironment.GetEnumerator()) {
            $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
        Push-Location $fixtureDirectory
        try {
            $processOutput = @(& $bashPath --noprofile --norc -e -o pipefail './run.sh' 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    $calls = @()
    if (Test-Path -LiteralPath $callLogPath) {
        $calls = @(Get-Content -LiteralPath $callLogPath)
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        ProcessOutput = $processOutput
        Calls = $calls
        Output = if (Test-Path -LiteralPath $outputPath) { [System.IO.File]::ReadAllText($outputPath) } else { '' }
        Summary = if (Test-Path -LiteralPath $summaryPath) { [System.IO.File]::ReadAllText($summaryPath) } else { '' }
    }
}

function Assert-PublishFixture {
    param(
        [Parameter(Mandatory)][string]$RunScript,
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Environment = @{},
        [Parameter(Mandatory)][bool]$ShouldSucceed,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ExpectedCalls,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ExpectedOutput,
        [bool]$ExpectDeploymentAttempt = $false,
        [bool]$RequireProcessFailure = $true
    )

    $result = Invoke-BashStepFixture -RunScript $RunScript -Name "publish-$Name" -Environment $Environment
    $actualCalls = @($result.Calls)
    $actualCallLog = $actualCalls -join "`n"
    $expectedCallLog = @($ExpectedCalls) -join "`n"
    Assert-Condition ($actualCallLog -ceq $expectedCallLog) "Publish fixture '$Name' call order mismatch.`nExpected:`n$expectedCallLog`nActual:`n$actualCallLog"
    Assert-Condition ($result.Output -ceq $ExpectedOutput) "Publish fixture '$Name' emitted unexpected GitHub outputs."
    Assert-Condition (-not $actualCallLog.Contains(':latest', [StringComparison]::OrdinalIgnoreCase)) "Publish fixture '$Name' used latest."
    $deploymentCalls = @($actualCalls | Where-Object { $_.EndsWith(':dev', [StringComparison]::Ordinal) })
    if ($ExpectDeploymentAttempt) {
        Assert-Condition ($deploymentCalls.Count -gt 0) "Publish fixture '$Name' did not attempt the deployment tag."
    }
    else {
        Assert-Condition ($deploymentCalls.Count -eq 0) "Publish fixture '$Name' attempted the deployment tag before successful collision resolution."
    }
    if ($ShouldSucceed) {
        Assert-Condition ($result.ExitCode -eq 0) "Publish step rejected valid fixture '$Name': $($result.ProcessOutput -join ' ')"
    }
    else {
        if ($RequireProcessFailure) {
            Assert-Condition ($result.ExitCode -ne 0) "Publish step did not fail closed for invalid fixture '$Name'."
        }
    }
}

function Assert-DigestFixture {
    param(
        [Parameter(Mandatory)][string]$RunScript,
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Environment = @{},
        [Parameter(Mandatory)][bool]$ShouldSucceed,
        [bool]$RequireProcessFailure = $true
    )

    $result = Invoke-BashStepFixture -RunScript $RunScript -Name "digest-$Name" -Environment $Environment
    $commitTag = 'sha-2222222222222222222222222222222222222222'
    $candidateDigest = "sha256:$('a' * 64)"
    $awsCalls = @($result.Calls | Where-Object { $_.StartsWith('aws|ecr|describe-images|', [StringComparison]::Ordinal) })
    $hasCommitQuery = @($awsCalls | Where-Object { $_.Contains("imageTag=$commitTag", [StringComparison]::Ordinal) }).Count -eq 1
    $hasDeploymentQuery = @($awsCalls | Where-Object { $_.Contains('imageTag=dev', [StringComparison]::Ordinal) }).Count -eq 1
    $semanticSuccess = $result.ExitCode -eq 0 -and $awsCalls.Count -eq 2 -and $hasCommitQuery -and $hasDeploymentQuery
    $semanticSuccess = $semanticSuccess -and $result.Summary.Contains("Published registry.example/dst-total-risk:$commitTag and registry.example/dst-total-risk:dev at $candidateDigest", [StringComparison]::Ordinal)
    if ($ShouldSucceed) {
        Assert-Condition $semanticSuccess "Digest verification rejected valid fixture '$Name': $($result.ProcessOutput -join ' ')"
    }
    else {
        Assert-Condition (-not $semanticSuccess) "Digest verification accepted invalid fixture '$Name'."
        if ($RequireProcessFailure) {
            Assert-Condition ($result.ExitCode -ne 0) "Digest verification did not fail closed for invalid fixture '$Name'."
        }
    }
}

function Assert-MutationRejected {
    param(
        [Parameter(Mandatory)][object]$Baseline,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Mutate
    )

    $mutated = (($Baseline | ConvertTo-Json -Depth 100) | ConvertFrom-Json)
    & $Mutate $mutated
    $mutatedPath = Join-Path $testRoot "mutation-$Name.json"
    [System.IO.File]::WriteAllText($mutatedPath, ($mutated | ConvertTo-Json -Depth 100), [System.Text.UTF8Encoding]::new($false))

    $rejected = $false
    try {
        $null = Assert-CwbiWorkflowContract -Path $mutatedPath
    }
    catch {
        $rejected = $true
    }
    Assert-Condition $rejected "Workflow contract accepted mutation '$Name'."
}

function Assert-PublishMutationRejected {
    param(
        [Parameter(Mandatory)][object]$Baseline,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Mutate,
        [Parameter(Mandatory)][string[]]$ExpectedCalls,
        [Parameter(Mandatory)][string]$ExpectedOutput
    )

    $mutated = (($Baseline | ConvertTo-Json -Depth 100) | ConvertFrom-Json)
    $publishIndex = Get-StepIndex @($mutated.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'
    $originalRun = [string]$mutated.jobs.'build-push-dev'.steps[$publishIndex].run
    & $Mutate $mutated
    $mutatedRun = [string]$mutated.jobs.'build-push-dev'.steps[$publishIndex].run
    Assert-Condition ($mutatedRun -cne $originalRun) "Publish mutation '$Name' did not change the run script."

    $mutatedPath = Join-Path $testRoot "mutation-$Name.json"
    [System.IO.File]::WriteAllText($mutatedPath, ($mutated | ConvertTo-Json -Depth 100), [System.Text.UTF8Encoding]::new($false))

    $rejected = $false
    try {
        $validated = Assert-CwbiWorkflowContract -Path $mutatedPath
        $validatedSteps = @($validated.jobs.'build-push-dev'.steps)
        $validatedRun = [string]$validatedSteps[(Get-StepIndex -Steps $validatedSteps -Name 'Publish commit and deployment tags')].run
        Assert-PublishFixture -RunScript $validatedRun -Name "mutation-$Name" -ShouldSucceed $true -ExpectedCalls $ExpectedCalls -ExpectedOutput $ExpectedOutput -ExpectDeploymentAttempt $true
    }
    catch {
        $rejected = $true
    }
    Assert-Condition $rejected "Workflow publish semantics accepted mutation '$Name'."
}

if ($WorkflowRevision -and $WorkflowPath) {
    throw 'Specify either WorkflowRevision or WorkflowPath, not both.'
}

New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    if ($WorkflowRevision) {
        $workflowContent = & git -C $repositoryRoot show "${WorkflowRevision}:$workflowRepositoryPath"
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to read $workflowRepositoryPath from revision $WorkflowRevision."
        }
        [System.IO.File]::WriteAllLines($testWorkflowPath, @($workflowContent), [System.Text.UTF8Encoding]::new($false))
    }
    elseif ($WorkflowPath) {
        Copy-Item -LiteralPath (Resolve-Path -LiteralPath $WorkflowPath).Path -Destination $testWorkflowPath
    }
    else {
        if (-not (Test-Path -LiteralPath $workflowSourcePath)) {
            throw "CWBI API workflow is missing: $workflowSourcePath"
        }
        Copy-Item -LiteralPath $workflowSourcePath -Destination $testWorkflowPath
    }

    $baseline = Assert-CwbiWorkflowContract -Path $testWorkflowPath
    if ($ContractOnly) {
        'RMC-TotalRisk CWBI API workflow contract passed.'
        return
    }

    if (-not (Test-Path -LiteralPath $nuGetAuditHelperPath -PathType Leaf)) {
        throw "Shared NuGet audit helper is missing: $nuGetAuditHelperPath"
    }
    . $nuGetAuditHelperPath
    $warningOnlyRejected = $false
    try {
        Assert-CwbiNuGetAuditClean -AuditOutput @('warning: vulnerability audit result unavailable')
    }
    catch {
        $warningOnlyRejected = $true
    }
    Assert-Condition $warningOnlyRejected 'Shared workflow NuGet audit semantics accepted warning-only output.'

    $steps = @($baseline.jobs.'build-push-dev'.steps)
    $sourceRun = [string]$steps[(Get-StepIndex -Steps $steps -Name 'Resolve upstream source provenance')].run
    $sourceRevision = '1111111111111111111111111111111111111111'
    Assert-ProvenanceParserFixture -RunScript $sourceRun -Name 'valid' -CommitMessage "Snapshot`n`nSource commit: $sourceRevision`n" -ShouldSucceed $true -ExpectedRevision $sourceRevision
    foreach ($fixture in @(
        @{ Name = 'missing'; Message = "Snapshot without provenance`n" },
        @{ Name = 'duplicate'; Message = "Source commit: $sourceRevision`nSource commit: $sourceRevision`n" },
        @{ Name = 'uppercase'; Message = 'Source commit: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' },
        @{ Name = 'short'; Message = 'Source commit: 111111111111111111111111111111111111111' },
        @{ Name = 'trailing'; Message = "Source commit: $sourceRevision " },
        @{ Name = 'suffix'; Message = "Source commit: ${sourceRevision}x" },
        @{ Name = 'malformed-prefix'; Message = "Source commit:$sourceRevision" },
        @{ Name = 'leading-space'; Message = " Source commit: $sourceRevision" },
        @{ Name = 'case-variant'; Message = "source commit: $sourceRevision" },
        @{ Name = 'extra-key-space'; Message = "Source  commit: $sourceRevision" },
        @{ Name = 'valid-plus-malformed'; Message = "Source commit: $sourceRevision`nSource commit:bad`n" }
    )) {
        Assert-ProvenanceParserFixture -RunScript $sourceRun -Name $fixture.Name -CommitMessage $fixture.Message -ShouldSucceed $false
    }

    $repositoryRun = [string]$steps[(Get-StepIndex -Steps $steps -Name 'Verify ECR repository')].run
    $validMutableRepository = @{
        repositories = @(@{
            repositoryName = 'dst-total-risk'
            imageTagMutability = 'MUTABLE'
            imageScanningConfiguration = @{ scanOnPush = $true }
        })
    } | ConvertTo-Json -Compress -Depth 10
    Assert-EcrPreflightFixture -RunScript $repositoryRun -Name 'valid-mutable' -RepositoryJson $validMutableRepository -ShouldSucceed $true
    $validImmutableRepository = '{"repositories":[{"repositoryName":"dst-total-risk","imageTagMutability":"IMMUTABLE_WITH_EXCLUSION","imageTagMutabilityExclusionFilters":[{"filterType":"WILDCARD","filter":"dev"}]}]}'
    Assert-EcrPreflightFixture -RunScript $repositoryRun -Name 'valid-immutable-with-exclusion' -RepositoryJson $validImmutableRepository -ShouldSucceed $true
    $validMinimalRepository = '{"repositories":[{"repositoryName":"dst-total-risk"}]}'
    Assert-EcrPreflightFixture -RunScript $repositoryRun -Name 'valid-minimal' -RepositoryJson $validMinimalRepository -ShouldSucceed $true
    foreach ($fixture in @(
        @{ Name = 'aws-error'; Json = $validMutableRepository; AwsExitCode = 23 },
        @{ Name = 'missing-repositories'; Json = '{}'; AwsExitCode = 0 },
        @{ Name = 'empty-repositories'; Json = '{"repositories":[]}'; AwsExitCode = 0 },
        @{ Name = 'wrong-repository'; Json = '{"repositories":[{"repositoryName":"not-dst-total-risk"}]}'; AwsExitCode = 0 },
        @{ Name = 'duplicate-repository'; Json = '{"repositories":[{"repositoryName":"dst-total-risk"},{"repositoryName":"dst-total-risk"}]}'; AwsExitCode = 0 },
        @{ Name = 'malformed-json'; Json = '{'; AwsExitCode = 0 }
    )) {
        Assert-EcrPreflightFixture -RunScript $repositoryRun -Name $fixture.Name -RepositoryJson $fixture.Json -AwsExitCode $fixture.AwsExitCode -ShouldSucceed $false
    }
    Assert-EcrPreflightFixture -RunScript '# comment-only preflight' -Name 'comment-only' -RepositoryJson $validMutableRepository -ShouldSucceed $false -RequireProcessFailure $false
    Assert-EcrPreflightFixture -RunScript '$true' -Name 'no-op' -RepositoryJson $validMutableRepository -ShouldSucceed $false -RequireProcessFailure $false

    $publishRun = [string]$steps[(Get-StepIndex -Steps $steps -Name 'Publish commit and deployment tags')].run
    $validDigest = "sha256:$('a' * 64)"
    $publishCall = @{
        CandidateTag = 'docker|tag|dst-total-risk:deployment-candidate|registry.example/dst-total-risk:run-12345-2'
        CandidatePush = 'docker|push|registry.example/dst-total-risk:run-12345-2'
        CandidateQuery = 'aws|ecr|describe-images|--repository-name|dst-total-risk|--image-ids|imageTag=run-12345-2|--query|imageDetails[0].imageDigest|--output|text'
        CollisionQuery = "aws|ecr|describe-images|--repository-name|dst-total-risk|--filter|tagStatus=TAGGED|--query|imageDetails[?contains(imageTags, 'sha-2222222222222222222222222222222222222222')].imageDigest | [0]|--output|text"
        CommitTag = 'docker|tag|dst-total-risk:deployment-candidate|registry.example/dst-total-risk:sha-2222222222222222222222222222222222222222'
        CommitPush = 'docker|push|registry.example/dst-total-risk:sha-2222222222222222222222222222222222222222'
        DeploymentTag = 'docker|tag|dst-total-risk:deployment-candidate|registry.example/dst-total-risk:dev'
        DeploymentPush = 'docker|push|registry.example/dst-total-risk:dev'
    }
    $newCommitCalls = @(
        $publishCall.CandidateTag,
        $publishCall.CandidatePush,
        $publishCall.CandidateQuery,
        $publishCall.CollisionQuery,
        $publishCall.CommitTag,
        $publishCall.CommitPush,
        $publishCall.DeploymentTag,
        $publishCall.DeploymentPush
    )
    $existingCommitCalls = @(
        $publishCall.CandidateTag,
        $publishCall.CandidatePush,
        $publishCall.CandidateQuery,
        $publishCall.CollisionQuery,
        $publishCall.DeploymentTag,
        $publishCall.DeploymentPush
    )
    $expectedPublishOutput = "image_uri=registry.example/dst-total-risk`ncommit_tag=sha-2222222222222222222222222222222222222222`ncandidate_digest=$validDigest`n"
    Assert-PublishFixture -RunScript $publishRun -Name 'new-commit' -ShouldSucceed $true -ExpectedCalls $newCommitCalls -ExpectedOutput $expectedPublishOutput -ExpectDeploymentAttempt $true
    Assert-PublishFixture -RunScript $publishRun -Name 'existing-same-commit' -Environment @{ FAKE_EXISTING_DIGEST = $validDigest } -ShouldSucceed $true -ExpectedCalls $existingCommitCalls -ExpectedOutput $expectedPublishOutput -ExpectDeploymentAttempt $true
    foreach ($fixture in @(
        @{ Name = 'candidate-tag-error'; Environment = @{ FAKE_DOCKER_FAIL_MATCH = 'tag dst-total-risk:deployment-candidate registry.example/dst-total-risk:run-12345-2' }; Calls = @($publishCall.CandidateTag); Deployment = $false },
        @{ Name = 'candidate-push-error'; Environment = @{ FAKE_DOCKER_FAIL_MATCH = 'push registry.example/dst-total-risk:run-12345-2' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush); Deployment = $false },
        @{ Name = 'candidate-query-error'; Environment = @{ FAKE_AWS_FAIL_MATCH = 'imageTag=run-' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery); Deployment = $false },
        @{ Name = 'candidate-none'; Environment = @{ FAKE_CANDIDATE_DIGEST = 'None' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery); Deployment = $false },
        @{ Name = 'candidate-empty'; Environment = @{ FAKE_CANDIDATE_DIGEST = '' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery); Deployment = $false },
        @{ Name = 'candidate-uppercase'; Environment = @{ FAKE_CANDIDATE_DIGEST = "sha256:$('A' * 64)" }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery); Deployment = $false },
        @{ Name = 'collision-query-error'; Environment = @{ FAKE_AWS_FAIL_MATCH = '--filter tagStatus=TAGGED' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery, $publishCall.CollisionQuery); Deployment = $false },
        @{ Name = 'commit-collision'; Environment = @{ FAKE_EXISTING_DIGEST = "sha256:$('b' * 64)" }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery, $publishCall.CollisionQuery); Deployment = $false },
        @{ Name = 'commit-tag-error'; Environment = @{ FAKE_DOCKER_FAIL_MATCH = 'tag dst-total-risk:deployment-candidate registry.example/dst-total-risk:sha-2222222222222222222222222222222222222222' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery, $publishCall.CollisionQuery, $publishCall.CommitTag); Deployment = $false },
        @{ Name = 'commit-push-error'; Environment = @{ FAKE_DOCKER_FAIL_MATCH = 'push registry.example/dst-total-risk:sha-2222222222222222222222222222222222222222' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery, $publishCall.CollisionQuery, $publishCall.CommitTag, $publishCall.CommitPush); Deployment = $false },
        @{ Name = 'deployment-tag-error'; Environment = @{ FAKE_DOCKER_FAIL_MATCH = 'tag dst-total-risk:deployment-candidate registry.example/dst-total-risk:dev' }; Calls = @($publishCall.CandidateTag, $publishCall.CandidatePush, $publishCall.CandidateQuery, $publishCall.CollisionQuery, $publishCall.CommitTag, $publishCall.CommitPush, $publishCall.DeploymentTag); Deployment = $true },
        @{ Name = 'deployment-push-error'; Environment = @{ FAKE_DOCKER_FAIL_MATCH = 'push registry.example/dst-total-risk:dev' }; Calls = $newCommitCalls; Deployment = $true }
    )) {
        Assert-PublishFixture -RunScript $publishRun -Name $fixture.Name -Environment $fixture.Environment -ShouldSucceed $false -ExpectedCalls $fixture.Calls -ExpectedOutput '' -ExpectDeploymentAttempt $fixture.Deployment
    }
    Assert-PublishFixture -RunScript '# comment-only publication' -Name 'comment-only' -ShouldSucceed $false -ExpectedCalls @() -ExpectedOutput '' -RequireProcessFailure $false
    Assert-PublishFixture -RunScript 'true' -Name 'no-op' -ShouldSucceed $false -ExpectedCalls @() -ExpectedOutput '' -RequireProcessFailure $false

    $digestRun = [string]$steps[(Get-StepIndex -Steps $steps -Name 'Verify published image digest')].run
    Assert-DigestFixture -RunScript $digestRun -Name 'matching' -ShouldSucceed $true
    foreach ($fixture in @(
        @{ Name = 'commit-mismatch'; Environment = @{ FAKE_COMMIT_DIGEST = "sha256:$('b' * 64)" } },
        @{ Name = 'deployment-mismatch'; Environment = @{ FAKE_DEPLOYMENT_DIGEST = "sha256:$('b' * 64)" } },
        @{ Name = 'none-digests'; Environment = @{ CANDIDATE_DIGEST = 'None'; FAKE_COMMIT_DIGEST = 'None'; FAKE_DEPLOYMENT_DIGEST = 'None' } },
        @{ Name = 'empty-digests'; Environment = @{ CANDIDATE_DIGEST = ''; FAKE_COMMIT_DIGEST = ''; FAKE_DEPLOYMENT_DIGEST = '' } },
        @{ Name = 'commit-query-error'; Environment = @{ FAKE_AWS_FAIL_MATCH = 'imageTag=sha-' } },
        @{ Name = 'deployment-query-error'; Environment = @{ FAKE_AWS_FAIL_MATCH = 'imageTag=dev' } }
    )) {
        Assert-DigestFixture -RunScript $digestRun -Name $fixture.Name -Environment $fixture.Environment -ShouldSucceed $false
    }
    Assert-DigestFixture -RunScript '# comment-only digest verification' -Name 'comment-only' -ShouldSucceed $false -RequireProcessFailure $false
    Assert-DigestFixture -RunScript 'true' -Name 'no-op' -ShouldSucceed $false -RequireProcessFailure $false

    $mutations = @(
        @{ Name = 'push-branch'; Apply = { param($w) $w.on.push.branches = @('main') } },
        @{ Name = 'push-path-filter'; Apply = { param($w) $w.on.push | Add-Member -NotePropertyName paths -NotePropertyValue @('RMC-TotalRisk/**') } },
        @{ Name = 'manual-dispatch'; Apply = { param($w) $w.on.PSObject.Properties.Remove('workflow_dispatch') } },
        @{ Name = 'branch-guard'; Apply = { param($w) $w.jobs.'build-push-dev'.if = "github.ref == 'refs/heads/main'" } },
        @{ Name = 'permission'; Apply = { param($w) $w.permissions.contents = 'write' } },
        @{ Name = 'extra-permission'; Apply = { param($w) $w.permissions | Add-Member -NotePropertyName packages -NotePropertyValue 'write' } },
        @{ Name = 'concurrency-group'; Apply = { param($w) $w.concurrency.group = 'other' } },
        @{ Name = 'concurrency-cancel'; Apply = { param($w) $w.concurrency.'cancel-in-progress' = $true } },
        @{ Name = 'aws-account'; Apply = { param($w) $w.env.AWS_ACCOUNT_ID = '000000000000' } },
        @{ Name = 'aws-region'; Apply = { param($w) $w.env.AWS_REGION = 'us-east-1' } },
        @{ Name = 'aws-role'; Apply = { param($w) $w.env.ROLE_TO_ASSUME = 'arn:aws:iam::000000000000:role/wrong' } },
        @{ Name = 'ecr-repository'; Apply = { param($w) $w.env.ECR_REPOSITORY = 'wrong' } },
        @{ Name = 'deployment-tag'; Apply = { param($w) $w.env.DEPLOYMENT_TAG = 'latest' } },
        @{ Name = 'provenance-parser'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Resolve upstream source provenance'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('[0-9a-f]{40}', '[0-9A-Fa-f]{40}') } },
        @{ Name = 'source-build-arg'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Build Release image'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('${{ steps.source-provenance.outputs.source_revision }}', '$GITHUB_SHA') } },
        @{ Name = 'snapshot-build-arg'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Build Release image'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('SNAPSHOT_REVISION=$GITHUB_SHA', 'SNAPSHOT_REVISION=not-published') } },
        @{ Name = 'nuget-audit-helper'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Audit NuGet dependencies'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('Assert-CwbiNuGetAuditClean -AuditOutput', 'Write-Host') } },
        @{ Name = 'verifier-expectations'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify deployment image'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('-ExpectedSnapshotRevision $env:EXPECTED_SNAPSHOT_REVISION', '') } },
        @{ Name = 'test-after-credentials'; Apply = { param($w) $s = [System.Collections.ArrayList]@($w.jobs.'build-push-dev'.steps); $from = Get-StepIndex @($s) 'Configure short-lived AWS credentials'; $item = $s[$from]; $s.RemoveAt($from); $to = Get-StepIndex @($s) 'Test image verifier'; $s.Insert($to, $item); $w.jobs.'build-push-dev'.steps = @($s) } },
        @{ Name = 'missing-ecr-preflight'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].name = 'Removed preflight' } },
        @{ Name = 'ecr-preflight-shell'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].shell = 'bash' } },
        @{ Name = 'ecr-preflight-no-op'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].run = '$true # no-op' } },
        @{ Name = 'ecr-preflight-scope'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('--repository-names $env:ECR_REPOSITORY', '--repository-names wrong') } },
        @{ Name = 'ecr-preflight-cardinality'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('$repositories.Count -ne 1', '$repositories.Count -lt 1') } },
        @{ Name = 'ecr-preflight-name'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('$repository.repositoryName -cne $env:ECR_REPOSITORY', '$false') } },
        @{ Name = 'ecr-preflight-order'; Apply = { param($w) $s = [System.Collections.ArrayList]@($w.jobs.'build-push-dev'.steps); $from = Get-StepIndex @($s) 'Verify ECR repository'; $item = $s[$from]; $s.RemoveAt($from); $to = (Get-StepIndex @($s) 'Log in to Amazon ECR') + 1; $s.Insert($to, $item); $w.jobs.'build-push-dev'.steps = @($s) } },
        @{ Name = 'ecr-setting-mutation'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify ECR repository'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run) + "`naws ecr put-image-tag-mutability" } },
        @{ Name = 'candidate-tag'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('run-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}', 'candidate') } },
        @{ Name = 'publish-shell'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'; $w.jobs.'build-push-dev'.steps[$i].shell = 'pwsh' } },
        @{ Name = 'publish-no-op'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'; $w.jobs.'build-push-dev'.steps[$i].run = 'true # no-op' } },
        @{ Name = 'commit-tag'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('sha-$GITHUB_SHA', 'snapshot') } },
        @{ Name = 'collision-check'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('existing_commit_digest', 'removed_collision_check') } },
        @{ Name = 'digest-check'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify published image digest'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run).Replace('test "$deployment_digest" = "$CANDIDATE_DIGEST"', 'true') } },
        @{ Name = 'digest-shell'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify published image digest'; $w.jobs.'build-push-dev'.steps[$i].shell = 'pwsh' } },
        @{ Name = 'digest-no-op'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify published image digest'; $w.jobs.'build-push-dev'.steps[$i].run = 'true # no-op' } },
        @{ Name = 'latest-tag'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run) + "`ndocker push `"`$image_uri`:latest`"" } },
        @{ Name = 'static-credential'; Apply = { param($w) $w.env | Add-Member -NotePropertyName AWS_ACCESS_KEY_ID -NotePropertyValue 'fixture' } },
        @{ Name = 'ecs-call'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Verify published image digest'; $w.jobs.'build-push-dev'.steps[$i].run = ([string]$w.jobs.'build-push-dev'.steps[$i].run) + "`naws ecs update-service" } },
        @{ Name = 'unpinned-action'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Check out repository'; $w.jobs.'build-push-dev'.steps[$i].uses = 'actions/checkout@v6' } },
        @{ Name = 'duplicate-known-action'; Apply = { param($w) $i = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Set up .NET SDK'; $w.jobs.'build-push-dev'.steps[$i].uses = 'actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd' } },
        @{ Name = 'unknown-action'; Apply = { param($w) $w.jobs.'build-push-dev'.steps += [pscustomobject]@{ name = 'Unknown'; uses = 'example/action@1111111111111111111111111111111111111111' } } },
        @{ Name = 'second-job-unknown-action'; Apply = { param($w) $w.jobs | Add-Member -NotePropertyName 'unexpected-job' -NotePropertyValue ([pscustomobject]@{ 'runs-on' = 'ubuntu-latest'; steps = @([pscustomobject]@{ name = 'Unknown'; uses = 'example/action@1111111111111111111111111111111111111111' }) }) } },
        @{ Name = 'second-job-unpinned-action'; Apply = { param($w) $w.jobs | Add-Member -NotePropertyName 'unexpected-job' -NotePropertyValue ([pscustomobject]@{ 'runs-on' = 'ubuntu-latest'; steps = @([pscustomobject]@{ name = 'Unpinned'; uses = 'actions/checkout@v6' }) }) } },
        @{ Name = 'second-job-without-action'; Apply = { param($w) $w.jobs | Add-Member -NotePropertyName 'unexpected-job' -NotePropertyValue ([pscustomobject]@{ 'runs-on' = 'ubuntu-latest'; steps = @([pscustomobject]@{ name = 'No action'; run = 'true' }) }) } }
    )

    foreach ($mutation in $mutations) {
        Assert-MutationRejected -Baseline $baseline -Name $mutation.Name -Mutate $mutation.Apply
    }

    Assert-PublishMutationRejected -Baseline $baseline -Name 'deployment-before-collision' -ExpectedCalls $newCommitCalls -ExpectedOutput $expectedPublishOutput -Mutate {
        param($w)
        $index = Get-StepIndex @($w.jobs.'build-push-dev'.steps) 'Publish commit and deployment tags'
        $run = [string]$w.jobs.'build-push-dev'.steps[$index].run
        $originalDeployment = @'
docker tag "$LOCAL_IMAGE" "$image_uri:$DEPLOYMENT_TAG"
docker push "$image_uri:$DEPLOYMENT_TAG"
'@
        $commentDecoys = @'
# docker tag "$LOCAL_IMAGE" "$image_uri:$DEPLOYMENT_TAG"
# docker push "$image_uri:$DEPLOYMENT_TAG"
'@
        $earlyDeployment = @'
deployment_uri="$image_uri:$DEPLOYMENT_TAG"
docker tag "$LOCAL_IMAGE" "$deployment_uri"
docker push "$deployment_uri"

existing_commit_digest=
'@
        $run = $run.Replace($originalDeployment, $commentDecoys)
        $run = $run.Replace('existing_commit_digest=', $earlyDeployment)
        $w.jobs.'build-push-dev'.steps[$index].run = $run
    }

    'All RMC-TotalRisk CWBI API workflow contract and mutation tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
