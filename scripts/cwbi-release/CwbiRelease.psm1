Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:CwbiTestFaultPoint = $null
$script:CwbiTestFaultAction = $null
$script:CwbiRequiredSecretBearingFileExtensions = @(
    '.cer', '.crt', '.der', '.jks', '.key', '.keystore', '.p12', '.pem', '.pfx'
)
$script:CwbiRequiredSecretBearingFileNames = @(
    'id_dsa', 'id_ecdsa', 'id_ed25519', 'id_rsa'
)
$nuGetAuditHelperPath = Join-Path $PSScriptRoot '..\..\.github\scripts\CwbiNuGetAudit.ps1'
if (-not (Test-Path -LiteralPath $nuGetAuditHelperPath -PathType Leaf)) {
    throw 'The shared CWBI NuGet audit helper is missing.'
}
. $nuGetAuditHelperPath

function Invoke-CwbiTestFault {
    param([Parameter(Mandatory)][string]$Point)

    if ($script:CwbiTestFaultAction) {
        & $script:CwbiTestFaultAction $Point
    }
    if ($script:CwbiTestFaultPoint -ceq $Point) {
        throw "Injected CWBI test failure $Point."
    }
}

function Invoke-CwbiGit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$HooksDirectory,
        [switch]$AllowFailure
    )

    $repositoryPath = [System.IO.Path]::GetFullPath($Repository)
    $safePath = $repositoryPath.Replace('\', '/')
    $gitConfiguration = @('-c', "safe.directory=$safePath", '-c', 'core.excludesFile=')
    if ($HooksDirectory) {
        $hooksPath = (Resolve-Path -LiteralPath $HooksDirectory).Path.Replace('\', '/')
        $gitConfiguration += @('-c', "core.hooksPath=$hooksPath", '-c', 'commit.gpgSign=false')
    }

    $output = & git @gitConfiguration -C $repositoryPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed in $repositoryPath."
    }

    [pscustomobject]@{ ExitCode = $exitCode; Output = @($output) }
}

function Invoke-CwbiGitBytes {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $repositoryPath = [System.IO.Path]::GetFullPath($Repository)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @(
        '-c',
        "safe.directory=$($repositoryPath.Replace('\', '/'))",
        '-c',
        'core.excludesFile=',
        '-C',
        $repositoryPath
    ) + $Arguments) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $memory = [System.IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "git $($Arguments -join ' ') failed in $repositoryPath. $standardError"
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = [byte[]]$memory.ToArray()
            StandardError = $standardError
        }
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Get-CwbiTarPath {
    # On Windows prefer the bsdtar shipped in System32: when this module runs from a Git Bash
    # session, a bare `tar.exe` resolves to GNU tar, which treats `C:` as a remote host name.
    if ($IsWindows) {
        $systemTar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (Test-Path -LiteralPath $systemTar -PathType Leaf) {
            return $systemTar
        }
    }
    return 'tar'
}

function Get-CwbiGitObjectBytes {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ObjectId
    )

    return [byte[]](Invoke-CwbiGitBytes -Repository $Repository -Arguments @('cat-file', 'blob', $ObjectId)).Output
}

function ConvertTo-CwbiDiagnosticPath {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Path)

    return [System.Text.Json.JsonEncodedText]::Encode($Path).ToString()
}

function Assert-CwbiRelativePathSafe {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [switch]$Directory
    )

    $diagnosticPath = ConvertTo-CwbiDiagnosticPath -Path $Path
    if ($Directory -and -not $Path.EndsWith('/', [StringComparison]::Ordinal)) {
        throw "$Description directory '$diagnosticPath' must end with '/'."
    }
    if (-not $Directory -and $Path.EndsWith('/', [StringComparison]::Ordinal)) {
        throw "$Description file '$diagnosticPath' must not end with '/'."
    }

    $candidate = if ($Directory) { $Path.Substring(0, $Path.Length - 1) } else { $Path }
    $segments = @($candidate.Split('/', [StringSplitOptions]::None))
    $unsafe = [string]::IsNullOrWhiteSpace($candidate) -or
        [System.IO.Path]::IsPathRooted($candidate) -or
        $candidate.StartsWith('/', [StringComparison]::Ordinal) -or
        $candidate.Contains('\', [StringComparison]::Ordinal) -or
        $candidate.Contains(':', [StringComparison]::Ordinal) -or
        -not $candidate.IsNormalized([System.Text.NormalizationForm]::FormC) -or
        $segments.Count -eq 0

    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or
            $segment -in @('.', '..') -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment -match '[<>:"\\|?*]' -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]|CONIN\$|CONOUT\$)(?:\..*)?$') {
            $unsafe = $true
            break
        }
        foreach ($character in $segment.ToCharArray()) {
            if ([char]::IsControl($character)) {
                $unsafe = $true
                break
            }
        }
        if ($unsafe) {
            break
        }
    }

    if ($unsafe) {
        throw "$Description contains an unsafe path that is non-portable on NTFS and Linux: '$diagnosticPath'."
    }
}

function Get-CwbiRawTreeEntries {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Revision
    )

    $bytes = [byte[]](Invoke-CwbiGitBytes -Repository $Repository -Arguments @('ls-tree', '-r', '-z', '--full-tree', $Revision)).Output
    $entries = [System.Collections.Generic.List[object]]::new()
    $recordStart = 0
    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)

    for ($index = 0; $index -lt $bytes.Length; $index++) {
        if ($bytes[$index] -ne 0) {
            continue
        }
        if ($index -eq $recordStart) {
            throw 'Git returned an empty raw tree entry.'
        }

        $tabIndex = -1
        for ($search = $recordStart; $search -lt $index; $search++) {
            if ($bytes[$search] -eq 9) {
                $tabIndex = $search
                break
            }
        }
        if ($tabIndex -lt 0) {
            throw 'Git returned a malformed raw tree entry.'
        }

        $metadataBytes = [byte[]]::new($tabIndex - $recordStart)
        [Array]::Copy($bytes, $recordStart, $metadataBytes, 0, $metadataBytes.Length)
        $metadata = [System.Text.Encoding]::ASCII.GetString($metadataBytes)
        if ($metadata -notmatch '^(?<mode>[0-9]{6}) (?<type>[a-z]+) (?<object>[0-9a-f]{40})$') {
            throw 'Git returned malformed raw tree metadata.'
        }
        $entryMode = $Matches.mode
        $entryType = $Matches.type
        $entryObject = $Matches.object

        $pathLength = $index - $tabIndex - 1
        if ($pathLength -le 0) {
            throw 'Git returned an empty raw tree path.'
        }
        $pathBytes = [byte[]]::new($pathLength)
        [Array]::Copy($bytes, $tabIndex + 1, $pathBytes, 0, $pathLength)
        try {
            $path = $strictUtf8.GetString($pathBytes)
        }
        catch {
            throw 'The source commit contains a raw tree path that is not valid UTF-8 and is non-portable.'
        }

        $entries.Add([pscustomobject]@{
            Mode = $entryMode
            Type = $entryType
            ObjectId = $entryObject
            Path = $path
        })
        $recordStart = $index + 1
    }

    if ($recordStart -ne $bytes.Length) {
        throw 'Git raw tree output was not NUL terminated.'
    }
    return @($entries)
}

function Assert-CwbiTreeEntriesPortable {
    param([Parameter(Mandatory)][object[]]$Entries)

    $portablePaths = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Entries) {
        if ($entry.Mode -cne '100644' -or $entry.Type -cne 'blob') {
            throw 'The source commit contains symlinks, submodules, or executable/non-regular modes that the release exporter does not permit.'
        }
        Assert-CwbiRelativePathSafe -Path ([string]$entry.Path) -Description 'Source tree'
        if ($portablePaths.ContainsKey([string]$entry.Path)) {
            $firstPath = ConvertTo-CwbiDiagnosticPath -Path ([string]$portablePaths[[string]$entry.Path])
            $secondPath = ConvertTo-CwbiDiagnosticPath -Path ([string]$entry.Path)
            throw "The source commit contains case-colliding or otherwise non-unique portable paths: '$firstPath', '$secondPath'."
        }
        $portablePaths.Add([string]$entry.Path, [string]$entry.Path)
    }
}

