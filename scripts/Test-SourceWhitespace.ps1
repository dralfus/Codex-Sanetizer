param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

$textExtensions = @(
    ".cs",
    ".csproj",
    ".iss",
    ".json",
    ".md",
    ".py",
    ".ps1",
    ".props",
    ".resx",
    ".targets",
    ".txt",
    ".xml",
    ".yml",
    ".yaml"
)

$trackedFiles = & git -C $RepositoryRoot ls-files
if ($LASTEXITCODE -ne 0) {
    throw "Could not list tracked files for whitespace verification."
}

$violations = foreach ($relativePath in $trackedFiles) {
    $extension = [System.IO.Path]::GetExtension($relativePath)
    if ($textExtensions -notcontains $extension.ToLowerInvariant()) {
        continue
    }

    $fullPath = Join-Path $RepositoryRoot $relativePath
    $lines = [System.IO.File]::ReadAllLines($fullPath)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match "[ `t]+$") {
            "${relativePath}:$($index + 1): trailing whitespace"
        }
    }

    $text = [System.IO.File]::ReadAllText($fullPath)
    if ($text -match "(\r?\n){2}$") {
        "${relativePath}:$($lines.Length): extra blank line at end of file"
    }
}

if ($violations) {
    $violations | ForEach-Object { Write-Host $_ }
    throw "Source whitespace verification failed."
}

Write-Host "source_whitespace=clean"
