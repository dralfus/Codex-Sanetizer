function Test-Net10Sdk {
    param([string] $DotnetPath)

    if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath)) {
        return $false
    }

    try {
        $sdkList = & $DotnetPath --list-sdks 2>$null | Out-String
        return $LASTEXITCODE -eq 0 -and $sdkList -match '(?m)^\s*10\.'
    }
    catch {
        return $false
    }
}

function Resolve-Net10Sdk {
    param([Parameter(Mandatory = $true)][string] $RepositoryRoot)

    $explicit = $env:DOTNET_EXE
    if (-not [string]::IsNullOrWhiteSpace($explicit)) {
        if (-not (Test-Net10Sdk -DotnetPath $explicit)) {
            throw "DOTNET_EXE does not point to a usable .NET 10 SDK host: $explicit"
        }

        return $explicit
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @(
        (Join-Path $RepositoryRoot "artifacts\dotnet-sdk\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
        (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe")
    )) {
        if ((Test-Path -LiteralPath $candidate) -and -not $candidates.Contains($candidate)) {
            $candidates.Add($candidate)
        }
    }

    $resolvedCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($resolvedCommand -and $resolvedCommand.Path -and -not $candidates.Contains($resolvedCommand.Path)) {
        $candidates.Add($resolvedCommand.Path)
    }

    foreach ($candidate in $candidates) {
        if (Test-Net10Sdk -DotnetPath $candidate) {
            return $candidate
        }
    }

    throw "A usable .NET 10 SDK was not found. Install the .NET 10 SDK or set DOTNET_EXE to a dotnet.exe that reports a 10.x SDK."
}