function Resolve-CwbiExactApprovedCommit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$SourceRevision
    )

    if ($SourceRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'SourceRevision must be an explicit 40-character commit id.'
    }
    $objectType = Invoke-CwbiGit -Repository $Repository -Arguments @('cat-file', '-t', $SourceRevision) -AllowFailure
    if ($objectType.ExitCode -ne 0) {
        throw "SourceRevision does not identify an existing Git object: $SourceRevision."
    }
    if (([string]$objectType.Output[0]).Trim() -cne 'commit') {
        throw 'SourceRevision must identify the supplied commit object directly; annotated-tag object SHAs are not accepted.'
    }

    $sourceCommit = ([string](Invoke-CwbiGit -Repository $Repository -Arguments @('rev-parse', '--verify', $SourceRevision)).Output[0]).Trim()
    if ($sourceCommit -ine $SourceRevision) {
        throw 'SourceRevision did not resolve to the exact supplied commit id.'
    }

    $approved = $false
    foreach ($approvedRef in @('refs/heads/v2.0-development', 'refs/remotes/origin/v2.0-development')) {
        $refExists = Invoke-CwbiGit -Repository $Repository -Arguments @('show-ref', '--verify', '--quiet', $approvedRef) -AllowFailure
        if ($refExists.ExitCode -ne 0) {
            continue
        }
        $ancestor = Invoke-CwbiGit -Repository $Repository -Arguments @('merge-base', '--is-ancestor', $sourceCommit, $approvedRef) -AllowFailure
        if ($ancestor.ExitCode -eq 0) {
            $approved = $true
            break
        }
        if ($ancestor.ExitCode -ne 1) {
            throw "Unable to establish source reachability from approved v2.0-development ref $approvedRef."
        }
    }
    if (-not $approved) {
        throw 'SourceRevision is not reachable from an approved v2.0-development ref (refs/heads/v2.0-development or refs/remotes/origin/v2.0-development).'
    }
    return $sourceCommit.ToLowerInvariant()
}

function Assert-CwbiSourceCheckoutIdentity {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit
    )

    $headCommit = ([string](Invoke-CwbiGit -Repository $Repository -Arguments @('rev-parse', 'HEAD')).Output[0]).Trim()
    if ($headCommit -cne $SourceCommit) {
        throw "Source checkout HEAD does not equal SourceRevision: HEAD is $headCommit."
    }
    $status = @((Invoke-CwbiGit -Repository $Repository -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output)
    if ($status.Count -gt 0) {
        throw 'Source checkout is not clean for non-ignored files.'
    }
}

function Get-CwbiBytesSha256 {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)

    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Assert-CwbiPolicyDigest {
    param(
        [Parameter(Mandatory)][string]$PolicyPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedPolicySha256
    )

    $resolvedPath = (Resolve-Path -LiteralPath $PolicyPath).Path
    $actualPolicySha256 = Get-CwbiBytesSha256 -Bytes ([System.IO.File]::ReadAllBytes($resolvedPath))
    if ($actualPolicySha256 -cne $ExpectedPolicySha256) {
        throw 'Pinned CWBI release policy changed after its initial digest was recorded.'
    }
}

function Assert-CwbiPolicyLiteralSet {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Values,
        [Parameter(Mandatory)][string[]]$RequiredValues,
        [Parameter(Mandatory)][string]$Description,
        [switch]$Extension
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $previous = $null
    foreach ($entry in $Values) {
        $value = [string]$entry
        $valid = if ($Extension) {
            $value -match '^\.[a-z0-9]+$'
        }
        else {
            $value -match '^[a-z0-9][a-z0-9._-]*$'
        }
        if (-not $valid -or $value -cne $value.ToLowerInvariant()) {
            throw "$Description contains invalid literal '$([System.Text.Json.JsonEncodedText]::Encode($value))'."
        }
        if (-not $seen.Add($value)) {
            throw "$Description contains duplicate literal '$value'."
        }
        if ($null -ne $previous -and [StringComparer]::Ordinal.Compare([string]$previous, $value) -ge 0) {
            throw "$Description must be strictly ordinal-sorted."
        }
        $previous = $value
    }
    foreach ($requiredValue in $RequiredValues) {
        if (-not $seen.Contains($requiredValue)) {
            throw "$Description is missing required literal '$requiredValue'."
        }
    }
}

