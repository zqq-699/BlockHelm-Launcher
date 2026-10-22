param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Release notes file is missing: $Path"
}

$content = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($content)) {
    throw "Release notes file is empty: $Path"
}

if ($content.Length -gt 65536) {
    throw "Release notes must not exceed 65536 characters: $Path"
}

$lines = @($content -split "`r?`n")
$firstContentLine = $lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
if ($firstContentLine -notmatch '^#\s+\S') {
    throw "Release notes must start with one level-one title: $Path"
}

$nonEmptyLines = @($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($nonEmptyLines.Count -lt 2) {
    throw "Release notes must contain content after the level-one title: $Path"
}

Write-Host "Validated Markdown release notes: $Path"
