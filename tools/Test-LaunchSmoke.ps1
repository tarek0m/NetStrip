<#
.SYNOPSIS
    Launch smoke test: starts the built NetStrip with simulated traffic, lets it exit by itself, and checks its log.

.DESCRIPTION
    Runs NetStrip.exe --fake --exit-after <ExitAfter> and fails (exit code 1) when the app does not exit by itself
    within -TimeoutSeconds, exits with a code other than 0, or its log for this run lacks "AppHost started" or
    "NetStrip exiting (code 0)" or has an ERROR line. WARN lines (for example the floating fallback when there is no
    taskbar to attach to) are allowed. The run's log is printed either way.

    The app finds its log folder with Environment.GetFolderPath, which on Windows reads the known-folder path and
    ignores a changed LOCALAPPDATA variable, so the run cannot be pointed at a private folder. Instead the script
    records how long each existing netstrip-*.log is before launch and reads only what was appended after that, which
    also covers a run that crosses midnight into a new day's file.

    Used by CI (.github/workflows/ci.yml); also runs locally after `dotnet build -c Release` when NetStrip is not
    already running.

.EXAMPLE
    .\tools\Test-LaunchSmoke.ps1
    .\tools\Test-LaunchSmoke.ps1 -Exe src\NetStrip\bin\Debug\net10.0-windows\NetStrip.exe -ExitAfter 10
#>
#Requires -Version 7
param(
    # Defaults to the newest NetStrip.exe under src/NetStrip/bin/Release (any target framework folder).
    [string]$Exe,
    [int]$ExitAfter = 20,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

if (-not $Exe) {
    $bin = Join-Path $PSScriptRoot '..\src\NetStrip\bin\Release'
    $found = @(Get-ChildItem -Path $bin -Recurse -Filter NetStrip.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($found.Count -eq 0) { Write-Error "No NetStrip.exe under $bin; build with 'dotnet build -c Release' first." }
    if ($found.Count -gt 1) { Write-Warning "Several builds found; using the newest:`n  $($found.FullName -join "`n  ")" }
    $Exe = $found[0].FullName
}
$Exe = (Resolve-Path $Exe).Path

if (Get-Process -Name NetStrip -ErrorAction SilentlyContinue) {
    Write-Error 'NetStrip is already running; the new instance would hand over to it and exit. Close it first.'
}

$logDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NetStrip\logs'
$before = @{}
foreach ($f in @(Get-ChildItem -Path $logDir -Filter 'netstrip-*.log' -ErrorAction SilentlyContinue)) {
    $before[$f.Name] = $f.Length
}

$psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
foreach ($arg in '--fake', '--exit-after', "$ExitAfter") { $psi.ArgumentList.Add($arg) }
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $Exe

Write-Host "Launching $Exe --fake --exit-after $ExitAfter (timeout $TimeoutSeconds s)"
$started = Get-Date
$p = [System.Diagnostics.Process]::Start($psi)
$exited = $p.WaitForExit($TimeoutSeconds * 1000)
$elapsed = ((Get-Date) - $started).TotalSeconds

$failures = [System.Collections.Generic.List[string]]::new()
if ($exited) {
    $p.WaitForExit()   # lets the exit code settle
    $code = $p.ExitCode
    Write-Host ('Exited after {0:N1} s with code {1} (0x{1:X8})' -f $elapsed, $code)
    if ($code -ne 0) { $failures.Add("exit code was $code, expected 0") }
}
else {
    $failures.Add("did not exit by itself within $TimeoutSeconds s")
    try { $p.Kill($true) } catch { Write-Warning "Could not kill PID $($p.Id): $_" }
}

# Only the text this run appended to each day's log (a file that shrank was pruned and recreated, so read it all).
$text = [System.Text.StringBuilder]::new()
foreach ($f in @(Get-ChildItem -Path $logDir -Filter 'netstrip-*.log' -ErrorAction SilentlyContinue | Sort-Object Name)) {
    $offset = if ($before.ContainsKey($f.Name) -and $before[$f.Name] -le $f.Length) { $before[$f.Name] } else { 0 }
    if ($offset -eq $f.Length) { continue }
    $stream = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($offset, 'Begin')
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false))
        [void]$text.Append($reader.ReadToEnd())
    }
    finally { $stream.Dispose() }
}
$log = $text.ToString()

Write-Host ''
Write-Host "----- NetStrip log for this run ($logDir) -----"
if ($log) { Write-Host $log.TrimEnd() } else { Write-Host '(nothing was written)' }
Write-Host '----- end of log -----'
Write-Host ''

if ($log -notmatch '(?m)^\S+ \S+ INFO AppHost started') { $failures.Add('log lacks "AppHost started"') }
if ($exited -and $log -notmatch '(?m)^\S+ \S+ INFO NetStrip exiting \(code 0\)') {
    $failures.Add('log lacks "NetStrip exiting (code 0)" (the normal shutdown path did not finish)')
}
# Timestamped ERROR lines only: an exception's text on the lines below a WARN cannot trip this.
$errors = @([regex]::Matches($log, '(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ERROR .*$') | ForEach-Object { $_.Value.TrimEnd() })
if ($errors.Count) { $failures.Add("log has $($errors.Count) ERROR line(s):`n    $($errors -join "`n    ")") }
$warnings = [regex]::Matches($log, '(?m)^\S+ \S+ WARN ').Count
if ($warnings) { Write-Host "$warnings WARN line(s) (allowed)." }

if ($failures.Count) {
    # A crash before the log exists only shows up here (.NET Runtime event 1026, Application Error event 1000).
    $events = @()
    try {
        $events = @(Get-WinEvent -ErrorAction SilentlyContinue -FilterHashtable @{
                LogName = 'Application'; StartTime = $started.AddSeconds(-5)
                ProviderName = '.NET Runtime', 'Application Error', 'Windows Error Reporting'
            })
    }
    catch { }
    if ($events.Count) {
        Write-Host '----- Application event log since launch -----'
        $events | Sort-Object TimeCreated | ForEach-Object { Write-Host "$($_.TimeCreated) $($_.ProviderName) $($_.Id)`n$($_.Message)`n" }
        Write-Host '----- end of events -----'
    }
}

if ($failures.Count) {
    foreach ($f in $failures) {
        # Workflow-command escaping, so each failure shows as one annotation on the run.
        $msg = $f.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error title=Launch smoke test::$msg"
    }
    Write-Host "Launch smoke test FAILED: $($failures.Count) problem(s)."
    exit 1
}
Write-Host 'Launch smoke test passed.'