function Get-CwbiReleasePolicy {
    param(
        [Parameter(Mandatory)][string]$PolicyPath,
        [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedPolicySha256
    )

    $resolvedPath = (Resolve-Path -LiteralPath $PolicyPath).Path
    $policyBytes = [System.IO.File]::ReadAllBytes($resolvedPath)
    $policySha256 = Get-CwbiBytesSha256 -Bytes $policyBytes
    if ($ExpectedPolicySha256 -and $policySha256 -cne $ExpectedPolicySha256) {
        throw 'Pinned CWBI release policy changed after its initial digest was recorded.'
    }
    try {
        $policyText = [System.Text.UTF8Encoding]::new($false, $true).GetString($policyBytes).TrimStart([char]0xFEFF)
        $policy = $policyText | ConvertFrom-Json
    }
    catch {
        throw "CWBI release policy is not valid UTF-8 JSON: $($_.Exception.Message)"
    }
    foreach ($property in @(
        'schemaVersion',
        'allowedFiles',
        'allowedDirectories',
        'excludedPaths',
        'requiredFiles',
        'secretScanExcludedFiles',
        'secretBearingFileExtensions',
        'secretBearingFileNames',
        'generatedOutputExclusion'
    )) {
        if ($null -eq $policy.PSObject.Properties[$property]) {
            throw "CWBI release policy is missing '$property'."
        }
    }
    if ([int]$policy.schemaVersion -ne 1) {
        throw "Unsupported CWBI release policy schema version '$($policy.schemaVersion)'."
    }
    if ([string]$policy.generatedOutputExclusion -cne 'test_scenarios/**/csharp/output.json') {
        throw 'generatedOutputExclusion must be the pinned test_scenarios/**/csharp/output.json rule.'
    }

    foreach ($path in @($policy.allowedFiles)) {
        Assert-CwbiRelativePathSafe -Path ([string]$path) -Description 'allowedFiles'
    }
    foreach ($path in @($policy.allowedDirectories)) {
        Assert-CwbiRelativePathSafe -Path ([string]$path) -Description 'allowedDirectories' -Directory
    }
    foreach ($property in @('excludedPaths', 'requiredFiles', 'secretScanExcludedFiles')) {
        foreach ($path in @($policy.$property)) {
            Assert-CwbiRelativePathSafe -Path ([string]$path) -Description $property
        }
    }
    Assert-CwbiPolicyLiteralSet `
        -Values @($policy.secretBearingFileExtensions) `
        -RequiredValues $script:CwbiRequiredSecretBearingFileExtensions `
        -Description 'secretBearingFileExtensions' `
        -Extension
    Assert-CwbiPolicyLiteralSet `
        -Values @($policy.secretBearingFileNames) `
        -RequiredValues $script:CwbiRequiredSecretBearingFileNames `
        -Description 'secretBearingFileNames'

    return $policy
}

function ConvertTo-CwbiRelativePath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Test-CwbiPathAllowed {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)]$Policy
    )

    $normalizedPath = $RelativePath.Replace('\', '/')
    if ($normalizedPath.StartsWith('test_scenarios/', [StringComparison]::Ordinal) -and
        $normalizedPath.EndsWith('/csharp/output.json', [StringComparison]::Ordinal)) {
        return $false
    }
    if (@($Policy.excludedPaths) -ccontains $normalizedPath) {
        return $false
    }
    if (@($Policy.allowedFiles) -ccontains $normalizedPath) {
        return $true
    }
    foreach ($directory in @($Policy.allowedDirectories)) {
        if ($normalizedPath.StartsWith([string]$directory, [StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Test-CwbiSecretBearingPath {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)]$Policy
    )

    $leafName = [System.IO.Path]::GetFileName($RelativePath.Replace('\', '/')).ToLowerInvariant()
    $extension = [System.IO.Path]::GetExtension($leafName)
    return (@($Policy.secretBearingFileExtensions) -ccontains $extension) -or
        (@($Policy.secretBearingFileNames) -ccontains $leafName)
}

function Assert-CwbiSecretBearingPathsAbsent {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$RelativePaths,
        [Parameter(Mandatory)]$Policy,
        [string]$Context = 'CWBI snapshot'
    )

    $forbiddenPaths = @(
        $RelativePaths |
            Where-Object { Test-CwbiSecretBearingPath -RelativePath $_ -Policy $Policy } |
            Sort-Object -CaseSensitive -Unique
    )
    if ($forbiddenPaths.Count -gt 0) {
        $diagnosticPaths = @($forbiddenPaths | ForEach-Object { ConvertTo-CwbiDiagnosticPath -Path $_ })
        throw "$Context contains secret-bearing path(s) (path only): $($diagnosticPaths -join ', ')."
    }
}

function Get-CwbiFiles {
    param([Parameter(Mandatory)][string]$Root)

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    return @(
        Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force |
            Where-Object { (ConvertTo-CwbiRelativePath -Root $resolvedRoot -Path $_.FullName) -cne '.git' }
    )
}

function Assert-CwbiReleasePathPolicy {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$PolicyPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedPolicySha256
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $policy = Get-CwbiReleasePolicy -PolicyPath $PolicyPath -ExpectedPolicySha256 $ExpectedPolicySha256
    $snapshotPaths = @(
        Get-CwbiFiles -Root $resolvedRoot |
            ForEach-Object { ConvertTo-CwbiRelativePath -Root $resolvedRoot -Path $_.FullName }
    )
    Assert-CwbiSecretBearingPathsAbsent -RelativePaths $snapshotPaths -Policy $policy
    $unexpectedPaths = @(
        $snapshotPaths |
            Where-Object { -not (Test-CwbiPathAllowed -RelativePath $_ -Policy $policy) }
    )
    if ($unexpectedPaths.Count -gt 0) {
        $diagnosticPaths = @($unexpectedPaths | ForEach-Object { ConvertTo-CwbiDiagnosticPath -Path ([string]$_) })
        throw "CWBI snapshot contains non-allowlisted paths: $($diagnosticPaths -join ', ')."
    }

    $missingPaths = @(
        @($policy.requiredFiles) |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $resolvedRoot $_) -PathType Leaf) }
    )
    if ($missingPaths.Count -gt 0) {
        $diagnosticPaths = @($missingPaths | ForEach-Object { ConvertTo-CwbiDiagnosticPath -Path ([string]$_) })
        throw "CWBI snapshot is missing required paths: $($diagnosticPaths -join ', ')."
    }

    foreach ($excludedPath in @($policy.excludedPaths)) {
        if (Test-Path -LiteralPath (Join-Path $resolvedRoot $excludedPath)) {
            $diagnosticPath = ConvertTo-CwbiDiagnosticPath -Path ([string]$excludedPath)
            throw "CWBI snapshot contains excluded path '$diagnosticPath'."
        }
    }
}

function Test-CwbiTextFile {
    param([Parameter(Mandatory)][string]$Path)

    $textExtensions = @(
        '', '.bat', '.config', '.cs', '.csproj', '.csv', '.dockerignore', '.gitattributes',
        '.gitignore', '.json', '.md', '.props', '.ps1', '.psm1', '.sln', '.targets',
        '.txt', '.xml', '.yaml', '.yml'
    )
    return $textExtensions -ccontains [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
}

function Test-CwbiScannableFile {
    param([Parameter(Mandatory)][string]$Path)

    $buffer = [byte[]]::new(8192)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $bytesRead = $stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }

    if ($bytesRead -eq 0) {
        return $true
    }
    if ($bytesRead -ge 2 -and
        (($buffer[0] -eq 255 -and $buffer[1] -eq 254) -or
         ($buffer[0] -eq 254 -and $buffer[1] -eq 255))) {
        return $true
    }

    $controlBytes = 0
    for ($index = 0; $index -lt $bytesRead; $index++) {
        $value = $buffer[$index]
        if ($value -eq 0) {
            return $false
        }
        if ($value -lt 9 -or ($value -gt 13 -and $value -lt 32)) {
            $controlBytes++
        }
    }
    return ($controlBytes / $bytesRead) -le 0.05
}

function Get-CwbiScannableFiles {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string[]]$ExcludedFiles = @()
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    return @(
        Get-CwbiFiles -Root $resolvedRoot |
            Where-Object {
                $relativePath = ConvertTo-CwbiRelativePath -Root $resolvedRoot -Path $_.FullName
                (Test-CwbiScannableFile -Path $_.FullName) -and $ExcludedFiles -cnotcontains $relativePath
            }
    )
}

function Assert-CwbiSecretsAbsent {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$PolicyPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedPolicySha256
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $policy = Get-CwbiReleasePolicy -PolicyPath $PolicyPath -ExpectedPolicySha256 $ExpectedPolicySha256
    $snapshotPaths = @(
        Get-CwbiFiles -Root $resolvedRoot |
            ForEach-Object { ConvertTo-CwbiRelativePath -Root $resolvedRoot -Path $_.FullName }
    )
    Assert-CwbiSecretBearingPathsAbsent -RelativePaths $snapshotPaths -Policy $policy
    $packageCredentialsElement = 'packageSource' + 'Credentials'
    $clearCredentialKey = 'ClearText' + 'Password'
    $passwordKey = 'Pass' + 'word'
    $packageCredentialsPattern = "(?i)<\s*$([regex]::Escape($packageCredentialsElement))\b"
    $clearCredentialPattern = "(?i)(?:\bkey\s*=\s*[`"']$([regex]::Escape($clearCredentialKey))[`"']|<\s*$([regex]::Escape($clearCredentialKey))\b|\b$([regex]::Escape($clearCredentialKey))\s*=)"
    $passwordValuePattern = "(?i)(?:\bkey\s*=\s*[`"']$([regex]::Escape($passwordKey))[`"']|<\s*$([regex]::Escape($passwordKey))\b[^>]*>|\b$([regex]::Escape($passwordKey))\s*=\s*[`"'][^`"']+)"
    $rules = [ordered]@{
        'private-key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        'aws-access-key' = '(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])'
        'github-token' = '(?<![A-Za-z0-9_])gh[pousr]_[A-Za-z0-9]{36,255}'
        'npm-auth-token' = '(?im)^\s*(?://[^:]+/)?_authToken\s*='
        'credentialed-uri' = '(?i)\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|https?)://[^\s/:@]+:[^\s/@]+@'
        'nuget-package-source-credentials' = $packageCredentialsPattern
        'nuget-clear-text-password' = $clearCredentialPattern
        'nuget-password-value' = $passwordValuePattern
    }
    $findings = [System.Collections.Generic.List[string]]::new()

    foreach ($file in Get-CwbiScannableFiles -Root $resolvedRoot -ExcludedFiles @($policy.secretScanExcludedFiles)) {
        $content = [System.IO.File]::ReadAllText($file.FullName)
        foreach ($rule in $rules.GetEnumerator()) {
            if ([regex]::IsMatch($content, [string]$rule.Value)) {
                $relativePath = ConvertTo-CwbiRelativePath -Root $resolvedRoot -Path $file.FullName
                $findings.Add("$($rule.Key):$(ConvertTo-CwbiDiagnosticPath -Path $relativePath)")
            }
        }
    }

    if ($findings.Count -gt 0) {
        throw "Potential secrets detected (rule:path only): $($findings -join ', ')."
    }
}

