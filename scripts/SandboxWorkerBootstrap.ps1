<#
.SYNOPSIS
Records Windows Sandbox worker startup before invoking the constrained worker.

.DESCRIPTION
The .wsb LogonCommand is otherwise silent when PowerShell fails before the
worker publishes worker.json. This wrapper writes only timestamps, paths, and
exception types/messages to the mapped job log; it never writes job content,
test output, or environment values.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$jobsRoot = Join-Path $repositoryRoot '.sandbox-jobs'
$startupLog = Join-Path $jobsRoot 'worker-startup.log'
$interactiveSessionDirectory = Join-Path $env:ProgramData 'CodexRedactionGate\SandboxWorker'
$interactiveSessionPath = Join-Path $interactiveSessionDirectory 'interactive-session.id'

function Test-InteractiveSandboxSessionId {
    param([string] $Value)

    [Guid] $parsed = [Guid]::Empty
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        [Guid]::TryParseExact($Value, 'D', [ref] $parsed) -and
        $Value -ceq $parsed.ToString('D')
}

function Get-InteractiveSandboxSessionId {
    [IO.Directory]::CreateDirectory($interactiveSessionDirectory) | Out-Null
    if (Test-Path -LiteralPath $interactiveSessionPath -PathType Leaf) {
        $existing = [IO.File]::ReadAllText($interactiveSessionPath).Trim()
        if (-not (Test-InteractiveSandboxSessionId $existing)) { throw 'interactive_session_id_invalid' }
        return $existing
    }

    $candidate = [Guid]::NewGuid().ToString('D')
    try {
        $stream = [IO.File]::Open($interactiveSessionPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes($candidate)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
        }
        finally {
            $stream.Dispose()
        }
        return $candidate
    }
    catch [IO.IOException] {
        $existing = [IO.File]::ReadAllText($interactiveSessionPath).Trim()
        if (-not (Test-InteractiveSandboxSessionId $existing)) { throw 'interactive_session_id_invalid' }
        return $existing
    }
}

function Initialize-InteractiveSupervisor {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    if (-not ('SandboxInteractiveHostNative' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class SandboxInteractiveHostNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

}
'@
    }

    $supervisor = New-Object System.Windows.Forms.Form
    $supervisor.Text = 'Sandbox test supervisor'
    $supervisor.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedToolWindow
    $supervisor.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
    $supervisor.Location = New-Object System.Drawing.Point(24, 24)
    $supervisor.ClientSize = New-Object System.Drawing.Size(260, 72)
    $supervisor.MaximizeBox = $false
    $supervisor.MinimizeBox = $false
    $supervisor.ShowInTaskbar = $true
    $supervisor.Controls.Add((New-Object System.Windows.Forms.Label -Property @{
        AutoSize = $true
        Location = New-Object System.Drawing.Point(12, 24)
        Text = 'Sandbox test worker is ready.'
    }))
    $supervisor.Show()
    [System.Windows.Forms.Application]::DoEvents()
    return $supervisor
}

function Request-InteractiveSupervisorForeground {
    param([Parameter(Mandatory)] [System.Windows.Forms.Form] $Supervisor)

    $attemptedAtUtc = [DateTime]::UtcNow.ToString('O')
    $Supervisor.Show()
    $Supervisor.Activate()
    [void] [SandboxInteractiveHostNative]::SetForegroundWindow($Supervisor.Handle)
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        if ([SandboxInteractiveHostNative]::GetForegroundWindow() -eq $Supervisor.Handle) {
            return [pscustomobject]@{
                Present = $true
                ForegroundReady = $true
                AttemptedAtUtc = $attemptedAtUtc
                Attempt = 1
            }
        }

        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)

    return [pscustomobject]@{
        Present = $true
        ForegroundReady = $false
        AttemptedAtUtc = $attemptedAtUtc
        Attempt = 1
    }
}

function Write-StartupLog {
    param([Parameter(Mandatory)] [string] $Message)

    [System.IO.Directory]::CreateDirectory($jobsRoot) | Out-Null
    $line = "{0} {1}`r`n" -f [DateTime]::UtcNow.ToString('O'), $Message
    [System.IO.File]::AppendAllText($startupLog, $line, [System.Text.UTF8Encoding]::new($false))
}

function ConvertTo-StartupDiagnosticToken {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^[A-Za-z0-9_.-]{1,120}$') {
        return 'unavailable'
    }

    return $Value
}

$supervisor = $null
$workerRunspace = $null
$workerPowerShell = $null
$workerAsync = $null
$workerCompletionTimer = $null
$workerEndInvoked = $false
$startupStage = 'bootstrap'
$interactiveSessionId = $null

