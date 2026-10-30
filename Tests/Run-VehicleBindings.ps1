param([string]$CandidatePath=(Join-Path $PSScriptRoot 'Artifacts/vehicle-candidate/RimLayers.dll'),[switch]$UseCandidate,[int]$TimeoutSeconds=180,[switch]$Install,[switch]$NoFramework,[switch]$Upstream,[ValidateSet('None','VVE','Technical')][string]$ContentProfile='None')
$ErrorActionPreference='Stop'
if($PSBoundParameters.ContainsKey('CandidatePath') -and !$UseCandidate){throw 'CandidatePath requires -UseCandidate; omit it to build the default vehicle candidate.'}
$root=Split-Path -Parent $PSScriptRoot
$game=Split-Path -Parent (Split-Path -Parent $root)
if(Get-Process RimWorld -ErrorAction SilentlyContinue){throw 'Close RimWorld before swapping a candidate DLL for this isolated test.'}
$run=Join-Path $PSScriptRoot ('Artifacts/vehicle-bindings-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path (Join-Path $run 'Config') | Out-Null
$live=Join-Path $root 'Assemblies/RimLayers.dll'
$backup=Join-Path $run 'RimLayers.before-test.dll'
$version=(Get-Content -LiteralPath (Join-Path $game 'Version.txt') -Raw).Trim()
if($NoFramework -and $Upstream){throw 'Cannot specify both -NoFramework and -Upstream.'}
if($NoFramework -and $ContentProfile -ne 'None'){throw 'Content profiles require a Vehicle Framework provider.'}
$content=switch($ContentProfile){
 'VVE' {'<li>oskarpotocki.vanillafactionsexpanded.core</li><li>oskarpotocki.vanillavehiclesexpanded</li><li>oskarpotocki.vanillavehiclesexpandedtier3</li>'}
 'Technical' {'<li>rimthunder.core</li><li>mo.technicalvehicles</li>'}
 default {''}
}
$provider=if($NoFramework){''}elseif($Upstream){'<li>smashphil.vehicleframework</li>'}else{'<li>baal.vehicleframework</li>'}
[IO.File]::WriteAllText((Join-Path $run 'Config/ModsConfig.xml'),('<ModsConfigData><version>'+$version+'</version><activeMods><li>brrainz.harmony</li><li>ludeon.rimworld</li>' + $provider + $content + '<li>rimlayers.core</li></activeMods><knownExpansions><li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li></knownExpansions></ModsConfigData>'),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $run 'Config/Prefs.xml'),'<PrefsData><runInBackground>True</runInBackground><langFolderName>English</langFolderName></PrefsData>',[Text.UTF8Encoding]::new($false))
if(!$UseCandidate){
    & dotnet build (Join-Path $root 'Source/RimLayers/RimLayers.csproj') -c Release '-p:OutputPath=../../Tests/Artifacts/vehicle-candidate/' --nologo
    if($LASTEXITCODE -ne 0){throw 'Candidate build failed'}
}
if(!(Test-Path -LiteralPath $CandidatePath -PathType Leaf)){throw "Candidate not found: $CandidatePath"}
$CandidatePath=(Resolve-Path -LiteralPath $CandidatePath).Path
Copy-Item -LiteralPath $live -Destination $backup
$process=$null
try {
    Copy-Item -LiteralPath $CandidatePath -Destination $live
    $report=Join-Path $run 'vehicle-bindings.txt'
    $args=@('-savedatafolder='+$run,'-rimlayersvehiclebindings','-rimlayersvehicleprobeexit','-rimlayersvehiclebindingreport='+$report,'-screen-width=1024','-screen-height=768','-screen-fullscreen=0')
    if($Install){$args+='-rimlayersvehiclesprototype'}
    $process=Start-Process -FilePath (Join-Path $game 'RimWorld.exe') -WorkingDirectory $game -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'stdout.log') -RedirectStandardError (Join-Path $run 'stderr.log')
    if(!$process.WaitForExit($TimeoutSeconds*1000)){$process.Kill();$process.WaitForExit();throw "Binding probe timed out: $run"}
    $process.WaitForExit()
    $gameLog='C:/Users/zonre/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log'
    if(Test-Path -LiteralPath $gameLog){Copy-Item -LiteralPath $gameLog -Destination (Join-Path $run 'Player.log')}
    if(!(Test-Path -LiteralPath $report)){throw "Missing binding report: $run"}
    if($null -ne $process.ExitCode -and $process.ExitCode -ne 0){throw "Game exited with code $($process.ExitCode): $run"}
    $result=Get-Content -LiteralPath $report -Raw
    Write-Output $result
    ${expected}=if($NoFramework){'^No Vehicle Framework provider enabled'}elseif($Install){'^Ground hook installation accepted'}else{'^(Steel Horizon|baal\.vehicleframework|smashphil\.vehicleframework) ground binding signatures accepted'}
    if($result -notmatch $expected){throw "Binding probe failed: $run"}
    Write-Output "Report: $report"
} finally {
    if($null -ne $process -and !$process.HasExited){$process.Kill();$process.WaitForExit()}
    while(Get-Process RimWorld -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 250}
    for($attempt=0;;$attempt++){
        try {Copy-Item -LiteralPath $backup -Destination $live; break}
        catch [System.IO.IOException] {
            if($attempt -ge 19){throw}
            Start-Sleep -Milliseconds 250
        }
    }
}