function Assert-CwbiGitHistorySecretsAbsent {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$SourceRevision,
        [string[]]$ExcludedFiles = @(),
        $Policy
    )

    if ($SourceRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'SourceRevision must be an explicit 40-character commit id.'
    }
    $repositoryPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $objectType = Invoke-CwbiGit -Repository $repositoryPath -Arguments @('cat-file', '-t', $SourceRevision) -AllowFailure
    if ($objectType.ExitCode -ne 0 -or ([string]$objectType.Output[0]).Trim() -cne 'commit') {
        throw 'SourceRevision must identify the supplied commit object directly.'
    }
    $sourceCommit = ([string](Invoke-CwbiGit -Repository $repositoryPath -Arguments @('rev-parse', '--verify', $SourceRevision)).Output[0]).Trim()
    $commits = @((Invoke-CwbiGit -Repository $repositoryPath -Arguments @('rev-list', $sourceCommit)).Output | ForEach-Object { ([string]$_).Trim() })
    $effectivePolicy = if ($null -ne $Policy) {
        $Policy
    }
    else {
        [pscustomobject]@{
            secretBearingFileExtensions = $script:CwbiRequiredSecretBearingFileExtensions
            secretBearingFileNames = $script:CwbiRequiredSecretBearingFileNames
        }
    }
    $historyPathResult = Invoke-CwbiGitBytes `
        -Repository $repositoryPath `
        -Arguments @('log', '--format=', '--name-only', '-z', $sourceCommit, '--')
    try {
        $historyPathText = [System.Text.UTF8Encoding]::new($false, $true).GetString(
            [byte[]]$historyPathResult.Output)
    }
    catch {
        throw 'Reachable Git history contains a path that is not valid UTF-8.'
    }
    $historyPaths = @(
        $historyPathText.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    )
    Assert-CwbiSecretBearingPathsAbsent `
        -RelativePaths $historyPaths `
        -Policy $effectivePolicy `
        -Context 'Reachable Git history'
    $packageCredentialsElement = 'packageSource' + 'Credentials'
    $clearCredentialKey = 'ClearText' + 'Password'
    $passwordKey = 'Pass' + 'word'
    $passwordHistoryPattern = "(key[[:space:]]*=[[:space:]]*[`"']$passwordKey[`"']|$passwordKey[[:space:]]*=[[:space:]]*[`"'])"
    $passwordDotNetPattern = "(?:\bkey\s*=\s*[`"']$([regex]::Escape($passwordKey))[`"']|\b$([regex]::Escape($passwordKey))\s*=\s*[`"'])"
    $rules = [ordered]@{
        'private-key' = [pscustomobject]@{ GitPattern = '-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'; DotNetPattern = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'; IgnoreCase = $false }
        'aws-access-key' = [pscustomobject]@{ GitPattern = '(AKIA|ASIA)[A-Z0-9]{16}'; DotNetPattern = '(?:AKIA|ASIA)[A-Z0-9]{16}'; IgnoreCase = $false }
        'github-token' = [pscustomobject]@{ GitPattern = 'gh[pousr]_[A-Za-z0-9]{36,}'; DotNetPattern = 'gh[pousr]_[A-Za-z0-9]{36,}'; IgnoreCase = $false }
        'npm-auth-token' = [pscustomobject]@{ GitPattern = '_authToken[[:space:]]*='; DotNetPattern = '_authToken\s*='; IgnoreCase = $false }
        'credentialed-uri' = [pscustomobject]@{ GitPattern = '(postgres(ql)?|mysql|mongodb(\+srv)?|https?)://[^[:space:]/:@]+:[^[:space:]/@]+@'; DotNetPattern = '(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|https?)://[^\s/:@]+:[^\s/@]+@'; IgnoreCase = $true }
        'nuget-package-source-credentials' = [pscustomobject]@{ GitPattern = $packageCredentialsElement; DotNetPattern = [regex]::Escape($packageCredentialsElement); IgnoreCase = $true }
        'nuget-clear-text-password' = [pscustomobject]@{ GitPattern = $clearCredentialKey; DotNetPattern = [regex]::Escape($clearCredentialKey); IgnoreCase = $true }
        'nuget-password-value' = [pscustomobject]@{ GitPattern = $passwordHistoryPattern; DotNetPattern = $passwordDotNetPattern; IgnoreCase = $true }
    }
    $findings = [System.Collections.Generic.List[string]]::new()
    $batchSize = 500
    for ($offset = 0; $offset -lt $commits.Count; $offset += $batchSize) {
        $lastIndex = [Math]::Min($offset + $batchSize - 1, $commits.Count - 1)
        $batch = @($commits[$offset..$lastIndex])
        $arguments = @('grep', '-I', '-l', '-E', '-i')
        foreach ($rule in $rules.GetEnumerator()) {
            $arguments += @('-e', [string]$rule.Value.GitPattern)
        }
        $arguments += $batch + @('--')
        $result = Invoke-CwbiGit -Repository $repositoryPath -Arguments $arguments -AllowFailure
        if ($result.ExitCode -notin @(0, 1)) {
            throw 'Git history secret candidate scan failed.'
        }
        if ($result.ExitCode -eq 1) {
            continue
        }

        foreach ($line in @($result.Output)) {
            $finding = [string]$line
            if ($finding -notmatch '^(?<commit>[0-9a-f]{40}):(?<path>.+)$') {
                throw 'Git history secret scan returned an unexpected result shape.'
            }
            $commit = $Matches.commit
            $path = $Matches.path.Replace('\', '/')
            if ($ExcludedFiles -ccontains $path) {
                continue
            }

            $blob = [byte[]](Invoke-CwbiGitBytes -Repository $repositoryPath -Arguments @('show', "$commit`:$path")).Output
            $content = [System.Text.Encoding]::Latin1.GetString($blob)
            foreach ($rule in $rules.GetEnumerator()) {
                $options = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
                if ($rule.Value.IgnoreCase) {
                    $options = $options -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
                }
                if (-not [regex]::IsMatch($content, [string]$rule.Value.DotNetPattern, $options)) {
                    continue
                }
                $redactedFinding = "$($rule.Key):$(ConvertTo-CwbiDiagnosticPath -Path $path)@$($commit.Substring(0, 12))"
                if (-not $findings.Contains($redactedFinding)) {
                    $findings.Add($redactedFinding)
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        throw "Potential secrets detected in reachable Git history (rule:path@commit only): $($findings -join ', ')."
    }
}

function Get-CwbiContentSha256 {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes,
        [Parameter(Mandatory)][string]$Path
    )

    if (Test-CwbiTextFile -Path $Path) {
        $encoding = [System.Text.Encoding]::Latin1
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) {
            $encoding = [System.Text.Encoding]::Unicode
        }
        elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255) {
            $encoding = [System.Text.Encoding]::BigEndianUnicode
        }
        $text = $encoding.GetString($bytes).Replace("`r`n", "`n").Replace("`r", "`n")
        $bytes = $encoding.GetBytes($text)
    }

    return Get-CwbiBytesSha256 -Bytes $bytes
}

function Get-CwbiFileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return Get-CwbiContentSha256 -Bytes ([System.IO.File]::ReadAllBytes($Path)) -Path $Path
}

function Sort-CwbiManifest {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Manifest)

    $sorted = [object[]]@($Manifest)
    $comparison = [System.Comparison[object]]{
        param($left, $right)
        return [StringComparer]::Ordinal.Compare([string]$left.Path, [string]$right.Path)
    }
    [Array]::Sort($sorted, $comparison)
    return @($sorted)
}

function Get-CwbiContentManifest {
    param([Parameter(Mandatory)][string]$Root)

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $manifest = @(
        Get-CwbiFiles -Root $resolvedRoot |
            ForEach-Object {
                [pscustomobject]@{
                    Path = ConvertTo-CwbiRelativePath -Root $resolvedRoot -Path $_.FullName
                    Sha256 = Get-CwbiFileSha256 -Path $_.FullName
                }
            }
    )
    return @(Sort-CwbiManifest -Manifest $manifest)
}

function Get-CwbiManifestSha256 {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Manifest)

    $serializedEntries = @(
        Sort-CwbiManifest -Manifest $Manifest | ForEach-Object {
            $encodedPath = [System.Text.Json.JsonEncodedText]::Encode([string]$_.Path).ToString()
            $encodedHash = [System.Text.Json.JsonEncodedText]::Encode([string]$_.Sha256).ToString()
            "{`"path`":`"$encodedPath`",`"sha256`":`"$encodedHash`"}"
        }
    )
    $canonical = "[$($serializedEntries -join ',')]"
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($canonical)
    return Get-CwbiBytesSha256 -Bytes $bytes
}

function New-CwbiRunUniqueImageTag {
    param([Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceCommit)

    return "dst-total-risk:cwbi-release-$($SourceCommit.Substring(0, 12).ToLowerInvariant())-$([Guid]::NewGuid().ToString('N'))"
}

function Get-CwbiGitTreeManifest {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Revision
    )

    $entries = @(Get-CwbiRawTreeEntries -Repository $Repository -Revision $Revision)
    Assert-CwbiTreeEntriesPortable -Entries $entries
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "total-risk-cwbi-tree-manifest-$([Guid]::NewGuid().ToString('N'))"
    $archivePath = Join-Path $temporaryRoot 'tree.tar'
    $treeRoot = Join-Path $temporaryRoot 'tree'
    New-Item -ItemType Directory -Path $treeRoot -Force | Out-Null
    try {
        Invoke-CwbiGit -Repository $Repository -Arguments @('archive', '--format=tar', '--output', $archivePath, $Revision) | Out-Null
        & (Get-CwbiTarPath) -xf $archivePath -C $treeRoot
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to extract the prevalidated Git tree for manifest verification.'
        }
        return @(Get-CwbiContentManifest -Root $treeRoot)
    }
    finally {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if ([System.IO.Path]::GetDirectoryName($resolvedTemporaryRoot).TrimEnd('\', '/') -ceq $systemTemporaryRoot -and
            (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}

function Assert-CwbiManifestEqual {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Expected,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Actual
    )

    $difference = @(Compare-Object $Expected $Actual -Property Path, Sha256)
    if ($difference.Count -gt 0) {
        $paths = @($difference | ForEach-Object { ConvertTo-CwbiDiagnosticPath -Path ([string]$_.Path) } | Sort-Object -Unique)
        throw "CWBI target content differs from the verified snapshot: $($paths -join ', ')."
    }
}

function Export-CwbiReleaseTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$SourceRevision,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$PolicyRepositoryPath
    )

    Assert-CwbiRelativePathSafe -Path $PolicyRepositoryPath -Description 'PolicyRepositoryPath'

    $repositoryPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $destinationPath = [System.IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $destinationPath) {
        throw "CWBI staging destination already exists: $destinationPath."
    }

    $sourceCommit = Resolve-CwbiExactApprovedCommit -Repository $repositoryPath -SourceRevision $SourceRevision
    $treeResult = Invoke-CwbiGit -Repository $repositoryPath -Arguments @('rev-parse', "$sourceCommit^{tree}")
    $sourceTree = ([string]$treeResult.Output[0]).Trim()
    $treeEntries = @(Get-CwbiRawTreeEntries -Repository $repositoryPath -Revision $sourceCommit)
    Assert-CwbiTreeEntriesPortable -Entries $treeEntries
    Assert-CwbiSourceCheckoutIdentity -Repository $repositoryPath -SourceCommit $sourceCommit

    $policyEntries = @($treeEntries | Where-Object { $_.Path -ceq $PolicyRepositoryPath })
    if ($policyEntries.Count -ne 1) {
        throw "The selected source commit does not contain the release policy '$(ConvertTo-CwbiDiagnosticPath -Path $PolicyRepositoryPath)'."
    }

    $destinationParent = Split-Path -Parent $destinationPath
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    $releaseInputsRoot = Join-Path $destinationParent "release-inputs-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $releaseInputsRoot | Out-Null
    $pinnedPolicyPath = Join-Path $releaseInputsRoot 'policy.json'
    $policyBytes = Get-CwbiGitObjectBytes -Repository $repositoryPath -ObjectId $policyEntries[0].ObjectId
    [System.IO.File]::WriteAllBytes($pinnedPolicyPath, $policyBytes)
    $policySha256 = Get-CwbiBytesSha256 -Bytes $policyBytes
    $policy = Get-CwbiReleasePolicy -PolicyPath $pinnedPolicyPath -ExpectedPolicySha256 $policySha256
    $selectedTreePaths = @(
        $treeEntries |
            ForEach-Object { [string]$_.Path } |
            Where-Object { Test-CwbiPathAllowed -RelativePath $_ -Policy $policy }
    )
    Assert-CwbiSecretBearingPathsAbsent `
        -RelativePaths $selectedTreePaths `
        -Policy $policy `
        -Context 'Selected CWBI source tree'

    New-Item -ItemType Directory -Path $destinationPath | Out-Null
    $treePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $treeEntries) {
        $null = $treePaths.Add([string]$entry.Path)
    }
    $archivePath = Join-Path $destinationParent "allowlisted-source-$([Guid]::NewGuid().ToString('N')).tar"
    $archivePathspecs = [System.Collections.Generic.List[string]]::new()
    foreach ($allowedFile in @($policy.allowedFiles)) {
        if ($treePaths.Contains([string]$allowedFile)) {
            $archivePathspecs.Add(":(literal)$allowedFile")
        }
    }
    foreach ($allowedDirectory in @($policy.allowedDirectories)) {
        $directoryPrefix = [string]$allowedDirectory
        if (@($treeEntries | Where-Object { $_.Path.StartsWith($directoryPrefix, [StringComparison]::Ordinal) }).Count -gt 0) {
            $archivePathspecs.Add(":(literal)$($directoryPrefix.TrimEnd('/'))")
        }
    }
    foreach ($excludedPath in @($policy.excludedPaths)) {
        $archivePathspecs.Add(":(exclude,literal)$excludedPath")
    }
    $archivePathspecs.Add(':(exclude,glob)test_scenarios/**/csharp/output.json')
    if ($archivePathspecs.Count -eq 0) {
        throw 'CWBI release policy selected no source paths for export.'
    }
    try {
        $archiveArguments = @('archive', '--format=tar', '--output', $archivePath, $sourceCommit, '--') + @($archivePathspecs)
        Invoke-CwbiGit -Repository $repositoryPath -Arguments $archiveArguments | Out-Null
        & (Get-CwbiTarPath) -xf $archivePath -C $destinationPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to extract the prevalidated allowlisted source archive.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $archivePath) {
            Remove-Item -LiteralPath $archivePath -Force
        }
    }

    Assert-CwbiReleasePathPolicy -Root $destinationPath -PolicyPath $pinnedPolicyPath -ExpectedPolicySha256 $policySha256
    $manifest = @(Get-CwbiContentManifest -Root $destinationPath)
    [pscustomobject]@{
        SourceCommit = $sourceCommit
        SourceTree = $sourceTree
        PolicyPath = $pinnedPolicyPath
        PolicySha256 = $policySha256
        Policy = $policy
        SnapshotRoot = $destinationPath
        Manifest = $manifest
        ManifestSha256 = Get-CwbiManifestSha256 -Manifest $manifest
    }
}

