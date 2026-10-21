param([ValidateSet('elevators','cargo','gravship','structure')][string[]]$Scenarios=@('elevators','cargo','gravship'),[switch]$NoFramework)
$ErrorActionPreference='Stop'
if(Get-Process RimWorld -ErrorAction SilentlyContinue){throw 'Close RimWorld before running isolated regression.'}
$root=Split-Path -Parent $PSScriptRoot
$run=Join-Path $PSScriptRoot ('Artifacts/vehicle-regression-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run | Out-Null
$live=Join-Path $root 'Assemblies/RimLayers.dll'
$backup=Join-Path $run 'RimLayers.before-test.dll'
Copy-Item -LiteralPath $live -Destination $backup
try {
    foreach($scenario in $Scenarios){
        if(Get-Process RimWorld -ErrorAction SilentlyContinue){throw 'A RimWorld process is active; regression did not swap another candidate.'}
        & (Join-Path $PSScriptRoot 'Run-V1.ps1') -Scenario $scenario -Vehicles:(!$NoFramework)
        if($LASTEXITCODE -ne 0){throw "Regression failed: $scenario"}
    }
} finally {
    # The renderer/loader can briefly retain the DLL after its test process exits.
    # Retry the same scoped restore; never terminate another game to acquire the file.
    for($attempt=0;;$attempt++){
        try {Copy-Item -LiteralPath $backup -Destination $live; break}
        catch [System.IO.IOException] {
            if($attempt -ge 19 -or (Get-Process RimWorld -ErrorAction SilentlyContinue)){throw}
            Start-Sleep -Milliseconds 250
        }
    }
}
