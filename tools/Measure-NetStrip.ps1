<#
.SYNOPSIS
    Measures a running NetStrip against the idle budget (CPU < 0.5 %, working set <= 60 MB).

.DESCRIPTION
    Samples CPU over -Seconds, then prints memory, threads, handles and which heavyweight native
    modules are loaded (Direct3D / GPU driver, UI Automation, WinRT), so a miss points at its cause.
    Run it after NetStrip has been idle for at least 5 minutes (plan Task 13, step 4).

.EXAMPLE
    .\tools\Measure-NetStrip.ps1
    .\tools\Measure-NetStrip.ps1 -Seconds 120
#>
param([int]$Seconds = 60)

$p = Get-Process -Name NetStrip -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Error 'NetStrip is not running.'; exit 1 }

Write-Host "Sampling NetStrip (PID $($p.Id)) for $Seconds s..."
$cpu0 = $p.TotalProcessorTime
$t0 = Get-Date
Start-Sleep -Seconds $Seconds
$p.Refresh()
$elapsed = ((Get-Date) - $t0).TotalMilliseconds
# Same normalisation as Task Manager: share of all logical processors.
$cpu = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds / $elapsed / [Environment]::ProcessorCount * 100

$perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)"
function MB([double]$bytes) { '{0,7:N1} MB' -f ($bytes / 1MB) }
function Verdict([bool]$ok) { if ($ok) { 'PASS' } else { 'FAIL' } }

$ws = $p.WorkingSet64
Write-Host ''
Write-Host ('Uptime            {0:hh\:mm\:ss}' -f ((Get-Date) - $p.StartTime))
Write-Host ('CPU (avg)         {0,7:N2} %    budget < 0.5 %   {1}' -f $cpu, (Verdict ($cpu -lt 0.5)))
Write-Host ('Working set       {0}   budget <= 60 MB  {1}' -f (MB $ws), (Verdict ($ws -le 60MB)))
Write-Host ('Private WS        {0}   (Task Manager "Memory" column)' -f (MB $perf.WorkingSetPrivate))
Write-Host ('Private bytes     {0}' -f (MB $p.PrivateMemorySize64))
Write-Host ('Threads           {0,7}' -f $p.Threads.Count)
Write-Host ('Handles           {0,7}' -f $p.HandleCount)

$groups = [ordered]@{
    'Direct3D / GPU driver' = '^(d3d9|d3d10warp|d3d11|dxgi|nvd3dum|nvwgf2um|nvldumd|aticfx|amdxc|atiu|igd).*\.dll$'
    'UI Automation client'  = '^(UIAutomationCore|UIAutomationClient|UIAutomationClientSideProviders|UIAutomationTypes)\.dll$'
    'WinRT / toasts'        = '^(WinRT\.Runtime|Microsoft\.Windows\.SDK\.NET|Microsoft\.Toolkit\.Uwp\.Notifications)\.dll$'
    'Fluent theme'          = '^PresentationFramework\.Fluent\.dll$'
}
$modules = @($p.Modules | ForEach-Object { $_.ModuleName })
Write-Host ''
Write-Host 'Heavyweight modules loaded:'
foreach ($name in $groups.Keys) {
    $hits = @($modules | Where-Object { $_ -match $groups[$name] } | Sort-Object -Unique)
    $list = if ($hits.Count) { $hits -join ', ' } else { '-' }
    Write-Host ('  {0,-22} {1}' -f $name, $list)
}