function Restore-CwbiWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$BranchName,
        [Parameter(Mandatory)][string]$OriginalBranch,
        [Parameter(Mandatory)][ValidatePattern('^([0-9a-f]{40}|unborn)$')][string]$OriginalCommit,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$OriginalManifest,
        [Parameter(Mandatory)][string]$HooksDirectory,
        [switch]$SwitchedToOrphan,
        [switch]$Unborn
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    $branchRef = "refs/heads/$BranchName"

    function Invoke-RollbackGit {
        param([Parameter(Mandatory)][string[]]$Arguments)

        $result = Invoke-CwbiGit -Repository $TargetPath -Arguments $Arguments -HooksDirectory $HooksDirectory -AllowFailure
        if ($result.ExitCode -ne 0) {
            $failures.Add("git $($Arguments -join ' ')")
        }
        return $result
    }

    $installedRefResult = Invoke-CwbiGit -Repository $TargetPath -Arguments @('show-ref', '--hash', '--verify', $branchRef) -HooksDirectory $HooksDirectory -AllowFailure
    $installedCommit = if ($installedRefResult.ExitCode -eq 0) { ([string]$installedRefResult.Output[0]).Trim() } else { $null }

    if ($Unborn) {
        # The target started as an empty orphan worktree. Restore that state: drop any commit
        # that was created, unstage everything, and remove every file except the .git pointer.
        if ($installedCommit) {
            Invoke-RollbackGit -Arguments @('update-ref', '-d', $branchRef, $installedCommit) | Out-Null
        }
        Invoke-CwbiGit -Repository $TargetPath -Arguments @('rm', '-r', '-f', '-q', '--cached', '--', '.') -HooksDirectory $HooksDirectory -AllowFailure | Out-Null
        foreach ($entry in Get-ChildItem -LiteralPath $TargetPath -Force) {
            if ($entry.Name -ceq '.git') {
                continue
            }
            Remove-Item -LiteralPath $entry.FullName -Recurse -Force
        }
        $branchResult = Invoke-RollbackGit -Arguments @('branch', '--show-current')
        if ($branchResult.ExitCode -eq 0 -and ([string]$branchResult.Output[0]).Trim() -cne $OriginalBranch) {
            $failures.Add('original branch was not restored')
        }
        $headResult = Invoke-CwbiGit -Repository $TargetPath -Arguments @('rev-parse', '--verify', '--quiet', 'HEAD^{commit}') -HooksDirectory $HooksDirectory -AllowFailure
        if ($headResult.ExitCode -eq 0) {
            $failures.Add('unborn target still has a commit after rollback')
        }
        $statusResult = Invoke-RollbackGit -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
        if ($statusResult.ExitCode -eq 0 -and @($statusResult.Output).Count -gt 0) {
            $failures.Add('restored target is dirty')
        }
        if (@(Get-CwbiContentManifest -Root $TargetPath).Count -ne 0) {
            $failures.Add('restored unborn target is not empty')
        }
        if ($failures.Count -gt 0) {
            throw "CWBI rollback could not be verified: $($failures -join '; ')."
        }
        return
    }

    Invoke-RollbackGit -Arguments @('add', '-f', '--all') | Out-Null
    if ($SwitchedToOrphan) {
        Invoke-RollbackGit -Arguments @('switch', '--discard-changes', $OriginalBranch) | Out-Null
        if ($installedCommit) {
            Invoke-RollbackGit -Arguments @('update-ref', '-d', $branchRef, $installedCommit) | Out-Null
        }
    }
    else {
        if (-not $installedCommit) {
            $failures.Add("missing $branchRef")
        }
        elseif ($installedCommit -cne $OriginalCommit) {
            Invoke-RollbackGit -Arguments @('update-ref', $branchRef, $OriginalCommit, $installedCommit) | Out-Null
        }
        Invoke-RollbackGit -Arguments @('restore', "--source=$OriginalCommit", '--staged', '--worktree', '--', '.') | Out-Null
    }

    $branchResult = Invoke-RollbackGit -Arguments @('branch', '--show-current')
    if ($branchResult.ExitCode -eq 0 -and ([string]$branchResult.Output[0]).Trim() -cne $OriginalBranch) {
        $failures.Add('original branch was not restored')
    }
    $headResult = Invoke-RollbackGit -Arguments @('rev-parse', 'HEAD')
    if ($headResult.ExitCode -eq 0 -and ([string]$headResult.Output[0]).Trim() -cne $OriginalCommit) {
        $failures.Add('original commit was not restored')
    }
    if ($SwitchedToOrphan) {
        $orphanRef = Invoke-CwbiGit -Repository $TargetPath -Arguments @('show-ref', '--verify', '--quiet', $branchRef) -HooksDirectory $HooksDirectory -AllowFailure
        if ($orphanRef.ExitCode -eq 0) {
            $failures.Add("new $branchRef remains after rollback")
        }
    }

    $statusResult = Invoke-RollbackGit -Arguments @('status', '--porcelain=v1', '--untracked-files=all')
    if ($statusResult.ExitCode -eq 0 -and @($statusResult.Output).Count -gt 0) {
        $failures.Add('restored target is dirty')
    }
    $cleanResult = Invoke-RollbackGit -Arguments @('clean', '-ndx')
    if ($cleanResult.ExitCode -eq 0 -and @($cleanResult.Output).Count -gt 0) {
        $failures.Add('restored target contains untracked or ignored files')
    }
    try {
        Assert-CwbiManifestEqual -Expected $OriginalManifest -Actual @(Get-CwbiContentManifest -Root $TargetPath)
    }
    catch {
        $failures.Add('restored target content does not match the original manifest')
    }

    if ($failures.Count -gt 0) {
        throw "CWBI rollback could not be verified: $($failures -join '; ')."
    }
}

function Get-CwbiGitConfigValues {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Key,
        [switch]$Local
    )

    $arguments = @('config')
    if ($Local) {
        $arguments += '--local'
    }
    $arguments += @('--get-all', $Key)
    $result = Invoke-CwbiGit -Repository $Repository -Arguments $arguments -AllowFailure
    if ($result.ExitCode -eq 1) {
        return @()
    }
    if ($result.ExitCode -ne 0) {
        throw "Unable to inspect Git configuration key '$Key'."
    }
    return @($result.Output | ForEach-Object { ([string]$_).Trim() })
}

