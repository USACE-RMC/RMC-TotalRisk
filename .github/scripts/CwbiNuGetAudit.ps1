function Assert-CwbiNuGetAuditClean {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$AuditOutput)

    # `dotnet list <project> package --vulnerable` reports only the named project (its transitive
    # PACKAGES are included, its referenced projects are not), so the release path audits each
    # shipped project and hands the concatenated output here; every one of them must report clean.
    $expectedProjects = @('RMC.TotalRisk', 'RMC.TotalRisk.Api', 'RMC.TotalRisk.Tests', 'RMC.TotalRisk.Api.Tests')
    $nonEmptyLines = @($AuditOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($nonEmptyLines.Count -eq 0) {
        throw 'NuGet audit output is empty.'
    }

    $auditText = $nonEmptyLines -join "`n"
    if ($auditText -match '(?i)has the following vulnerable packages') {
        throw 'NuGet reported vulnerable packages.'
    }
    if ($auditText -match '(?i)(?:^|\b)(?:warning|error|failed|failure|unable|NU1900|advisory)(?:\b|:)') {
        throw 'NuGet audit reported a warning, error, or advisory-source failure.'
    }

    $cleanProjects = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $nonEmptyLines) {
        if ($line -notmatch '^The given project `(?<Project>[^`]+)` has no vulnerable packages given the current sources\.$') {
            continue
        }
        $project = [string]$Matches.Project
        if ($expectedProjects -cnotcontains $project) {
            throw "NuGet audit reported an unrecognized project '$project'."
        }
        if (-not $cleanProjects.Add($project)) {
            throw "NuGet audit reported duplicate clean results for project '$project'."
        }
    }

    $missingProjects = @($expectedProjects | Where-Object { -not $cleanProjects.Contains($_) })
    if ($missingProjects.Count -gt 0) {
        throw "NuGet audit is missing recognized clean results for: $($missingProjects -join ', ')."
    }
}
