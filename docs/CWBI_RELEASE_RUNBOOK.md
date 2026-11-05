# Publish `v2.0-development` to CWBI

This is the repeatable process for sending a sanitized copy of `RMC-TotalRisk/v2.0-development` to `cwbi-apps/dst-total-risk`. It mirrors the process used for System-Response (`cwbi-apps/dst-src`) and RasProcessingApi (`cwbi-apps/dst-ras`). Every publish goes to one environment, chosen with `-Environment`:

| `-Environment` | Receiving branch | Workflow that runs | AWS account | ECR tag |
|---|---|---|---|---|
| `test` | `cwbi-test` | `cwbi-build-push-api-test.yml` | `276847049069` | `dst-total-risk:test` |
| `dev` | `cwbi-dev` | `cwbi-build-push-api-dev.yml` | `718787032875` | `dst-total-risk:dev` |

`test` is the normal target. `dev` is kept for the record and for the occasional dev-only push. There is no default: the publisher refuses to run without `-Environment`.

The process runs on your computer. It does not use a GitHub Action to do the publishing and does not require a permanent second worktree. The GitHub Action for the receiving branch takes over after the push: it rebuilds the image, verifies it, and pushes it to the `dst-total-risk` ECR repository in that environment's AWS account with that environment's tag, which is what CWBI's pipeline deploys.

`v2.0-development` is the source branch because it is the home of the v2.0 work until that merges to `main`. When the merge happens, the branch name is pinned in three places: `scripts/Publish-CwbiSnapshot.ps1` (`$originBranch`), the approved-ref list in `scripts/cwbi-release/CwbiRelease.psm1`, and the fixtures in `.github/tests/Prepare-CwbiRelease.Tests.ps1` and `.github/tests/Publish-CwbiSnapshot.Tests.ps1`.

## What each name means

- `v2.0-development`: the approved RMC-TotalRisk code you are publishing.
- `snapshot/sanitized`: a temporary local holding branch. The script creates it and removes it after the run.
- `cwbi-test` / `cwbi-dev`: the receiving branches in `cwbi-apps/dst-total-risk`. Each one has its own build-and-push workflow, and a push to one never runs the other's.
- `cwbi`: the local nickname (Git remote) for `https://github.com/cwbi-apps/dst-total-risk.git`.
- `scripts/cwbi-release/policy.json`: the allowlist of files that may leave this repository. Anything not listed there is never published.

## What is published

The snapshot contains the model library (`src/RMC.TotalRisk/`), the API (`src/RMC.TotalRisk.Api/`), the two fast test projects (`src/RMC.TotalRisk.Tests/`, `src/RMC.TotalRisk.Api.Tests/`, plus the one `MSTestSettings.cs` file they link from the Verification project), the shared build inputs at the repository root (`Directory.Build.props`, `Directory.Packages.props`, `global.json`, `nuget.cwbi.config`, `LICENSE`), the `Dockerfile` and `.dockerignore`, and the release tooling that the CWBI workflow needs (`.github/`). It does not contain the Verification project, the solution file, `docs/`, `examples/`, the perf harness, the root `NuGet.config`, `launchSettings.json`, or `appsettings.Development.json`.

Because the solution file and the Verification project stay behind, every `dotnet` command in the release path targets the two test projects directly; they restore and build the library and the API transitively.

## The Numerics dependency

The library consumes the published `RMC.Numerics` package from nuget.org; the version lives in `Directory.Packages.props`. `nuget.cwbi.config` lists only nuget.org, which is why the Dockerfile, the workflow, and the preparation script restore with it rather than the root `NuGet.config` (which also names a developer-machine feed). A Numerics version change needs its refreshed `packages.lock.json` files: run `dotnet restore --force-evaluate` and commit them with the change.

## One-time setup

You need:

