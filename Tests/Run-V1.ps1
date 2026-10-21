param(
    [string]$GameRoot = 'G:/SteamLibrary/steamapps/common/RimWorld',
    [int]$TimeoutSeconds = 240,
    [ValidateSet("English", "ChineseSimplified")][string]$Language = "English",
    [ValidateSet("ship-landing-items", "temperature", "cargo", "reinstall", "jump", "transition", "ship-vge", "shaft-extension", "settings-behavior", "settings-perf", "settings-quota", "settings-ui", "settings-recovery", "underground", "combat-raid-audit", "combat-raid-guns", "combat-upper", "combat-grenade", "combat-raid-melee", "combat-raid", "combat-raid-isolated", "combat-manned", "combat-3d", "ship-coexistence", "mountains", "ship-structure", "elevators", "stairs-bootstrap", "expansion", "gravship", "corpse-refresh", "command-input", "reachability", "presentation", "usability", "migration", "world-exit", "home", "navigation-door", "scale", "lord", "roping", "mechanitor", "navigation", "navigation-maze", "navigation-rules", "work", "work-save", "work-container", "work-haul", "work-hunger", "structure", "skyfall", "skyfall-shuttle", "combat-geometry", "combat-shot", "combat-ai", "combat-turret", "combat-policy", "combat-shields", "combat-shield-save", "combat-shield-pass", "combat-explosion", "combat-delay", "combat-grenade-flee", "combat-roof", "combat-ballistics", "combat-blast-effects", "utilities", "air", "heat", "sky", "hazards", "hazards-timed")][string]$Scenario = "navigation",
    [switch]$CheckPure,
    [switch]$TemperatureCore,
    [ValidateSet("travel","handling","queue")][string]$CargoCheckpoint="travel",
    [switch]$NativeShip,
    [ValidateRange(0,2)][int]$Vge=0,
    [switch]$LegacyFlight,
    [ValidateRange(3,6)][int]$ShipDecks=3,
    [switch]$ShipVisual,
    [switch]$BootstrapLift,
    [switch]$LiftWork,
    [switch]$Shelf,
    [switch]$TravelMultipliers,
    [ValidateRange(0,2)][int]$EngineLayer=0,
    [switch]$Nanami,
    [switch]$Dashboard,
    [switch]$GuiPerf,
    [switch]$CorpseDamage,
    [switch]$FacialAnimation,
    [switch]$PlayerMods,
    [switch]$River,
    [switch]$Space,
    [switch]$ExistingMap,
    [switch]$Har,
    [switch]$AllDlc,
    [int]$RaidSeed=17,
    [string]$RaidAuditSave,
    [ValidateRange(120,325)][int]$MapSize=120,
    [ValidateRange(1,120)][int]$PawnCount=20,
    [ValidateRange(2,3)][int]$Layers=3,
    [ValidateRange(1024,3840)][int]$ScreenWidth=1024,
    [ValidateRange(720,2160)][int]$ScreenHeight=768,
    [ValidateRange(1,2)][double]$UiScale=1
)
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $PSScriptRoot (('Artifacts/v1-' + $Scenario + '-') + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
# Native -mod forcibly adds every installed compatible official DLC, even with an explicit config.
# Do not pass it for a controlled DLC matrix; use this profile's activeMods instead.
# Declare both active and known expansions in this OWN profile, never the player profile.
$expansions = @('ludeon.rimworld.royalty','ludeon.rimworld.ideology','ludeon.rimworld.biotech','ludeon.rimworld.anomaly','ludeon.rimworld.odyssey')
$active = @('ludeon.rimworld')
if ($AllDlc) { $active += $expansions }
elseif ($Scenario -in @('jump','combat-shields','combat-shield-save','combat-shield-pass')) { $active += 'ludeon.rimworld.royalty' }
elseif ($Scenario -in @('mechanitor','temperature') -and -not ($Scenario -eq 'temperature' -and $TemperatureCore)) { $active += 'ludeon.rimworld.biotech' }
elseif ($Scenario -in @('ship-landing-items','ship-coexistence','gravship','ship-structure','work-container')) { $active += 'ludeon.rimworld.odyssey' }
$active += @('brrainz.harmony','rimlayers.core')
if($Vge -gt 0){$active=@($active|Where-Object {$_ -ne 'rimlayers.core'});$active+=@('oskarpotocki.vanillafactionsexpanded.core','vanillaexpanded.gravship');if($Vge -eq 2){$active+='vanillaexpanded.gravship2'};$active+='rimlayers.core'}
if($Dashboard){$active+=@('rimiris.rimiris.beta','rimiris.dashboard')}
if($GuiPerf){$active+='wowgag.guiperformancepatch'}
if($FacialAnimation){$active+=@('nals.facialanimation','nals.facialanimationexperimentals')}
if($Har){$active+='erdelf.humanoidalienraces'}
if($Nanami){$active += 'shimakaze.nanami.startrail'}
if($PlayerMods){
    [xml]$playerConfig = Get-Content -LiteralPath 'C:/Users/zonre/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml' -Raw
    $active = @($playerConfig.ModsConfigData.activeMods.li | ForEach-Object { [string]$_ })
}
if($Scenario -in @('scale','settings-perf')) {
    $hardware=@{cpu=(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name);logicalProcessors=[Environment]::ProcessorCount;physicalMemoryBytes=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;os=[Environment]::OSVersion.VersionString;requestedMapSize=$MapSize;requestedPawns=$PawnCount;requestedLayers=$(if($Scenario -eq 'settings-perf'){11}else{$Layers})}
    [IO.File]::WriteAllText((Join-Path $runRoot 'scale-machine.json'),($hardware|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
}
$configDir = Join-Path $runRoot 'Config'
New-Item -ItemType Directory -Path $configDir | Out-Null
# The renderer still runs in a hidden test process. Native defaults suspend updates
# when unfocused; that is not a simulation failure or a valid load benchmark.
[IO.File]::WriteAllText((Join-Path $configDir 'Prefs.xml'),'<PrefsData><devMode>True</devMode><runInBackground>True</runInBackground><edgeScreenScroll>False</edgeScreenScroll><langFolderName>' + $Language + '</langFolderName></PrefsData>',[Text.UTF8Encoding]::new($false))
$version = (Get-Content -LiteralPath (Join-Path $GameRoot 'Version.txt') -Raw).Trim()
$activeXml = ($active | ForEach-Object { '<li>' + $_ + '</li>' }) -join ''
$knownXml = ($expansions | ForEach-Object { '<li>' + $_ + '</li>' }) -join ''
$config = '<?xml version="1.0" encoding="utf-8"?><ModsConfigData><version>' + $version + '</version><activeMods>' + $activeXml + '</activeMods><knownExpansions>' + $knownXml + '</knownExpansions></ModsConfigData>'
[IO.File]::WriteAllText((Join-Path $configDir 'ModsConfig.xml'),$config,[Text.UTF8Encoding]::new($false))
& dotnet build (Join-Path $modRoot 'Source/RimLayers/RimLayers.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($CheckPure) {
    foreach ($name in @('TransitionTests', 'LayoutTests', 'NavigationTests', 'CombatGeometryTests','GasExchangeTests','HeatExchangeTests','ShipTopologyTests')) {
        & dotnet run --project (Join-Path $PSScriptRoot ($name + '/' + $name + '.csproj')) -c Release
        if ($LASTEXITCODE -ne 0) { throw ($name + ' failed') }
    }
}
if($Scenario -eq 'combat-raid-audit') {
    if(-not $RaidAuditSave -or -not (Test-Path -LiteralPath $RaidAuditSave -PathType Leaf)){throw 'RaidAuditSave must name an existing isolated raid save'}
    $saves=Join-Path $runRoot 'Saves';New-Item -ItemType Directory -Path $saves | Out-Null
    Copy-Item -LiteralPath $RaidAuditSave -Destination (Join-Path $saves 'RimLayers_V1RaidEnd.rws')
}
$modes = if($Scenario -eq 'shaft-extension'){@('generate','load','completed-load')} elseif($Scenario -eq 'combat-raid-audit'){@('load')} elseif($Scenario -eq "combat-manned"){@("generate","load","flight-load")} elseif($Scenario -eq "gravship"){@("generate","load","landed-load")} elseif($Scenario -eq "migration"){@("generate","load","converted-load")} elseif ($Scenario -in @('temperature','cargo','jump','transition','ship-vge','shaft-extension','settings-behavior','settings-recovery','combat-grenade','combat-manned','ship-coexistence','underground','mountains','elevators','stairs-bootstrap','expansion','reachability','presentation','usability','migration','world-exit','home','scale','lord','roping','mechanitor','navigation','navigation-maze','work-save','work-container','structure','skyfall','combat-shot','combat-shield-save','combat-shield-pass','combat-explosion','combat-delay','combat-grenade-flee','utilities','air','heat','sky','hazards','hazards-timed')) { @('generate', 'load') } else { @('generate') }
foreach ($mode in $modes) {
    $arguments = @(
        ('-savedatafolder=' + $runRoot),
        $(if ($AllDlc) { '-rimlayersalldlc' } else { '-rimlayerscoredlc' }),
        '-quicktest', '-rimlayersv1test', ('-rimlayersmode=' + $(if($mode -in @('converted-load','landed-load','flight-load','completed-load')){'load'}else{$mode})), ('-rimlayerscase=' + $Scenario),
        ('-screen-width='+$ScreenWidth), ('-screen-height='+$ScreenHeight), '-screen-fullscreen=0', ('-rimlayersuiscale='+$UiScale.ToString([Globalization.CultureInfo]::InvariantCulture)),
        ('-rimlayersraidseed='+$RaidSeed),('-rimlayersmapsize='+$MapSize),('-rimlayerspawns='+$PawnCount),('-rimlayerslayers='+$Layers)
    )
    if($mode -eq 'completed-load'){$arguments+='-rimlayersshaftcompletedreload'}
    if($mode -eq 'flight-load'){$arguments+='-rimlayersmortarflightreload'}
    if($mode -eq 'landed-load'){$arguments+='-rimlayersshiplandedreload'}
    if($NativeShip){$arguments+='-rimlayersshipnative'}
    if($LegacyFlight){$arguments+='-rimlayerslegacyflight'}
    $arguments+=('-rimlayersshipdecks='+$ShipDecks)
    if($ShipVisual){$arguments+='-rimlayersshipvisual'}
    if($BootstrapLift){$arguments+='-rimlayersbootlift'}
    if($LiftWork){$arguments+='-rimlayersliftwork'}
    if($Shelf){$arguments+='-rimlayersshelf'}
    if($TravelMultipliers){$arguments+='-rimlayerstravelmultipliers'}
    $arguments+=('-rimlayersenginelayer='+$EngineLayer)
    if($Scenario -eq "cargo"){$arguments+=("-rimlayerscargocheckpoint="+$CargoCheckpoint)}
    if($TemperatureCore){$arguments+='-rimlayerstemperaturecore'}
    if($CorpseDamage){$arguments+='-rimlayerscorpsedamage'}
    if($River){$arguments+='-rimlayersriver'}
    if($Space){$arguments+='-rimlayersspace'}
    if($ExistingMap){$arguments+='-rimlayersexistingmap'}
    if($mode -eq 'converted-load'){$arguments+='-rimlayersmigrationreload'}
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
    $resultPath = Join-Path $runRoot ($mode + '-' + $Scenario + '-results.json')
    if (-not (Test-Path -LiteralPath $resultPath)) { throw ("No runtime results: " + $mode + "; inspect " + $runRoot) }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Write-Output ($mode + ': ' + $result.checks.Count + ' assertions, ' + $result.failures + ' failures')
    if($Scenario -eq 'migration' -and $mode -eq 'generate') {
        $save=Join-Path $runRoot 'Saves/RimLayers_V1Migration.rws'
        $baseline=Get-Content -LiteralPath (Join-Path $runRoot 'migration-baseline.json') -Raw|ConvertFrom-Json
        [xml]$doc=Get-Content -LiteralPath $save -Raw
        $old=$doc.SelectSingleNode("/savegame/game/maps/li[uniqueID='"+$baseline.map+"']/components")
        $component=$old.SelectSingleNode("li[@Class='RimLayers.Core.LayerMapComponent']")
        if($null -ne $component){$old.RemoveChild($component)|Out-Null}
        $doc.Save($save)
    }
    if($LegacyFlight -and $Scenario -eq 'gravship' -and $mode -eq 'generate' -and $result.failures -eq 0) {
        # Only this runner's newly created isolated save: model a pre-quota in-flight save.
        $save=Join-Path $runRoot 'Saves/RimLayers_V1Gravship.rws'
        [xml]$legacy=Get-Content -LiteralPath $save -Raw
        $reservation=$legacy.SelectSingleNode('//rimLayersExpansionReservations')
        if($null -eq $reservation){throw 'No saved reservation node for legacy migration fixture'}
        $reservation.ParentNode.RemoveChild($reservation)|Out-Null
        $legacy.Save($save)
    }
    if ($result.failures -ne 0) {
        $result.checks | Where-Object { -not $_.pass } | ConvertTo-Json -Depth 5 | Write-Output
        throw ("Runtime acceptance failed: " + $mode)
    }
}
Write-Output (('V1 ' + $Scenario + ' acceptance passed: ') + $runRoot)
