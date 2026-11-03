$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
$control="$root\source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll"
function Send($items){$f="$root\temp\block-live-command.json";[IO.File]::WriteAllText($f,(ConvertTo-Json -InputObject @($items) -Depth 12 -Compress)); & dotnet $control $f | Out-Null;if($LASTEXITCODE){throw 'Bridge command failed'}}
function HostOp($op,$extra=@{}){$m=@{t='director';host='cs2';op=$op};foreach($k in $extra.Keys){$m[$k]=$extra[$k]};return $m}
function Inspect {Send @((HostOp 'inspect'));return (Get-Content "$root\logs\cs2-live-state.json" -Raw|ConvertFrom-Json)}
function Ids($s){return @($s.blocks|ForEach-Object {$_ -join ','})}
$baseline=Inspect
$results=@()
# Caller aims at a valid surface and selects grass blocks before this test.
# Keep background input from being cancelled by the normal focus watchdog.
try {
 Send @((HostOp 'test_mode' @{on=$true}),@{wait_ms=500},(HostOp 'pose' @{p=($baseline.players|Where-Object {-not $_.bot}|Select-Object -First 1).pos;r=($baseline.players|Where-Object {-not $_.bot}|Select-Object -First 1).angles}),@{t='resetinput';close=$true},@{wait_ms=500})
 for($i=1;$i -le 3;$i++){
  Send @(@{t='key';k='use';down=$true},@{wait_ms=100},@{t='key';k='use';down=$false},@{wait_ms=650})
  $placed=Inspect
  $added=@((Ids $placed)|Where-Object {$_ -notin (Ids $baseline)})
  if($added.Count -ne 1){throw 'Placement failed: stop before attack can remove a pre-existing block.'}
  if($i -eq 1){Send @((HostOp 'capture'))}
  Send @(@{t='key';k='attack';down=$true},@{wait_ms=100},@{t='key';k='attack';down=$false},@{wait_ms=650})
  $broken=Inspect
  $results+= [ordered]@{cycle=$i;before=$baseline.validBlocks;afterPlace=$placed.validBlocks;afterBreak=$broken.validBlocks;added=$added;pass=($added.Count -eq 1 -and $broken.validBlocks -eq $baseline.validBlocks -and @(Compare-Object (Ids $baseline) (Ids $broken)).Count -eq 0)}
 }
 [ordered]@{time=(Get-Date).ToString('o');method='Real Minecraft use/attack input, grass_block slot 8';cycles=$results;pass=(@($results|Where-Object {-not $_.pass}).Count -eq 0)}|ConvertTo-Json -Depth 9|Tee-Object -FilePath "$root\logs\block-placement-v023.json"
} finally {
 Send @(@{t='key';k='use';down=$false},@{t='key';k='attack';down=$false})
 $current=Inspect
 foreach($added in @((Ids $current)|Where-Object {$_ -notin (Ids $baseline)})) {Send @(@{t='cmd';c=('setblock '+($added -replace ',',' ')+' minecraft:air')})}
 Send @((HostOp 'test_mode' @{on=$false}))
}
