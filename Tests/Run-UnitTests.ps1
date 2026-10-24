# Runs every standalone, game-independent test project and reports one summary.
# These suites compile individual source files directly, so they validate pure logic
# (layout tables, route bounds, incremental edge sums, exchange math) without RimWorld.
param(
    [string]$Configuration = 'Release',
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem -Path $PSScriptRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName ($_.Name + '.csproj')) } |
    Sort-Object Name

$failures = @()
$passed = 0
foreach ($project in $projects) {
    $csproj = Join-Path $project.FullName ($project.Name + '.csproj')
    # Skip projects that need the game assembly; those are driven by Run-V1.ps1 instead.
    $needsGame = Select-String -Path $csproj -Pattern 'Assembly-CSharp' -Quiet
    if ($needsGame) {
        if (-not $Quiet) { Write-Host ("SKIP {0} (requires game assembly)" -f $project.Name) }
        continue
    }
    $output = & dotnet run --project $csproj -c $Configuration -v q --nologo 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $failures += $project.Name
        Write-Host ("FAIL {0}" -f $project.Name) -ForegroundColor Red
        $output | Select-Object -Last 12 | ForEach-Object { Write-Host "     $_" }
    } else {
        $passed++
        if (-not $Quiet) {
            $last = ($output | Where-Object { $_ -match '^\s*(PASS|Gas|HeatExchange)' } | Select-Object -Last 1)
            Write-Host ("PASS {0}: {1}" -f $project.Name, $last)
        }
    }
}

if ($failures.Count -ne 0) {
    Write-Host ("`n{0} suite(s) failed: {1}" -f $failures.Count, ($failures -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host ("`nAll {0} game-independent test suites passed." -f $passed) -ForegroundColor Green