try {
    $startupStage = 'bootstrap_log'
    Write-StartupLog "bootstrap_started script=$PSScriptRoot"
    $startupStage = 'interactive_session'
    $interactiveSessionId = Get-InteractiveSandboxSessionId
    $startupStage = 'supervisor_initialize'
    $supervisor = Initialize-InteractiveSupervisor
    $startupStage = 'supervisor_foreground'
    $initialReadiness = Request-InteractiveSupervisorForeground -Supervisor $supervisor
    Write-StartupLog "interactive_host_present=$($initialReadiness.Present.ToString().ToLowerInvariant()) interactive_host_foreground_ready=$($initialReadiness.ForegroundReady.ToString().ToLowerInvariant())"

    $startupStage = 'runspace_create'
    $workerRunspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $workerRunspace.ApartmentState = [System.Threading.ApartmentState]::MTA
    $workerRunspace.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
    $startupStage = 'runspace_open'
    $workerRunspace.Open()
    $startupStage = 'worker_powershell_create'
    $workerPowerShell = [System.Management.Automation.PowerShell]::Create()
    $workerPowerShell.Runspace = $workerRunspace
    $startupStage = 'worker_invocation_bind'
    $workerInvocation = $workerPowerShell.AddCommand((Join-Path $PSScriptRoot 'SandboxTestWorker.ps1'))
    [void] $workerInvocation.AddParameter('InteractiveHostHandle', $supervisor.Handle)
    [void] $workerInvocation.AddParameter('InteractiveHostPresent', $initialReadiness.Present)
    [void] $workerInvocation.AddParameter('InteractiveHostForegroundReady', $initialReadiness.ForegroundReady)
    [void] $workerInvocation.AddParameter('InteractiveHostAttemptedAtUtc', $initialReadiness.AttemptedAtUtc)
    [void] $workerInvocation.AddParameter('InteractiveHostAttempt', $initialReadiness.Attempt)
    [void] $workerInvocation.AddParameter('InteractiveSessionId', $interactiveSessionId)
    $startupStage = 'worker_begin_invoke'
    $workerAsync = $workerPowerShell.BeginInvoke()

    $workerCompletionTimer = New-Object System.Windows.Forms.Timer
    $workerCompletionTimer.Interval = 200
    $workerCompletionTimer.Add_Tick({
        if ($null -eq $workerAsync -or -not $workerAsync.IsCompleted) {
            return
        }

        $workerCompletionTimer.Stop()
        if (-not $workerEndInvoked) {
            $workerEndInvoked = $true
            try {
                $null = $workerPowerShell.EndInvoke($workerAsync)
                Write-StartupLog 'worker_runspace_completed'
            }
            catch {
                Write-StartupLog 'worker_runspace_failed'
            }
        }

        if (-not $supervisor.IsDisposed) {
            $null = $supervisor.BeginInvoke([System.Windows.Forms.MethodInvoker]{ $supervisor.Close() })
        }
    })
    $supervisor.Add_FormClosing({
        if ($null -ne $workerAsync -and -not $workerAsync.IsCompleted) {
            $workerPowerShell.Stop()
        }
    })
    $workerCompletionTimer.Start()
    $startupStage = 'ui_message_loop'
    [System.Windows.Forms.Application]::Run($supervisor)
}
catch {
    $exceptionType = ConvertTo-StartupDiagnosticToken $_.Exception.GetType().Name
    $errorId = ConvertTo-StartupDiagnosticToken $_.FullyQualifiedErrorId
    Write-StartupLog "worker_start_failed stage=$(ConvertTo-StartupDiagnosticToken $startupStage) exception_type=$exceptionType error_id=$errorId"
    exit 1
}
finally {
    if ($null -ne $workerCompletionTimer) {
        $workerCompletionTimer.Stop()
        $workerCompletionTimer.Dispose()
    }
    if ($null -ne $workerPowerShell) {
        if ($null -ne $workerAsync -and -not $workerAsync.IsCompleted) {
            $workerPowerShell.Stop()
            $null = $workerAsync.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(5))
        }
        if ($null -ne $workerAsync -and $workerAsync.IsCompleted -and -not $workerEndInvoked) {
            try { $null = $workerPowerShell.EndInvoke($workerAsync) } catch {}
        }
        $workerPowerShell.Dispose()
    }
    if ($null -ne $workerRunspace) {
        $workerRunspace.Dispose()
    }
    if ($null -ne $supervisor -and -not $supervisor.IsDisposed) {
        $supervisor.Dispose()
    }
}