- permission to push to `cwbi-apps/dst-total-risk`;
- Git credentials that work for that repository;
- Git 2.42 or newer, PowerShell 7 (`pwsh`), the .NET 10 SDK, and Docker Desktop;
- network access to nuget.org, `mcr.microsoft.com` (the image build pulls the pinned .NET SDK image) and `cgr.dev` (the runtime base and its packages);
- Docker Desktop running;
- a Chainguard pull token for the `usace-cwbi` organization, logged in with `docker login cgr.dev`. The runtime image builds on CWBI's Chainguard FIPS base image, so the image build and the image-verifier tests cannot pull it without this login. Create the token in the Chainguard console and paste its username and password at the prompts; never store them in the repository. Docker Desktop keeps the login in the Windows credential store, so this is needed once per token lifetime, not per publish.

From `C:\GitHub\RMC-TotalRisk`, verify the repository connections (Git remotes):

```powershell
git remote get-url origin
git remote get-url cwbi
```

Expected:

```text
https://github.com/USACE-RMC/RMC-TotalRisk.git
https://github.com/cwbi-apps/dst-total-risk.git
```

The release safety check requires these HTTPS addresses (an optional `.git` suffix is accepted). If `cwbi` does not exist, add it:

```powershell
git remote add cwbi https://github.com/cwbi-apps/dst-total-risk.git
```

Disable accidental shorthand pushes in this repository (the release safety check requires this exact local setting):

```powershell
git config --local push.default nothing
```

## Every publication

1. Land the approved work on `v2.0-development` on GitHub.

2. Open PowerShell and run:

```powershell
cd C:\GitHub\RMC-TotalRisk
git switch v2.0-development
git status --short --branch
pwsh -NoProfile -File .\scripts\Publish-CwbiSnapshot.ps1 -Environment test
```

Use `-Environment dev` instead only when you deliberately want to update the dev environment.

`git status` should show `v2.0-development` with no changed or untracked files. The publisher stops if the checkout is not clean.

3. Wait while the script performs these six stages:

```text
[1/6] Check the local RMC-TotalRisk checkout
[2/6] Update v2.0-development and read the current CWBI branch
[3/6] Create a disposable local snapshot workspace
[4/6] Sanitize, build, test, scan, and prepare the snapshot
[5/6] Review the prepared result
[6/6] Recheck the destination and publish the exact snapshot
```

The long part is stage 4. It creates the approved file-only copy (sanitized snapshot), scans it and the reachable Git history for secrets, restores and builds it in locked mode, runs the complete `RMC.TotalRisk.Tests` and `RMC.TotalRisk.Api.Tests` suites in Release, audits NuGet dependencies for known vulnerabilities, builds the Docker image, and verifies that image (labels, non-root user, port 8083, path base `/total-risk`, healthcheck, runtime filesystem, and live `/total-risk/health` and `/total-risk/health/detailed` probes).

4. At stage 5, review the three commit IDs and the change summary. The middle line names the branch you chose:

```text
Source v2.0-development: <RMC-TotalRisk commit>
Previous cwbi-test: <current destination commit, or "unborn" if that branch does not exist yet>
Prepared snapshot:       <new sanitized commit>
```

5. If the result is correct, type exactly:

```text
PUBLISH
```

Any other response cancels the publication. Nothing is sent (Git push) before this confirmation.

6. A successful run ends with messages like:

```text
Published and verified cwbi-test at <commit>.
Temporary snapshot workspace removed after publication.
```

The push then starts that branch's workflow in `cwbi-apps/dst-total-risk`. Watch it under the repository's Actions tab. Its first steps re-run the same contract tests, restore, build, tests, audit, image build and image verification; only then does it assume the AWS role and push to ECR.

### The first publication to an environment

`cwbi-test` is created once from the `cwbi-dev` history, so the first test publish is an ordinary forward update. If a receiving branch does not exist at all, the publisher detects that (`Previous cwbi-test: unborn`), builds the snapshot as a root commit, and creates the branch with the push. Nothing else is different.

