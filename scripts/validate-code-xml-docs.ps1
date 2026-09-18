param(
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$failures = New-Object System.Collections.Generic.List[string]

function Get-RelativePath([string]$Path) {
    $root = (Resolve-Path $repoRoot).Path.TrimEnd('\', '/')
    $resolved = (Resolve-Path $Path).Path
    if ($resolved.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $resolved.Substring($root.Length).TrimStart('\', '/').Replace("\", "/")
    }

    return $resolved.Replace("\", "/")
}

function Add-Failure([string]$Message) {
    [void]$failures.Add($Message)
}

$codeRoots = @(
    "src/RMC.TotalRisk",
    "src/RMC.TotalRisk.Tests",
    "src/RMC.TotalRisk.Verification",
    "src/RMC.TotalRisk.Api",
    "src/RMC.TotalRisk.Api.Tests"
)

$sourceFiles = @(foreach ($root in $codeRoots) {
    $path = Join-Path $repoRoot $root
    if (Test-Path $path) {
        Get-ChildItem -Path $path -Recurse -Filter *.cs -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
})

# Every public reference type receives a discoverable, dedicated unit-test file. This is a
# filename gate, not a coverage substitute: <TypeName>Tests.cs makes a new public API omission
# fail during the same mandatory validation command that checks documentation and dependencies.
$librarySourceFiles = @($sourceFiles | Where-Object {
    (Get-RelativePath $_.FullName).StartsWith("src/RMC.TotalRisk/", [System.StringComparison]::OrdinalIgnoreCase)
})
$testTypeNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$testRoot = Join-Path $repoRoot "src/RMC.TotalRisk.Tests"
if (Test-Path $testRoot) {
    Get-ChildItem -Path $testRoot -Recurse -Filter *Tests.cs -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { [void]$testTypeNames.Add($_.BaseName) }
}

$publicClassDeclaration = [regex]'^\s*public\s+(?:(?:abstract|sealed|static|partial|readonly)\s+)*(?:class|record(?!\s+struct)(?:\s+class)?)\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)'
foreach ($file in $librarySourceFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $match = $publicClassDeclaration.Match($lines[$i])
        if (-not $match.Success) {
            continue
        }
        $typeName = $match.Groups["Name"].Value
        if (-not $testTypeNames.Contains("${typeName}Tests")) {
            $relative = Get-RelativePath $file.FullName
            Add-Failure "${relative}:$($i + 1) declares public class '$typeName' without a matching ${typeName}Tests.cs in RMC.TotalRisk.Tests."
        }
    }
}

$exactRootNamespace = [regex]'^\s*namespace\s+RMC\.TotalRisk\s*(?:[;{]\s*)?$'
$rootUsing = [regex]'^\s*using\s+RMC\.TotalRisk\s*;'
$legacyFlatNamespace = [regex]'^\s*(?:namespace|using)\s+TotalRisk\s*[;{]?\s*$'
$retiredModelsNamespace = [regex]'^\s*(?:namespace|using)\s+RMC\.TotalRisk\.Models'
$bestFitReference = [regex]'\bRMC\.BestFit\b'
$uiFrameworkReference = [regex]'\bSystem\.Windows\b'
$sqliteReference = [regex]'SQLite'
$cultureLessTryParse = [regex]'double\.TryParse\((?![^)]*InvariantCulture)'
$cultureLessG17 = [regex]'\.ToString\("G17"\)'

if ($sourceFiles.Count -gt 0) {
    $combinedPattern = '^\s*namespace\s+RMC\.TotalRisk\s*(?:[;{]\s*)?$|^\s*using\s+RMC\.TotalRisk\s*;|^\s*(?:namespace|using)\s+RMC\.TotalRisk\.Models|^\s*(?:namespace|using)\s+TotalRisk\s*[;{]?\s*$|\bRMC\.BestFit\b|\bSystem\.Windows\b|SQLite|double\.TryParse\(|\.ToString\("G17"\)'
    $matches = Select-String -Path ($sourceFiles | Select-Object -ExpandProperty FullName) -Pattern $combinedPattern

    foreach ($match in $matches) {
        $line = $match.Line
        $relative = Get-RelativePath $match.Path
        $lineNumber = $match.LineNumber

        if ($exactRootNamespace.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber uses the bare root namespace RMC.TotalRisk; use a concrete namespace (e.g., RMC.TotalRisk.Core)."
        }
        elseif ($rootUsing.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber imports the bare root namespace RMC.TotalRisk; use the concrete namespace instead."
        }
        elseif ($retiredModelsNamespace.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber uses the retired RMC.TotalRisk.Models namespace; the v0.25 layout is Core[.Support/.Enums/.Interfaces] / RiskFunctions.* / Systems.* / Analyses.{Risk,CostBenefit,LifeCycle,Support} / Results.{Risk,CostBenefit,LifeCycle,Support}."
        }
        elseif ($legacyFlatNamespace.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber references the legacy flat TotalRisk namespace; porting must rename all namespaces to RMC.TotalRisk.*."
        }
        elseif ($bestFitReference.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber references RMC.BestFit; the model library depends only on RMC.Numerics (BestFit results are imported as Numerics artifacts)."
        }
        elseif ($uiFrameworkReference.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber references System.Windows; the model library is headless (no UI frameworks)."
        }
        elseif ($sqliteReference.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber references SQLite; persistence is a caller concern (XElement round-trip only in the model library)."
        }
        elseif ($cultureLessTryParse.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber parses a double without CultureInfo.InvariantCulture; serialization must be culture-invariant."
        }
        elseif ($cultureLessG17.IsMatch($line)) {
            Add-Failure "${relative}:$lineNumber formats G17 without CultureInfo.InvariantCulture; use ToString(""G17"", CultureInfo.InvariantCulture)."
        }
    }
}

# Folders mirror namespaces exactly: every folder is a namespace segment, so a file's declared
# namespace must equal its project's root namespace plus its folder path. The Api projects are
# excluded — the Api DTO folders deliberately flatten into RMC.TotalRisk.Api.DTOs.
$mirrorRoots = [ordered]@{
    "src/RMC.TotalRisk"              = "RMC.TotalRisk"
    "src/RMC.TotalRisk.Tests"        = "RMC.TotalRisk.Tests"
    "src/RMC.TotalRisk.Verification" = "RMC.TotalRisk.Verification"
}
$namespaceDeclaration = [regex]'^\s*namespace\s+(?<Name>[A-Za-z0-9_.]+)'
foreach ($file in $sourceFiles) {
    $relative = Get-RelativePath $file.FullName
    $rootPrefix = $null
    foreach ($key in $mirrorRoots.Keys) {
        if ($relative.StartsWith("$key/", [System.StringComparison]::OrdinalIgnoreCase)) {
            $rootPrefix = $key
            break
        }
    }
    if ($null -eq $rootPrefix) {
        continue
    }
    $relativeDir = [System.IO.Path]::GetDirectoryName($relative.Substring($rootPrefix.Length + 1))
    if ($null -ne $relativeDir) {
        $relativeDir = $relativeDir.Replace("\", "/")
    }
    if ($relativeDir -eq "Properties") {
        continue
    }
    $expected = $mirrorRoots[$rootPrefix]
    if ($relativeDir) {
        $expected = "$expected." + ($relativeDir -replace "/", ".")
    }
    $declared = $null
    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        $m = $namespaceDeclaration.Match($line)
        if ($m.Success) {
            $declared = $m.Groups["Name"].Value
            break
        }
    }
    if ($null -eq $declared) {
        continue # assembly-attribute-only files (AssemblyInfo, MSTestSettings)
    }
    if ($declared -ne $expected) {
        Add-Failure "${relative}: declares namespace '$declared' but folders mirror namespaces exactly; expected '$expected'."
    }
}

function Test-HasXmlDocumentation($lines, [int]$index) {
    $j = $index - 1
    while ($j -ge 0 -and [string]::IsNullOrWhiteSpace($lines[$j])) {
        $j--
    }

    if ($j -ge 0 -and $lines[$j].Trim().EndsWith("]")) {
        $depth = 0
        while ($j -ge 0) {
            $trimmed = $lines[$j].Trim()
            if ($trimmed.EndsWith("]")) {
                $depth++
            }

            if ($trimmed.StartsWith("[")) {
                $depth--
            }

            $j--
            while ($j -ge 0 -and
                ([string]::IsNullOrWhiteSpace($lines[$j]) -or
                ($lines[$j].TrimStart().StartsWith("//") -and -not $lines[$j].TrimStart().StartsWith("///")))) {
                $j--
            }

            if ($depth -le 0 -and ($j -lt 0 -or -not $lines[$j].Trim().EndsWith("]"))) {
                break
            }
        }
    }

    return ($j -ge 0 -and $lines[$j].TrimStart().StartsWith("///"))
}

$documentedMemberFiles = @($sourceFiles | Where-Object {
    $_.Name -notmatch '\.(Designer|g|g\.i)\.cs$'
})
$typeDeclaration = [regex]'^[\s]*(?:public|internal|private|protected|file)[\s]+(?:(?:abstract|sealed|static|partial|readonly|unsafe|new|ref)[\s]+)*(class|struct|interface|enum|record)[\s]+[A-Za-z_][A-Za-z0-9_]*'
$methodDeclaration = [regex]'^[\s]*(?:public|internal|private|protected)[\s]+(?:(?:static|virtual|override|abstract|async|extern|unsafe|sealed|new|partial)[\s]+)*(?!class\b|struct\b|interface\b|enum\b|record\b|delegate\b|event\b)(?:[A-Za-z_][A-Za-z0-9_<>\[\],.?]*(?:[\s]+|[\*&]+[\s]*))+[A-Za-z_][A-Za-z0-9_]*[\s]*(?:<[^>]+>)?[\s]*\('

foreach ($file in $documentedMemberFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if (($typeDeclaration.IsMatch($line) -or $methodDeclaration.IsMatch($line)) -and
            -not (Test-HasXmlDocumentation $lines $i)) {
            $relative = Get-RelativePath $file.FullName
            Add-Failure "${relative}:$($i + 1) is missing XML documentation for a class or method declaration."
        }
    }
}

# The Authors attribution block is required in every type-bearing library and Verification
# source file (test classes are exempt by policy; CLAUDE.md "Critical Quality Standards").
# Its absence fails the same mandatory gate as the rest of the documentation standard.
$authorsRequiredFiles = @($sourceFiles | Where-Object {
    $relative = Get-RelativePath $_.FullName
    ($relative.StartsWith("src/RMC.TotalRisk/", [System.StringComparison]::OrdinalIgnoreCase) -or
        $relative.StartsWith("src/RMC.TotalRisk.Verification/", [System.StringComparison]::OrdinalIgnoreCase)) -and
        $_.Name -notmatch '(AssemblyInfo|MSTestSettings|GlobalUsings)'
})
foreach ($file in $authorsRequiredFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $hasTypeDeclaration = $false
    foreach ($line in $lines) {
        if ($typeDeclaration.IsMatch($line)) {
            $hasTypeDeclaration = $true
            break
        }
    }
    if ($hasTypeDeclaration -and -not ($lines | Where-Object { $_.Contains("Authors:") })) {
        $relative = Get-RelativePath $file.FullName
        Add-Failure "${relative} is missing the required Authors block in its type-level <remarks>."
    }
}

# Phase and process language is confined to the planning docs (CLAUDE.md): phase numbers,
# ratification narration, decision numbers, landing narration, planning-doc references, and
# retired internal history vocabulary must not appear in source files. Comments describe the
# software as it is.
$processLanguagePatterns = @(
    @{ Pattern = '(?i)\bphase\s+[0-9]'; Reason = "a phase number" },
    @{ Pattern = '(?i)\bratified\b'; Reason = "ratification narration" },
    @{ Pattern = '(?i)\bdecision[- ][0-9]'; Reason = "a design-decision number" },
    @{ Pattern = '(?i)pre-cascade'; Reason = "retired internal history vocabulary (pre-cascade)" },
    @{ Pattern = '(?i)\blanded\b'; Reason = "landing narration" },
    @{ Pattern = 'ROADMAP\.md|PROGRESS\.md'; Reason = "a planning-doc reference" }
)
foreach ($file in $sourceFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($entry in $processLanguagePatterns) {
            if ($lines[$i] -match $entry.Pattern) {
                $relative = Get-RelativePath $file.FullName
                Add-Failure "${relative}:$($i + 1) contains $($entry.Reason); phase and process language lives only in the planning docs."
                break
            }
        }
    }
}

# The technical-reference and verification pages describe the software as it is. Dated
# runs-of-record and planning-doc links stay legitimate there, so the page deny-list is the
# narrower subset: phase numbers, ratification narration, decision numbers, and retired
# history vocabulary.
$docPagePatterns = @($processLanguagePatterns | Where-Object {
    $_.Reason -notin @("landing narration", "a planning-doc reference")
})
$docPageFiles = @(foreach ($root in @("docs/technical-reference", "docs/verification")) {
    $path = Join-Path $repoRoot $root
    if (Test-Path $path) {
        Get-ChildItem -Path $path -Recurse -Filter *.md -File
    }
})
foreach ($extraDoc in @("docs/verification.md", "docs/index.md")) {
    $path = Join-Path $repoRoot $extraDoc
    if (Test-Path $path) {
        $docPageFiles += Get-Item $path
    }
}
foreach ($file in $docPageFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($entry in $docPagePatterns) {
            if ($lines[$i] -match $entry.Pattern) {
                $relative = Get-RelativePath $file.FullName
                Add-Failure "${relative}:$($i + 1) contains $($entry.Reason); these pages describe the software as it is."
                break
            }
        }
    }
}

$traceabilityValidator = Join-Path $PSScriptRoot "validate-verification-traceability.ps1"
try {
    & $traceabilityValidator
}
catch {
    Add-Failure "Verification traceability validation failed:$([Environment]::NewLine)$($_.Exception.Message)"
}

if (-not $SkipBuild) {
    $projects = @(
        "src/RMC.TotalRisk/RMC.TotalRisk.csproj",
        "src/RMC.TotalRisk.Tests/RMC.TotalRisk.Tests.csproj",
        "src/RMC.TotalRisk.Verification/RMC.TotalRisk.Verification.csproj",
        "src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj",
        "src/RMC.TotalRisk.Api.Tests/RMC.TotalRisk.Api.Tests.csproj"
    )

    foreach ($project in $projects) {
        $projectPath = Join-Path $repoRoot $project
        if (-not (Test-Path $projectPath)) {
            Add-Failure "Missing project: $project"
            continue
        }

        # --no-incremental is load-bearing: an incremental enforced build can consider the
        # docs-off outputs up to date, skip compilation entirely, and silently pass a tree
        # carrying CS1574 errors. The documentation gate must always recompile.
        $args = @(
            "build",
            $projectPath,
            "-c",
            $Configuration,
            "--no-restore",
            "--no-dependencies",
            "--no-incremental",
            "-p:EnforceXmlDocumentation=true",
            "-p:UseSharedCompilation=false",
            "-v:minimal"
        )

        & dotnet @args
        if ($LASTEXITCODE -ne 0) {
            Add-Failure "XML documentation build failed for $project."
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Code namespace, public-class test coverage, XML documentation, Authors-block, and process-language validation passed."
