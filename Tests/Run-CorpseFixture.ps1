param([string]$SourceSave,[int]$TimeoutSeconds=600,[switch]$CorpseDamage)
$ErrorActionPreference='Stop'
$gameRoot='G:/SteamLibrary/steamapps/common/RimWorld'
$profile='C:/Users/zonre/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios'
$modRoot=Split-Path -Parent $PSScriptRoot
if(!$SourceSave){$SourceSave=(Get-ChildItem -LiteralPath (Join-Path $profile 'Saves') -File -Filter '*.rws'|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1).FullName}
$runRoot=Join-Path $PSScriptRoot ('Artifacts/corpse-fixture-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path (Join-Path $runRoot 'Saves'),(Join-Path $runRoot 'Config')|Out-Null
Copy-Item -LiteralPath $SourceSave -Destination (Join-Path $runRoot 'Saves/RimLayers_CorpseFixture.rws')
[xml]$cfg=Get-Content -LiteralPath (Join-Path $profile 'Config/ModsConfig.xml') -Raw
# Keep this offline diagnostic out of the player's online presence and chat services.
foreach($node in @($cfg.SelectNodes("/ModsConfigData/activeMods/li"))){
 if([string]$node.InnerText -in @('thomotron.phinix','natsuki.phinixredpacket','rwzh.chinesepack.phinix')){[void]$cfg.ModsConfigData.activeMods.RemoveChild($node)}
}
$cfg.Save((Join-Path $runRoot 'Config/ModsConfig.xml'))
[IO.File]::WriteAllText((Join-Path $runRoot 'Config/Prefs.xml'),'<PrefsData><devMode>False</devMode><runInBackground>True</runInBackground><edgeScreenScroll>False</edgeScreenScroll><langFolderName>English</langFolderName></PrefsData>',[Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath (Join-Path $profile 'Config') -File -Filter '*3797541313*'|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $runRoot ('Config/'+$_.Name))}
@{sourceSave=$SourceSave;sourceLength=(Get-Item -LiteralPath $SourceSave).Length;sourceModifiedUtc=(Get-Item -LiteralPath $SourceSave).LastWriteTimeUtc.ToString('o');offlineExcluded=@('thomotron.phinix','natsuki.phinixredpacket','rwzh.chinesepack.phinix')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $runRoot 'fixture-input.json') -Encoding UTF8
& dotnet build (Join-Path $modRoot 'Source/RimLayers/RimLayers.csproj') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$arguments=@(('-savedatafolder='+$runRoot),'-rimlayersv1test','-rimlayerscase=corpse-refresh','-rimlayerscorpsefixture','-rimlayersmode=generate','-rimlayersalldlc','-screen-width=1024','-screen-height=768','-screen-fullscreen=0')
if($CorpseDamage){$arguments+=' -rimlayerscorpsedamage'.Trim()}
$p=Start-Process -FilePath (Join-Path $gameRoot 'RimWorld.exe') -WorkingDirectory $gameRoot -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
@{processId=$p.Id;runRoot=$runRoot}|ConvertTo-Json -Compress|Write-Output
if(!$p.WaitForExit($TimeoutSeconds*1000)){$p.Kill();throw ('Own fixture timed out: '+$runRoot)}
Write-Output ('Exit code: '+$p.ExitCode)
$result=Join-Path $runRoot 'generate-corpse-refresh-results.json'
if(!(Test-Path -LiteralPath $result)){throw ('No fixture results: '+$runRoot)}
$data=Get-Content -LiteralPath $result -Raw|ConvertFrom-Json
@{failures=$data.failures;errorCount=$data.errors.Count;checks=$data.checks;runRoot=$runRoot}|ConvertTo-Json -Depth 5|Write-Output