function ConvertTo-CwbiNormalizedRemoteUrl {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Description
    )

    try {
        $uri = [Uri]::new($Url, [UriKind]::Absolute)
    }
    catch {
        throw "$Description URL is not an absolute HTTPS URL."
    }
    if ($uri.Scheme -cne 'https' -or
        $uri.Host -ine 'github.com' -or
        -not $uri.IsDefaultPort -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "$Description URL is not an approved GitHub HTTPS URL."
    }

    $rawMatch = [regex]::Match(
        $Url,
        '\Ahttps://github\.com(?<Path>/[^?#]*)\z',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $rawMatch.Success -or
        $Url.Contains('%', [StringComparison]::Ordinal) -or
        $Url.Contains('\', [StringComparison]::Ordinal)) {
        throw "$Description URL has a non-canonical repository path."
    }

    $path = $rawMatch.Groups['Path'].Value.TrimEnd('/')
    if ($rawMatch.Groups['Path'].Value -cnotmatch '\A/[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*/?\z') {
        throw "$Description URL has a non-canonical repository path."
    }
    if ($path.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
        $path = $path.Substring(0, $path.Length - 4)
    }
    if ($path -cnotmatch '\A/[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z') {
        throw "$Description URL has a non-canonical repository path."
    }
    return "https://github.com$path"
}

function Assert-CwbiRemoteUrl {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$RemoteName,
        [Parameter(Mandatory)][string]$ExpectedUrl
    )

    $explicitPushUrls = @(Get-CwbiGitConfigValues -Repository $Repository -Key "remote.$RemoteName.pushurl")
    if ($explicitPushUrls.Count -gt 0) {
        throw "Remote $RemoteName must not configure an explicit push URL (remote.$RemoteName.pushurl)."
    }

    $fetchResult = Invoke-CwbiGit -Repository $Repository -Arguments @('remote', 'get-url', '--all', $RemoteName) -AllowFailure
    if ($fetchResult.ExitCode -ne 0) {
        throw "Unable to resolve remote $RemoteName fetch URL."
    }
    $fetchUrls = @($fetchResult.Output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_.Length -gt 0 })
    if ($fetchUrls.Count -ne 1) {
        throw "Remote $RemoteName must have exactly one effective fetch URL."
    }
    $normalizedExpected = ConvertTo-CwbiNormalizedRemoteUrl -Url $ExpectedUrl -Description "Expected $RemoteName"
    $normalizedFetch = ConvertTo-CwbiNormalizedRemoteUrl -Url $fetchUrls[0] -Description "Remote $RemoteName fetch"
    if ($normalizedFetch -ine $normalizedExpected) {
        throw "Remote $RemoteName fetch URL is unexpected."
    }

    $pushResult = Invoke-CwbiGit -Repository $Repository -Arguments @('remote', 'get-url', '--push', '--all', $RemoteName) -AllowFailure
    if ($pushResult.ExitCode -ne 0) {
        throw "Unable to resolve remote $RemoteName push URL."
    }
    $pushUrls = @($pushResult.Output | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_.Length -gt 0 })
    if ($pushUrls.Count -ne 1) {
        throw "Remote $RemoteName must have exactly one effective push URL."
    }
    $normalizedPush = ConvertTo-CwbiNormalizedRemoteUrl -Url $pushUrls[0] -Description "Remote $RemoteName push"
    if ($normalizedPush -ine $normalizedExpected) {
        throw "Remote $RemoteName push URL is unexpected."
    }
}

function Assert-CwbiPushTopology {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^(cwbi-dev|snapshot/sanitized)$')][string]$BranchName
    )

    Assert-CwbiRemoteUrl `
        -Repository $Repository `
        -RemoteName 'origin' `
        -ExpectedUrl 'https://github.com/USACE-RMC/RMC-TotalRisk'
    Assert-CwbiRemoteUrl `
        -Repository $Repository `
        -RemoteName 'cwbi' `
        -ExpectedUrl 'https://github.com/cwbi-apps/dst-total-risk.git'

    $localPushDefault = @(Get-CwbiGitConfigValues -Repository $Repository -Key 'push.default' -Local)
    if ($localPushDefault.Count -ne 1 -or $localPushDefault[0] -cne 'nothing') {
        throw 'Repository-local push.default must be exactly nothing.'
    }
    if (@(Get-CwbiGitConfigValues -Repository $Repository -Key 'remote.pushDefault').Count -gt 0) {
        throw 'remote.pushDefault must be unset for deterministic explicit pushes.'
    }
    foreach ($branchSetting in @('remote', 'merge', 'pushRemote')) {
        $key = "branch.$BranchName.$branchSetting"
        if (@(Get-CwbiGitConfigValues -Repository $Repository -Key $key).Count -gt 0) {
            throw "$key must be unset for the local CWBI snapshot branch."
        }
    }
}

function Get-CwbiSnapshotProvenance {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit
    )

    $messageLines = @((Invoke-CwbiGit -Repository $Repository -Arguments @('show', '-s', '--format=%B', $Commit)).Output)
    $nonEmptyLines = @($messageLines | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_.Length -gt 0 })
    if ($nonEmptyLines.Count -ne 5 -or $nonEmptyLines[0] -cne 'Create CWBI development snapshot') {
        throw "CWBI snapshot history is not a generated snapshot history at commit $($Commit.Substring(0, 12))."
    }

    $patterns = @(
        '^Source commit: (?<value>[0-9a-f]{40})$',
        '^Source tree: (?<value>[0-9a-f]{40})$',
        '^Policy SHA-256: (?<value>[0-9a-f]{64})$',
        '^Manifest SHA-256: (?<value>[0-9a-f]{64})$'
    )
    $values = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $patterns.Count; $index++) {
        if ($nonEmptyLines[$index + 1] -notmatch $patterns[$index]) {
            throw "CWBI snapshot history has incomplete or invalid provenance at commit $($Commit.Substring(0, 12))."
        }
        $values.Add($Matches.value)
    }
    $provenance = [pscustomobject]@{
        SourceCommit = $values[0]
        SourceTree = $values[1]
        PolicySha256 = $values[2]
        ManifestSha256 = $values[3]
    }
    $approvedSourceCommit = Resolve-CwbiExactApprovedCommit -Repository $Repository -SourceRevision $provenance.SourceCommit
    $actualSourceTree = ([string](Invoke-CwbiGit -Repository $Repository -Arguments @('rev-parse', "$approvedSourceCommit^{tree}")).Output[0]).Trim()
    if ($actualSourceTree -cne $provenance.SourceTree) {
        throw "CWBI snapshot history records an incorrect source tree at commit $($Commit.Substring(0, 12))."
    }
    $sourceEntries = @(Get-CwbiRawTreeEntries -Repository $Repository -Revision $approvedSourceCommit)
    $policyEntries = @($sourceEntries | Where-Object { $_.Path -ceq 'scripts/cwbi-release/policy.json' })
    if ($policyEntries.Count -ne 1) {
        throw "CWBI snapshot history source lacks its release policy at commit $($Commit.Substring(0, 12))."
    }
    $sourcePolicySha256 = Get-CwbiBytesSha256 -Bytes (Get-CwbiGitObjectBytes -Repository $Repository -ObjectId $policyEntries[0].ObjectId)
    if ($sourcePolicySha256 -cne $provenance.PolicySha256) {
        throw "CWBI snapshot history records an incorrect policy digest at commit $($Commit.Substring(0, 12))."
    }
    return $provenance
}

function Assert-CwbiSnapshotHistory {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^(cwbi-dev|snapshot/sanitized)$')][string]$BranchName,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$CurrentCommit
    )

    $historyLines = @((Invoke-CwbiGit -Repository $Repository -Arguments @('rev-list', '--parents', '--reverse', "refs/heads/$BranchName")).Output)
    if ($historyLines.Count -eq 0) {
        throw 'CWBI snapshot history is empty.'
    }

    $previousCommit = $null
    $currentProvenance = $null
    for ($index = 0; $index -lt $historyLines.Count; $index++) {
        $parts = ([string]$historyLines[$index]).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        if ($index -eq 0) {
            if ($parts.Count -ne 1) {
                throw 'CWBI snapshot history does not begin with an orphan root commit.'
            }
        }
        elseif ($parts.Count -ne 2 -or $parts[1] -cne $previousCommit) {
            throw 'CWBI snapshot history must be linear and merge-free.'
        }
        $commit = $parts[0]
        $currentProvenance = Get-CwbiSnapshotProvenance -Repository $Repository -Commit $commit
        $treeManifest = @(Get-CwbiGitTreeManifest -Repository $Repository -Revision $commit)
        $treeManifestSha256 = Get-CwbiManifestSha256 -Manifest $treeManifest
        if ($treeManifestSha256 -cne $currentProvenance.ManifestSha256) {
            throw "CWBI snapshot tree does not match its recorded manifest provenance at commit $($commit.Substring(0, 12))."
        }
        $previousCommit = $commit
    }
    if ($previousCommit -cne $CurrentCommit) {
        throw 'CWBI snapshot history tip does not equal the expected current commit.'
    }
    return $currentProvenance
}

