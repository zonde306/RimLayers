param([string]$CandidatePath=(Join-Path $PSScriptRoot 'Artifacts/vehicle-candidate/RimLayers.dll'),[switch]$NormalStartup,[switch]$UseCandidate,[switch]$PlatformLoss,[switch]$World,[switch]$AirRoute,[switch]$Crash,[switch]$Flat,[switch]$BasicOnly,[switch]$Obstruction,[switch]$Manual,[switch]$Expansion,[string]$LoadProfile='', [ValidateSet('vehicle-ground','vehicle-lift','vehicle-ramp','vehicle-flight')][string]$Scenario='vehicle-ground',[int]$TimeoutSeconds=240,[ValidateSet('None','VVE','Technical')][string]$ContentProfile='None',[ValidatePattern('^[A-Za-z0-9_]+$')][string]$VehicleDef='RL_TestVehicle')
$ErrorActionPreference='Stop'
if($PSBoundParameters.ContainsKey('CandidatePath') -and !$UseCandidate){throw 'CandidatePath requires -UseCandidate; omit it to build the default vehicle candidate.'}
$root=Split-Path -Parent $PSScriptRoot
$game=Split-Path -Parent (Split-Path -Parent $root)
if(Get-Process RimWorld -ErrorAction SilentlyContinue){throw 'Close RimWorld before swapping a candidate DLL for this isolated test.'}
$run=Join-Path $PSScriptRoot ('Artifacts/vehicle-ground-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path (Join-Path $run 'Config') | Out-Null
if($Flat -and $Scenario -ne 'vehicle-ground'){throw 'Flat uses vehicle-ground only.'}
$modes=@('generate','load')
if($LoadProfile){
    $source=(Resolve-Path -LiteralPath $LoadProfile).Path
    $artifacts=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Artifacts')).Path
    if((Split-Path -Parent $source) -ne $artifacts){throw 'LoadProfile must be an isolated direct child of Tests/Artifacts.'}
    Copy-Item -LiteralPath (Join-Path $source 'Saves') -Destination $run -Recurse
    Get-ChildItem -LiteralPath $source -Filter '*-baseline.json' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $run}
    $modes=@('load')
}
if($Scenario -eq 'vehicle-lift' -or $World){$modes+=@('recover')}
$live=Join-Path $root 'Assemblies/RimLayers.dll'
$backup=Join-Path $run 'RimLayers.before-test.dll'
$fixture=Join-Path $root 'Defs/VehicleProbe.test.xml'
$textures=@('north','east','south','west') | ForEach-Object {Join-Path $root ('Textures/Things/Building/RimLayers/VehicleFixture_'+$_+'.png')}
foreach($path in @($fixture)+$textures){if(Test-Path -LiteralPath $path){throw "Temporary fixture already exists: $path"}}
if($VehicleDef -ne 'RL_TestVehicle' -and $ContentProfile -eq 'None'){throw 'A real vehicle requires its content profile.'}
$content=switch($ContentProfile){
 'VVE' {'<li>oskarpotocki.vanillafactionsexpanded.core</li><li>oskarpotocki.vanillavehiclesexpanded</li><li>oskarpotocki.vanillavehiclesexpandedtier3</li>'}
 'Technical' {'<li>rimthunder.core</li><li>mo.technicalvehicles</li>'}
 default {''}
}
$version=(Get-Content -LiteralPath (Join-Path $game 'Version.txt') -Raw).Trim()
[IO.File]::WriteAllText((Join-Path $run 'Config/ModsConfig.xml'),('<ModsConfigData><version>'+$version+'</version><activeMods><li>brrainz.harmony</li><li>ludeon.rimworld</li><li>baal.vehicleframework</li>'+$content+'<li>rimlayers.core</li></activeMods><knownExpansions><li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li></knownExpansions></ModsConfigData>'),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $run 'Config/Prefs.xml'),'<PrefsData><devMode>True</devMode><runInBackground>True</runInBackground><edgeScreenScroll>False</edgeScreenScroll><langFolderName>English</langFolderName></PrefsData>',[Text.UTF8Encoding]::new($false))
if(!$UseCandidate){
    & dotnet build (Join-Path $root 'Source/RimLayers/RimLayers.csproj') -c Release '-p:OutputPath=../../Tests/Artifacts/vehicle-candidate/' --nologo
    if($LASTEXITCODE -ne 0){throw 'Candidate build failed'}
}
if(!(Test-Path -LiteralPath $CandidatePath -PathType Leaf)){throw "No built vehicle candidate exists: $CandidatePath"}
$CandidatePath=(Resolve-Path -LiteralPath $CandidatePath).Path
Copy-Item -LiteralPath $live -Destination $backup
$created=@()
$process=$null
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fixtures/VehicleProbe.xml') -Destination $fixture
    $created+=$fixture
    # Existing RimLayers art is only a recognizable test marker, not a shipped vehicle asset.
    foreach($path in $textures){
        Copy-Item -LiteralPath (Join-Path $root 'Textures/Things/Building/RimLayers/Stairs.png') -Destination $path
        $created+=$path
    }
    Copy-Item -LiteralPath $CandidatePath -Destination $live
    foreach($mode in $modes){
        $gameMode=if($mode -eq 'recover'){'load'}else{$mode}
        $arguments=@('-savedatafolder='+$run,'-quicktest','-rimlayersv1test','-rimlayerscase='+$Scenario,'-rimlayersmode='+$gameMode,'-rimlayersvehicledef='+$VehicleDef,'-screen-width=1024','-screen-height=768','-screen-fullscreen=0')
        if(!$NormalStartup){$arguments+='-rimlayersvehiclesprototype'}
        if($mode -eq 'recover'){Copy-Item -LiteralPath (Join-Path $run ('load-'+$Scenario+'-results.json')) -Destination (Join-Path $run ('first-load-'+$Scenario+'-results.json'))}
        if($mode -eq 'recover'){$arguments+='-rimlayersvehiclerecovery'}
        if($PlatformLoss){$arguments+='-rimlayersvehicleplatformloss'}
        if($World){$arguments+='-rimlayersvehicleworld'}
        if($AirRoute){$arguments+='-rimlayersvehicleairroute'}
        if($Crash){$arguments+='-rimlayersvehiclecrash'}
        if($Flat){$arguments+='-rimlayersvehicleflat'}
        if($BasicOnly){$arguments+='-rimlayersvehiclebasic'}
        if($Obstruction){$arguments+='-rimlayersvehicleobstruction'}
        if($Manual){$arguments+='-rimlayersvehiclemanual'}
        if($Expansion){$arguments+='-rimlayersvehicleexpansion'}
        $process=Start-Process -FilePath (Join-Path $game 'RimWorld.exe') -WorkingDirectory $game -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run ($mode+'-stdout.log')) -RedirectStandardError (Join-Path $run ($mode+'-stderr.log'))
        if(!$process.WaitForExit($TimeoutSeconds*1000)){$process.Kill();$process.WaitForExit();throw "Vehicle scenario timed out: $run"}
        $gameLog='C:/Users/zonre/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log'
        if(Test-Path -LiteralPath $gameLog){Copy-Item -LiteralPath $gameLog -Destination (Join-Path $run ($mode+'-Player.log'))}
        if($mode -eq 'recover'){Move-Item -LiteralPath (Join-Path $run ('load-'+$Scenario+'-results.json')) -Destination (Join-Path $run ('recover-'+$Scenario+'-results.json'))}
        $report=Join-Path $run ($mode+'-'+$Scenario+'-results.json')
        if(!(Test-Path -LiteralPath $report)){throw "Missing vehicle report: $run"}
        $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $result.checks | Format-Table name,pass,detail -AutoSize
        if($result.failures -ne 0){throw "Vehicle scenario failed: $report"}
    }
    Write-Output "Reports: $run"
} finally {
    if($null -ne $process -and !$process.HasExited){$process.Kill();$process.WaitForExit()}
    Copy-Item -LiteralPath $backup -Destination $live
    foreach($path in $created){Remove-Item -LiteralPath $path}
}
