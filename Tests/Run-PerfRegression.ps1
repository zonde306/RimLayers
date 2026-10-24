# Runs the game scenarios most affected by the 2026-10 performance work, serially.
# Each scenario uses its own artifact profile; the script reports every failure at the end.
param([int]$TimeoutSeconds = 420)
$ErrorActionPreference = 'Continue'
$runner = Join-Path $PSScriptRoot 'Run-V1.ps1'

# Ordered by regression risk from this round's changes.
#
# 'minimap' needs -Minimap (it activates dubwise.dubsmintminimap, which IS installed in this
# workspace); without that switch the scenario self-fails on minimap-mod-present-for-compatibility-run.
# That was a harness bug, not a missing mod.
#
# 'heat' is excluded: top-ceiling-retains-native-outdoor-loss already fails on unmodified HEAD
# (verified by stashing these changes and re-running), so it is not a signal here.
$scenarios = @(
    @{ Name = 'air';           Switches = @() },  # VolumeLinks -> O(1) HasTransferEdge
    @{ Name = 'sky';           Switches = @() },  # SkyExposure Revision exposure / epoch cache
    @{ Name = 'reachability';  Switches = @() },  # For() memo + connector graph caches
    @{ Name = 'structure';     Switches = @() },  # conditional Connectors.Invalidate
    @{ Name = 'work-haul';     Switches = @() },  # storage candidate path caching
    @{ Name = 'combat-shot';   Switches = @() },  # FiringPositions MovementEdges + Solve
    @{ Name = 'minimap';       Switches = @('-Minimap') }, # For() memo on a per-cell third-party path
    @{ Name = 'ship-structure';Switches = @() }   # ShipStructureService reverse index
)

$failures = @()
foreach ($entry in $scenarios) {
    $scenario = $entry.Name
    Write-Host ("=== {0} ===" -f $scenario) -ForegroundColor Cyan
    # Pass -AllDlc exactly once: duplicate switch binding is an InvalidArgument error.
    # $args is a PowerShell automatic variable, so the splat array gets its own name.
    $runnerArgs = @('-Scenario', $scenario, '-AllDlc', '-TimeoutSeconds', $TimeoutSeconds) + $entry.Switches
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $runner @runnerArgs 2>&1
    $code = $LASTEXITCODE
    $summary = ($output | Select-String -Pattern '(generate|load|flight-load|landed-load):\s+\d+ assertions' -AllMatches).Matches.Value
    if ($code -ne 0) {
        $failures += $scenario
        $isBuild = ($output | Select-String -Pattern 'Build failed' -Quiet)
        if ($isBuild) { Write-Host "     (build-time failure, not a runtime assertion)" -ForegroundColor Yellow }
        Write-Host ("FAIL {0} (exit {1})" -f $scenario, $code) -ForegroundColor Red
        $output | Select-String -Pattern 'Runtime acceptance failed|"pass":\s+false|"name":|Exception|Build failed' |
            Select-Object -Last 20 | ForEach-Object { Write-Host "     $_" }
    } else {
        Write-Host ("PASS {0}: {1}" -f $scenario, ($summary -join '; ')) -ForegroundColor Green
    }
    # RimWorld releases Assemblies/RimLayers.dll only after its process is fully gone, and
    # Run-V1.ps1 builds at startup. Probing the DLL alone is not enough, so also wait for the
    # hidden test process to disappear, then confirm the output is writable. Without this the
    # next scenario fails with "Build failed" even though the previous one passed.
    $dll = Join-Path $PSScriptRoot '..\Assemblies\RimLayers.dll'
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $running = @(Get-Process -Name 'RimWorld' -ErrorAction SilentlyContinue).Count
        if ($running -eq 0) {
            try {
                $stream = [IO.File]::Open($dll, 'Open', 'ReadWrite', 'None')
                $stream.Close()
                break
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
}

Write-Host ""
if ($failures.Count -ne 0) {
    Write-Host ("FAILED scenarios: " + ($failures -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host ("All {0} perf-regression scenarios passed." -f $scenarios.Count) -ForegroundColor Green