function Test-CwbiPathWithin {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return $fullPath.Equals($fullRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith("$fullRoot$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)
}

function Assert-CwbiPathComponentsNotReparsePoints {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowLeafFile
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($pathRoot)) {
        throw 'Release evidence path has no filesystem root.'
    }

    $components = @($fullPath.Substring($pathRoot.Length).Split(
        [char[]]@('\', '/'),
        [StringSplitOptions]::RemoveEmptyEntries))
    $currentPath = $pathRoot
    for ($componentIndex = 0; $componentIndex -lt $components.Count; $componentIndex++) {
        $component = $components[$componentIndex]
        $currentPath = Join-Path $currentPath $component
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }
        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release evidence path traverses a reparse point, junction, or symbolic link: '$(ConvertTo-CwbiDiagnosticPath -Path $currentPath)'."
        }
        $isAllowedLeafFile = $AllowLeafFile -and $componentIndex -eq ($components.Count - 1)
        if (-not $item.PSIsContainer -and -not $isAllowedLeafFile) {
            throw "Release evidence path component is not a directory: '$(ConvertTo-CwbiDiagnosticPath -Path $currentPath)'."
        }
    }
}

function Assert-CwbiEvidenceLocation {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$EvidenceDirectory,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$SnapshotPath
    )

    $evidenceRoot = [System.IO.Path]::GetFullPath($EvidenceDirectory)
    Assert-CwbiPathComponentsNotReparsePoints -Path $evidenceRoot
    if ((Test-CwbiPathWithin -Path $evidenceRoot -Root $TargetPath) -or
        (Test-CwbiPathWithin -Path $evidenceRoot -Root $SnapshotPath)) {
        throw 'Release evidence directory must be outside both the deployment worktree and verified snapshot.'
    }
    return $evidenceRoot
}

function New-CwbiReleaseEvidence {
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$EvidenceDirectory,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$SnapshotPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceTree,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PolicySha256,
        [Parameter(Mandatory)][object[]]$Manifest,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
        [Parameter(Mandatory)][ValidatePattern('^cwbi-dev$')][string]$BranchName,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$TargetCommit
    )

    $evidenceRoot = Assert-CwbiEvidenceLocation -EvidenceDirectory $EvidenceDirectory -TargetPath $TargetPath -SnapshotPath $SnapshotPath
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    Assert-CwbiEvidenceLocation -EvidenceDirectory $evidenceRoot -TargetPath $TargetPath -SnapshotPath $SnapshotPath | Out-Null

    $entries = @(
        Sort-CwbiManifest -Manifest $Manifest | ForEach-Object {
            [ordered]@{ path = [string]$_.Path; sha256 = [string]$_.Sha256 }
        }
    )
    $evidence = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        source = [ordered]@{ commit = $SourceCommit; tree = $SourceTree }
        policySha256 = $PolicySha256
        manifest = [ordered]@{ entries = $entries; sha256 = $ManifestSha256 }
        target = [ordered]@{ branch = $BranchName; commit = $TargetCommit }
    }
    $json = $evidence | ConvertTo-Json -Depth 8 -Compress
    $fileName = "cwbi-release-$($SourceCommit.Substring(0, 12))-$($TargetCommit.Substring(0, 12))-$([Guid]::NewGuid().ToString('N')).json"
    $evidencePath = Join-Path $evidenceRoot $fileName
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = $null
    $completed = $false
    try {
        Assert-CwbiEvidenceLocation -EvidenceDirectory $evidenceRoot -TargetPath $TargetPath -SnapshotPath $SnapshotPath | Out-Null
        Invoke-CwbiTestFault -Point 'after-evidence-precreate-check'
        $stream = [System.IO.FileStream]::new(
            $evidencePath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None
        )
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        Invoke-CwbiTestFault -Point 'after-evidence-write'
        Assert-CwbiEvidenceLocation -EvidenceDirectory $evidenceRoot -TargetPath $TargetPath -SnapshotPath $SnapshotPath | Out-Null
        Assert-CwbiPathComponentsNotReparsePoints -Path $evidencePath -AllowLeafFile
        $targetStatus = @((Invoke-CwbiGit -Repository $TargetPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output)
        $targetCleanPreview = @((Invoke-CwbiGit -Repository $TargetPath -Arguments @('clean', '-ndx')).Output)
        if ($targetStatus.Count -gt 0 -or $targetCleanPreview.Count -gt 0) {
            throw 'Target worktree became dirty during release evidence creation.'
        }
        $completed = $true
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
        if (-not $completed -and (Test-Path -LiteralPath $evidencePath)) {
            Remove-Item -LiteralPath $evidencePath -Force
        }
    }
    return $evidencePath
}

