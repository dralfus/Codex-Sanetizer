Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$baseDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $baseDir ".generated"
$outputFile = Join-Path $outputDir "fake_secrets.txt"

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$samples = @(
    "AWS_ACCESS_KEY_ID=" + ("AK", "IA", "IOSF", "ODNN", "7EXA", "MPLE" -join ""),
    "aws_secret_access_key=" + ("wJalr", "XUtn", "FEMI", "/K7M", "DENG", "/bPx", "RfiCY", "EXAMPLEKEY" -join ""),
    "github_token=" + ("gh", "p_", "1234567890", "abcdefghijklmnop", "qrstuvwxyz", "ABCD" -join ""),
    "slack_token=" + ("xo", "xb-", "123456789012", "-", "123456789012", "-", "abcdefghijkl", "mnopqrstuvwx" -join "")
)

Set-Content -Path $outputFile -Value $samples -Encoding UTF8
Write-Host "Generated local fake secret fixture: $outputFile"