The first workflow run in an environment is also the first test of the AWS side there. The role name in each workflow file (`github-actions-ecr-dst-total-risk`, in that environment's account), the ECR repository (`dst-total-risk`), and the service port and path base (8083, `/total-risk`) follow the convention used by the other repositories. If the role is wrong, or its trust policy does not list `cwbi-apps/dst-total-risk`, the run fails at "Configure short-lived AWS credentials" with `Not authorized to perform sts:AssumeRoleWithWebIdentity`, before anything is pushed to ECR. That message is what to send to CWBI.

## What the script protects

Before sending anything, the script verifies that:

- you named the environment explicitly;
- you started from a clean `v2.0-development`;
- local `v2.0-development` exactly matches `origin/v2.0-development`;
- `origin` and `cwbi` point to the approved repositories;
- only allowlisted files are in the snapshot, and `launchSettings.json`, `appsettings.Development.json`, the root `NuGet.config`, docs, examples, the Verification project, and the release tooling itself are not;
- no secret-bearing file names or high-signal secret patterns exist in the snapshot or in any commit reachable from `v2.0-development`;
- the temporary snapshot passed all release checks;
- the receiving branch did not change while those checks were running;
- the outgoing update is a normal forward update, never a forced replacement (force-push);
- the destination commit after the push is the exact prepared snapshot.

If any check fails, the script stops without pushing and removes its temporary workspace when it can do so safely. Each successful preparation also writes a JSON evidence file (source commit, tree, policy digest, and the full file manifest with hashes) to `C:\GitHub\cwbi-release-evidence`, outside this repository.

## Validate without publishing

To run the full preparation and review but never receive a publication prompt, use:

```powershell
pwsh -NoProfile -File .\scripts\Publish-CwbiSnapshot.ps1 -Environment test -PrepareOnly
```

This still performs the full build, tests, scans, audit, and Docker verification. It then removes the temporary workspace.

## Running the release tests on their own

The publisher runs these itself, but they can be run directly while changing the tooling:

```powershell
pwsh -NoProfile -File .\.github\tests\Prepare-CwbiRelease.Tests.ps1
pwsh -NoProfile -File .\.github\tests\Publish-CwbiSnapshot.Tests.ps1
pwsh -NoProfile -File .\.github\tests\CwbiApiWorkflow.Tests.ps1
pwsh -NoProfile -File .\.github\tests\Verify-TotalRiskImage.Tests.ps1
```

The last two need Docker. Run `Prepare-CwbiRelease.Tests.ps1` from PowerShell, not Git Bash: Git Bash's `tar` cannot read Windows paths and the archive step fails.

## Common stops

| Message | What to do |
|---|---|
| Prompt for `Environment`, or `does not belong to the set "dev,test"` | Pass `-Environment test` or `-Environment dev` (lowercase). |
| `Switch to v2.0-development before publishing` | Run `git switch v2.0-development`, then start again. |
| `checkout must be clean` | Commit, stash, or remove the reported local changes. |
| `Repository-local push.default must be exactly nothing` | Run `git config --local push.default nothing` (see One-time setup). |
| `snapshot/sanitized already exists` | Run `git worktree list`. If no worktree uses it and it is only a failed temporary branch, run `git branch -D snapshot/sanitized`. |
| `cwbi-test changed while ... prepared` (or `cwbi-dev`) | Someone updated the destination. Start the publisher again so it uses the new version. |
| `NuGet reported vulnerable packages` | A dependency has a known advisory. Upgrade it on `v2.0-development` first; the audit is a hard gate. |
| `non-allowlisted paths` | A new file or folder was added inside a shipped directory that the policy does not cover, or a new root file is needed by the build. Add it to `scripts/cwbi-release/policy.json` if it belongs in the deployment, or move it. |
| `NU1004` or `locked-mode` restore failure | A package version moved without its `packages.lock.json`. Run `dotnet restore --force-evaluate` on `v2.0-development`, review the lock-file diff, and commit it. |
| Authentication or `403` error | Your current Git credentials cannot push to `cwbi-apps/dst-total-risk`; resolve access with that repository's administrators. |
| Docker connection error | Start Docker Desktop, wait until it is ready, and run the publisher again. |
| `cgr.dev/usace-cwbi/chainguard-base-fips` pull is unauthorized or not found | Your Chainguard login is missing or the pull token expired. Create a new pull token, run `docker login cgr.dev`, and run the publisher again. |