function Publish-CwbiSnapshotToWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TargetWorktree,
        [Parameter(Mandatory)][string]$SnapshotRoot,
        [Parameter(Mandatory)][ValidatePattern('^(cwbi-dev|snapshot/sanitized)$')][string]$BranchName,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceTree,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PolicySha256,
        [Parameter(Mandatory)][string]$PolicyPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ManifestSha256,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$EvidenceDirectory,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedCurrentBranch,
        [Parameter(Mandatory)][ValidatePattern('^([0-9a-fA-F]{40}|unborn)$')][string]$ExpectedCurrentCommit,
        [ValidatePattern('^cwbi-dev$')][string]$DestinationBranchName = 'cwbi-dev',
        [string]$ExpectedCommonGitDirectory
    )

    $targetPath = (Resolve-Path -LiteralPath $TargetWorktree).Path
    $snapshotPath = (Resolve-Path -LiteralPath $SnapshotRoot).Path
    $worktreeRoot = ([string](Invoke-CwbiGit -Repository $targetPath -Arguments @('rev-parse', '--show-toplevel')).Output[0]).Trim()
    if ([System.IO.Path]::GetFullPath($worktreeRoot) -cne $targetPath) {
        throw "Target must be the exact Git worktree root: $worktreeRoot."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $targetPath '.git') -PathType Leaf)) {
        throw 'Target must be a linked Git worktree with a .git pointer file.'
    }
    if ($targetPath -ceq $snapshotPath -or
        $targetPath.StartsWith("$snapshotPath\", [StringComparison]::OrdinalIgnoreCase) -or
        $snapshotPath.StartsWith("$targetPath\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The target worktree must be separate from the verified snapshot.'
    }
    Assert-CwbiEvidenceLocation -EvidenceDirectory $EvidenceDirectory -TargetPath $targetPath -SnapshotPath $snapshotPath | Out-Null

    $currentBranch = ([string](Invoke-CwbiGit -Repository $targetPath -Arguments @('branch', '--show-current')).Output[0]).Trim()
    # An "unborn" target is a linked worktree created with `git worktree add --orphan`: the
    # branch name is checked out but has no commit yet. That is how the very first snapshot
    # is published when the destination repository has no cwbi-dev branch at all.
    $headResult = Invoke-CwbiGit -Repository $targetPath -Arguments @('rev-parse', '--verify', '--quiet', 'HEAD^{commit}') -AllowFailure
    $unborn = $headResult.ExitCode -ne 0
    $currentCommit = if ($unborn) { 'unborn' } else { ([string]$headResult.Output[0]).Trim() }
    if ($currentBranch -cne $ExpectedCurrentBranch) {
        throw "Target branch changed: expected $ExpectedCurrentBranch, found $currentBranch."
    }
    if ($currentCommit -ine $ExpectedCurrentCommit) {
        throw "Target commit changed: expected $ExpectedCurrentCommit, found $currentCommit."
    }
    if ($ExpectedCommonGitDirectory) {
        $actualCommonDirectory = ([string](Invoke-CwbiGit -Repository $targetPath -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir')).Output[0]).Trim()
        $expectedCommonDirectory = [System.IO.Path]::GetFullPath($ExpectedCommonGitDirectory)
        if ([System.IO.Path]::GetFullPath($actualCommonDirectory) -cne $expectedCommonDirectory) {
            throw "Target worktree is attached to an unexpected Git directory: $actualCommonDirectory."
        }
    }
    Assert-CwbiPushTopology -Repository $targetPath -BranchName $BranchName

    $status = (Invoke-CwbiGit -Repository $targetPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all')).Output
    $cleanPreview = (Invoke-CwbiGit -Repository $targetPath -Arguments @('clean', '-ndx')).Output
    if (@($status).Count -gt 0 -or @($cleanPreview).Count -gt 0) {
        throw 'Target worktree is not completely clean, including untracked and ignored files.'
    }

    $branchExists = (Invoke-CwbiGit -Repository $targetPath -Arguments @('show-ref', '--verify', '--quiet', "refs/heads/$BranchName") -AllowFailure).ExitCode -eq 0
    if ($unborn) {
        if ($branchExists) {
            throw "Target has no commit but local branch $BranchName already has a ref."
        }
        if ($currentBranch -cne $BranchName) {
            throw "An unborn target must be checked out on $BranchName; found $currentBranch."
        }
    }
    if ($branchExists -and $currentBranch -cne $BranchName) {
        throw "Local branch $BranchName already exists but is not checked out in the target worktree."
    }
    if (-not $branchExists -and -not $unborn -and $currentBranch -ceq $BranchName) {
        throw "Target reports branch $BranchName but its local ref is missing."
    }
    if ($branchExists) {
        $upstream = Invoke-CwbiGit -Repository $targetPath -Arguments @('rev-parse', '--abbrev-ref', '@{upstream}') -AllowFailure
        if ($upstream.ExitCode -eq 0) {
            throw 'The local CWBI branch unexpectedly has an upstream.'
        }
        Assert-CwbiSnapshotHistory -Repository $targetPath -BranchName $BranchName -CurrentCommit $currentCommit | Out-Null
    }

    $originalManifest = @(Get-CwbiContentManifest -Root $targetPath)
    $expectedManifest = @(Get-CwbiContentManifest -Root $snapshotPath)
    if ((Get-CwbiManifestSha256 -Manifest $expectedManifest) -cne $ManifestSha256) {
        throw 'Snapshot manifest digest changed before target replacement.'
    }

    $switchedToOrphan = $false
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
    $emptyHooksDirectory = Join-Path $temporaryRoot "total-risk-cwbi-empty-hooks-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $emptyHooksDirectory | Out-Null

    try {
        Assert-CwbiPolicyDigest -PolicyPath $PolicyPath -ExpectedPolicySha256 $PolicySha256
        if ($unborn) {
            # Already on an empty orphan branch; there is nothing to switch or remove.
        }
        elseif (-not $branchExists) {
            Invoke-CwbiGit -Repository $targetPath -Arguments @('switch', '--orphan', $BranchName) -HooksDirectory $emptyHooksDirectory | Out-Null
            $switchedToOrphan = $true
        }
        else {
            Invoke-CwbiGit -Repository $targetPath -Arguments @('rm', '-r', '-f', '--', '.') -HooksDirectory $emptyHooksDirectory | Out-Null
        }

        foreach ($entry in Get-ChildItem -LiteralPath $snapshotPath -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination $targetPath -Recurse -Force
        }
        Invoke-CwbiGit -Repository $targetPath -Arguments @('add', '-f', '--all') -HooksDirectory $emptyHooksDirectory | Out-Null
        Assert-CwbiManifestEqual -Expected $expectedManifest -Actual @(Get-CwbiContentManifest -Root $targetPath)

        $stagedDifference = (Invoke-CwbiGit -Repository $targetPath -Arguments @('diff', '--cached', '--quiet') -HooksDirectory $emptyHooksDirectory -AllowFailure).ExitCode
        if ($unborn -and $stagedDifference -eq 0) {
            throw 'The first CWBI snapshot staged no content.'
        }
        if ($stagedDifference -eq 0) {
            $currentCommitBody = @((Invoke-CwbiGit -Repository $targetPath -Arguments @('log', '-1', '--format=%B') -HooksDirectory $emptyHooksDirectory).Output)
            $requiredProvenance = @(
                "Source commit: $SourceCommit"
                "Source tree: $SourceTree"
                "Policy SHA-256: $PolicySha256"
                "Manifest SHA-256: $ManifestSha256"
            )
            if (@($requiredProvenance | Where-Object { $currentCommitBody -cnotcontains $_ }).Count -eq 0) {
                Assert-CwbiPolicyDigest -PolicyPath $PolicyPath -ExpectedPolicySha256 $PolicySha256
                Assert-CwbiPushTopology -Repository $targetPath -BranchName $BranchName
                $evidencePath = New-CwbiReleaseEvidence `
                    -EvidenceDirectory $EvidenceDirectory `
                    -TargetPath $targetPath `
                    -SnapshotPath $snapshotPath `
                    -SourceCommit $SourceCommit `
                    -SourceTree $SourceTree `
                    -PolicySha256 $PolicySha256 `
                    -Manifest $expectedManifest `
                    -ManifestSha256 $ManifestSha256 `
                    -BranchName $DestinationBranchName `
                    -TargetCommit $currentCommit
                return [pscustomobject]@{ Branch = $DestinationBranchName; LocalBranch = $BranchName; Commit = $currentCommit; CreatedRootCommit = $false; Changed = $false; EvidencePath = $evidencePath }
            }
        }
        elseif ($stagedDifference -ne 1) {
            throw 'Unable to determine whether the CWBI snapshot changed.'
        }

        $commitArguments = @('commit')
        if ($stagedDifference -eq 0) {
            $commitArguments += '--allow-empty'
        }
        $commitArguments += @(
            '-m', 'Create CWBI development snapshot',
            '-m', "Source commit: $SourceCommit`nSource tree: $SourceTree`nPolicy SHA-256: $PolicySha256`nManifest SHA-256: $ManifestSha256"
        )
        Invoke-CwbiGit -Repository $targetPath -Arguments $commitArguments -HooksDirectory $emptyHooksDirectory | Out-Null
        Invoke-CwbiTestFault -Point 'after-commit'

        $newCommit = ([string](Invoke-CwbiGit -Repository $targetPath -Arguments @('rev-parse', 'HEAD') -HooksDirectory $emptyHooksDirectory).Output[0]).Trim()
        $parents = ([string](Invoke-CwbiGit -Repository $targetPath -Arguments @('rev-list', '--parents', '-n', '1', 'HEAD') -HooksDirectory $emptyHooksDirectory).Output[0]).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        $createdRootCommit = $switchedToOrphan -or $unborn
        if ($createdRootCommit -and $parents.Count -ne 1) {
            throw 'The first CWBI snapshot commit unexpectedly has a parent.'
        }
        if (-not $createdRootCommit -and $parents.Count -ne 2) {
            throw 'The updated CWBI snapshot commit does not have exactly one parent.'
        }
        Assert-CwbiManifestEqual -Expected $expectedManifest -Actual @(Get-CwbiContentManifest -Root $targetPath)
        if (@((Invoke-CwbiGit -Repository $targetPath -Arguments @('status', '--porcelain=v1') -HooksDirectory $emptyHooksDirectory).Output).Count -gt 0) {
            throw 'The committed CWBI snapshot is not clean.'
        }
        Assert-CwbiPolicyDigest -PolicyPath $PolicyPath -ExpectedPolicySha256 $PolicySha256
        Assert-CwbiPushTopology -Repository $targetPath -BranchName $BranchName
        Assert-CwbiSnapshotHistory -Repository $targetPath -BranchName $BranchName -CurrentCommit $newCommit | Out-Null
        $evidencePath = New-CwbiReleaseEvidence `
            -EvidenceDirectory $EvidenceDirectory `
            -TargetPath $targetPath `
            -SnapshotPath $snapshotPath `
            -SourceCommit $SourceCommit `
            -SourceTree $SourceTree `
            -PolicySha256 $PolicySha256 `
            -Manifest $expectedManifest `
            -ManifestSha256 $ManifestSha256 `
            -BranchName $DestinationBranchName `
            -TargetCommit $newCommit

        [pscustomobject]@{ Branch = $DestinationBranchName; LocalBranch = $BranchName; Commit = $newCommit; CreatedRootCommit = $createdRootCommit; Changed = $true; EvidencePath = $evidencePath }
    }
    catch {
        $publicationError = $_
        try {
            Restore-CwbiWorktree `
                -TargetPath $targetPath `
                -BranchName $BranchName `
                -OriginalBranch $currentBranch `
                -OriginalCommit $currentCommit `
                -OriginalManifest $originalManifest `
                -HooksDirectory $emptyHooksDirectory `
                -SwitchedToOrphan:$switchedToOrphan `
                -Unborn:$unborn
        }
        catch {
            throw "CWBI publication failed and rollback could not be verified. Publication error: $($publicationError.Exception.Message) Rollback error: $($_.Exception.Message)"
        }
        throw $publicationError
    }
    finally {
        $resolvedHooksDirectory = [System.IO.Path]::GetFullPath($emptyHooksDirectory)
        if ([System.IO.Path]::GetDirectoryName($resolvedHooksDirectory).TrimEnd('\') -ceq $temporaryRoot -and
            (Test-Path -LiteralPath $resolvedHooksDirectory)) {
            Remove-Item -LiteralPath $resolvedHooksDirectory -Recurse -Force
        }
    }
}

Export-ModuleMember -Function @(
    'Assert-CwbiPushTopology',
    'Assert-CwbiNuGetAuditClean',
    'Assert-CwbiGitHistorySecretsAbsent',
    'Assert-CwbiPolicyDigest',
    'Assert-CwbiReleasePathPolicy',
    'Assert-CwbiSecretsAbsent',
    'Export-CwbiReleaseTree',
    'Get-CwbiContentManifest',
    'Get-CwbiManifestSha256',
    'Get-CwbiReleasePolicy',
    'New-CwbiRunUniqueImageTag',
    'Publish-CwbiSnapshotToWorktree'
)
