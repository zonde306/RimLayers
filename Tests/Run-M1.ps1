param(
    [string]$GameRoot = 'G:/SteamLibrary/steamapps/common/RimWorld',
    [int]$TimeoutSeconds = 240,
    [switch]$River,
    [ValidateSet("English","ChineseSimplified")][string]$Language="English"
)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $PSScriptRoot ('Artifacts/m1-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$configDir=Join-Path $runRoot 'Config'
New-Item -ItemType Directory -Path $configDir | Out-Null
[IO.File]::WriteAllText((Join-Path $configDir 'Prefs.xml'),('<PrefsData><runInBackground>True</runInBackground><langFolderName>'+$Language+'</langFolderName></PrefsData>'),[Text.UTF8Encoding]::new($false))
& dotnet build (Join-Path $modRoot 'Source/RimLayers/RimLayers.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& dotnet run --project (Join-Path $PSScriptRoot 'LayoutTests/LayoutTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Layout tests failed' }
foreach ($mode in @('generate', 'load')) {
    $arguments = @(
        ('-savedatafolder=' + $runRoot),
        '-mod=brrainz.harmony,rimlayers.core',
        '-quicktest', '-rimlayerstest', ('-rimlayersmode=' + $mode), ('-rimlayerslanguage='+$Language),
        '-screen-width=1024', '-screen-height=768', '-screen-fullscreen=0'
    )
    if ($River) { $arguments += '-rimlayersriver' }
    $process = Start-Process -FilePath (Join-Path $GameRoot 'RimWorld.exe') -WorkingDirectory $GameRoot -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot ($mode + '-stdout.log')) -RedirectStandardError (Join-Path $runRoot ($mode + '-stderr.log'))
    @{ mode=$mode; processId=$process.Id; runRoot=$runRoot } | ConvertTo-Json -Compress | Write-Output
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited -and $watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Start-Sleep -Seconds 2
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        # This is the exact child process launched above, never an existing player session.
        $process.Kill()
        throw ("Timed out in " + $mode + "; inspect " + $runRoot)
    }
    Write-Output ($mode + " exit code: " + $process.ExitCode)
    $resultPath = Join-Path $runRoot ($mode + '-results.json')
    if (-not (Test-Path -LiteralPath $resultPath)) { throw ("No runtime results: " + $mode + "; inspect " + $runRoot) }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Write-Output ($mode + ': ' + $result.checks.Count + ' assertions, ' + $result.failures + ' failures')
    if ($result.failures -ne 0) {
        $result.checks | Where-Object { -not $_.pass } | ConvertTo-Json -Depth 5 | Write-Output
        throw ("Runtime acceptance failed: " + $mode)
    }
}
Write-Output ('M1 runtime acceptance passed: ' + $runRoot)
