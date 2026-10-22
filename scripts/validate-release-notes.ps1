param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Release notes file is missing: $Path"
}

$lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
$firstContentLine = $lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if ($firstContentLine -notmatch '^#\s+\S') {
    throw "Release notes must start with one level-one title: $Path"
}

$sectionOrderByName = @{
    '新功能' = 0
    'New Features' = 0
    '问题修复' = 1
    'Bug Fixes' = 1
    '优化改进' = 2
    'Improvements' = 2
    '兼容性与迁移' = 3
    'Compatibility and Migration' = 3
    '已知问题' = 4
    'Known Issues' = 4
    '其他' = 5
    'Other' = 5
}
$seenSectionOrders = [System.Collections.Generic.HashSet[int]]::new()
$currentSection = $null
$currentItemCount = 0
$totalItemCount = 0
$sectionCount = 0
$lastSectionOrder = -1
$titleSeen = $false

foreach ($rawLine in $lines) {
    $line = $rawLine.Trim()
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    if ($line -match '^#\s+') {
        if ($titleSeen) {
            throw "Release notes must contain exactly one level-one title: $Path"
        }

        $titleSeen = $true
        continue
    }

    if ($line -match '^##\s+(?<title>.+?)\s*$') {
        if ($null -ne $currentSection -and $currentItemCount -eq 0) {
            throw "Release notes section '$currentSection' must contain at least one list item: $Path"
        }

        $currentSection = $Matches.title
        if (-not $sectionOrderByName.ContainsKey($currentSection)) {
            $allowedSections = $sectionOrderByName.Keys | Sort-Object { $sectionOrderByName[$_] }, { $_ }
            throw "Unsupported release notes section '$currentSection'. Allowed sections: $($allowedSections -join ', ')"
        }

        $sectionOrder = [int]$sectionOrderByName[$currentSection]
        if ($sectionOrder -lt $lastSectionOrder) {
            throw "Release notes section '$currentSection' is out of order: $Path"
        }

        if (-not $seenSectionOrders.Add($sectionOrder)) {
            throw "Release notes category '$currentSection' is duplicated: $Path"
        }

        $lastSectionOrder = $sectionOrder
        $currentItemCount = 0
        $sectionCount++
        continue
    }

    if ($null -eq $currentSection) {
        throw "Release notes content must be placed under a level-two section: $Path"
    }

    if ($line -notmatch '^[-*]\s+\S') {
        throw "Release notes entries must be one-line Markdown list items: '$line'"
    }

    if ($line.Length -gt 1002) {
        throw "Release notes entries must not exceed 1000 characters: '$currentSection'"
    }

    $currentItemCount++
    $totalItemCount++
    if ($totalItemCount -gt 200) {
        throw "Release notes must not contain more than 200 list items: $Path"
    }
}

if ($sectionCount -eq 0) {
    throw "Release notes must contain at least one level-two section: $Path"
}

if ($currentItemCount -eq 0) {
    throw "Release notes section '$currentSection' must contain at least one list item: $Path"
}

Write-Host "Validated structured release notes: $Path"
