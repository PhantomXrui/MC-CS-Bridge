$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
$control=Join-Path $root 'source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll'
function Send-Bridge($commands){
 $path=Join-Path $root 'temp\interaction-command.json'
 [IO.File]::WriteAllText($path,(ConvertTo-Json -InputObject @($commands) -Depth 15 -Compress),[Text.UTF8Encoding]::new($false))
 & dotnet $control $path | Out-Null
 if($LASTEXITCODE){throw 'Control command failed'}
}
function Host-Command($op,$extra=@{}) { $m=@{t='director';host='cs2';op=$op};foreach($key in $extra.Keys){$m[$key]=$extra[$key]};return $m }
function Inspect-State($name){
 Send-Bridge @((Host-Command 'inspect'))
 $path=Join-Path $root 'logs\cs2-live-state.json';$state=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
 Copy-Item -LiteralPath $path -Destination (Join-Path $root ('logs\test-'+$name+'.json')) -Force
 return $state
}
Send-Bridge @(@{t='key';k='escape';down=$true},(Host-Command 'test_mode' @{on=$true}),@{wait_ms=1200})
$start=Inspect-State 'initial'
$human=$start.players|Where-Object {-not $_.bot}
if(-not $human.alive){throw 'Local test player is not alive'}
try{
 $pose=Host-Command 'pose' @{p=@(120,2300,-119);r=@(0,0)}
 $velocity=Host-Command 'walk' @{on=$true}
 $halt=Host-Command 'walk' @{on=$false}
 Send-Bridge @($pose,@{wait_ms=200},$velocity,@{wait_ms=1200},$halt)
 $wall=Inspect-State 'wall'
 Send-Bridge @(@{t='cmd';c='fill 5 92 -58 6 94 -58 minecraft:air'},@{wait_ms=600},$pose,@{wait_ms=200},$velocity,@{wait_ms=1200},$halt)
 $open=Inspect-State 'opening'
 Send-Bridge @(@{t='cmd';c='fill 5 92 -58 6 94 -58 minecraft:gold_block'},@{wait_ms=600},$pose,@{wait_ms=500},@{t='slot';n=7},@{t='key';k='use';down=$true},@{wait_ms=100},@{t='key';k='use';down=$false},@{wait_ms=300})
 $place=Inspect-State 'place'
 Send-Bridge @(@{t='key';k='attack';down=$true},@{wait_ms=100},@{t='key';k='attack';down=$false},@{wait_ms=300})
 $broken=Inspect-State 'break'
 $wallHuman=$wall.players|Where-Object {-not $_.bot};$openHuman=$open.players|Where-Object {-not $_.bot}
 $report=[ordered]@{Time=(Get-Date).ToString('o');WallX=$wallHuman.pos[0];OpeningX=$openHuman.pos[0];CollisionPass=($wallHuman.alive -and $openHuman.alive -and $wallHuman.pos[0] -lt 200 -and $openHuman.pos[0] -gt 280);InitialBlocks=@($start.blocks).Count;PlacedBlocks=@($place.blocks).Count;BrokenBlocks=@($broken.blocks).Count;PlaceBreakPass=(@($place.blocks).Count -gt @($start.blocks).Count -and @($broken.blocks).Count -lt @($place.blocks).Count)}
 $report|ConvertTo-Json|Tee-Object -FilePath (Join-Path $root 'logs\interaction-test-result.json')
}finally{
 Send-Bridge @(@{t='cmd';c='fill 5 92 -58 6 94 -58 minecraft:gold_block'},(Host-Command 'test_mode' @{on=$false}),(Host-Command 'pose' @{p=$human.pos;r=$human.angles}))
}
