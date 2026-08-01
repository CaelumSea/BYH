<#!
.SYNOPSIS
    Runs BYH under a lightweight external crash monitor.

.DESCRIPTION
    BYH is a tray/GUI process and normally has no useful console output. This
    monitor records process lifetime, exit code, memory/CPU samples, stdout,
    stderr, and matching Windows Application Error / .NET Runtime / WER events.
    It does not change registry settings or install a debugger. The output
    directory is safe to archive and attach to a bug report.

.EXAMPLE
    pwsh -File tools\monitor-byh-crash.ps1 `
        -ExePath artifacts\publish\win-x64-nativeuia\BYH.exe `
        -ArgumentList @('--probe-vision','0','0','3200','1800') `
        -OutputDirectory artifacts\qa\crash-monitor-large
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [string[]]$ArgumentList = @(),

    [string]$OutputDirectory = (Join-Path (Get-Location) 'artifacts\qa\crash-monitor'),

    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 180,

    [ValidateRange(50, 5000)]
    [int]$SampleMilliseconds = 250
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$exeName = [IO.Path]::GetFileName($resolvedExe)
$exeStem = [IO.Path]::GetFileNameWithoutExtension($resolvedExe)
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
}
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$startedAt = Get-Date
$startedAtUtc = $startedAt.ToUniversalTime()
$samples = [Collections.Generic.List[object]]::new()
$process = [Diagnostics.Process]::new()
$startFailure = $null
$timedOut = $false

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedExe
    $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($resolvedExe)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Process.Start returned false."
    }
}
catch {
    $startFailure = $_.Exception.ToString()
}

$stdout = ''
$stderr = ''
$exitCode = $null
$processId = $null

if ($startFailure -eq $null) {
    $processId = $process.Id
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $sample = [ordered]@{
            TimestampUtc = [DateTime]::UtcNow.ToString('o')
            Pid = $process.Id
            WorkingSetBytes = $null
            PrivateMemoryBytes = $null
            VirtualMemoryBytes = $null
            CpuMilliseconds = $null
        }
        try {
            $sample.WorkingSetBytes = $process.WorkingSet64
            $sample.PrivateMemoryBytes = $process.PrivateMemorySize64
            $sample.VirtualMemoryBytes = $process.VirtualMemorySize64
            $sample.CpuMilliseconds = [Math]::Round($process.TotalProcessorTime.TotalMilliseconds, 1)
        }
        catch {
            $sample.SampleError = $_.Exception.Message
        }
        $samples.Add([pscustomobject]$sample)
        Start-Sleep -Milliseconds $SampleMilliseconds
    }

    if (-not $process.HasExited) {
        $timedOut = $true
        try {
            $process.Kill($true)
        }
        catch {
            $samples.Add([pscustomobject]@{
                TimestampUtc = [DateTime]::UtcNow.ToString('o')
                Pid = $process.Id
                WorkingSetBytes = $null
                PrivateMemoryBytes = $null
                VirtualMemoryBytes = $null
                CpuMilliseconds = $null
                KillError = $_.Exception.ToString()
            })
        }
    }

    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
}

$finishedAt = Get-Date
$eventRecords = @()
$eventQueryError = $null
try {
    $rawEvents = @(Get-WinEvent -FilterHashtable @{
        LogName = 'Application'
        StartTime = $startedAt.AddSeconds(-3)
    } -ErrorAction SilentlyContinue)
    $eventRecords = @(
        $rawEvents |
        Where-Object {
            $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') -or
            $_.Message -match [Regex]::Escape($exeName) -or
            $_.Message -match [Regex]::Escape($exeStem)
        } |
        Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message
    )
}
catch {
    $eventQueryError = $_.Exception.ToString()
}

$summary = [ordered]@{
    ExePath = $resolvedExe
    ExeName = $exeName
    Arguments = @($ArgumentList)
    Pid = $processId
    StartedAt = $startedAt.ToString('o')
    StartedAtUtc = $startedAtUtc.ToString('o')
    FinishedAt = $finishedAt.ToString('o')
    DurationMilliseconds = [Math]::Round(($finishedAt - $startedAt).TotalMilliseconds, 1)
    ExitCode = $exitCode
    TimedOut = $timedOut
    StartFailure = $startFailure
    SampleCount = $samples.Count
    MaxWorkingSetBytes = ($samples | Where-Object { $null -ne $_.WorkingSetBytes } | Measure-Object -Property WorkingSetBytes -Maximum).Maximum
    MaxPrivateMemoryBytes = ($samples | Where-Object { $null -ne $_.PrivateMemoryBytes } | Measure-Object -Property PrivateMemoryBytes -Maximum).Maximum
    WindowsApplicationEvents = $eventRecords.Count
    WindowsEventQueryError = $eventQueryError
    MonitorVersion = '1'
}

$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $resolvedOutput 'summary.json') -Encoding UTF8
$samples | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $resolvedOutput 'samples.json') -Encoding UTF8
$eventRecords | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $resolvedOutput 'application-events.json') -Encoding UTF8
$stdout | Set-Content -LiteralPath (Join-Path $resolvedOutput 'stdout.log') -Encoding UTF8
$stderr | Set-Content -LiteralPath (Join-Path $resolvedOutput 'stderr.log') -Encoding UTF8

Write-Output ("Monitor output: {0}" -f $resolvedOutput)
Write-Output ("PID={0} ExitCode={1} TimedOut={2} DurationMs={3} Samples={4} Events={5}" -f `
    $processId, $exitCode, $timedOut, $summary.DurationMilliseconds, $samples.Count, $eventRecords.Count)
if ($eventRecords.Count -gt 0) {
    $eventRecords | ForEach-Object {
        Write-Output ("Event {0}/{1}: {2}" -f $_.ProviderName, $_.Id, $_.TimeCreated)
    }
}
if ($eventQueryError) {
    Write-Output ("Event query error: {0}" -f $eventQueryError)
}
