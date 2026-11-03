$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
$control="$root\source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll"
function Send($a){$f="$root\temp\building-catalog-command.json";[IO.File]::WriteAllText($f,(ConvertTo-Json -InputObject @($a) -Depth 12 -Compress));& dotnet $control $f|Out-Null;if($LASTEXITCODE){throw 'Bridge command failed'}}
function HostOp($op,$more=@{}){$r=@{t='director';host='cs2';op=$op};foreach($k in $more.Keys){$r[$k]=$more[$k]};$r}
function MC($c){@{t='cmd';c=$c}}
function State {Send @((HostOp 'inspect'),@{wait_ms=300});Get-Content "$root\logs\cs2-live-state.json" -Raw|ConvertFrom-Json}
function Ids($s){@($s.blocks|ForEach-Object {$_ -join ','})}
$results=@();$testBlocks=@()
$spots=@(@(340,2460,97.269684),@(1214,1105,0.21820068),@(-1280,320,367.5),@(-500,-800,107.64487))
try{
 Send @((HostOp 'test_mode' @{on=$true}),@{t='resetinput';close=$true},@{t='slot';n=7},(MC 'item replace entity @p hotbar.7 with minecraft:grass_block 64'))
 foreach($spot in $spots){
  Send @((HostOp 'pose' @{p=$spot;r=@(30,0)}),@{wait_ms=3000})
  $before=State
  Send @(@{t='key';k='use';down=$true},@{wait_ms=100},@{t='key';k='use';down=$false},@{wait_ms=600})
  $after=State;$added=@((Ids $after)|Where-Object {$_ -notin (Ids $before)})
  $results+=@{case='terrain_grass';position=$spot;added=$added;pass=($added.Count -eq 1)}
  foreach($p in $added){Send @((MC ('setblock '+($p -replace ',',' ')+' minecraft:air')))}
 }
 Send @((HostOp 'pose' @{p=$spots[0];r=@(45,0)}),@{wait_ms=1000})
 foreach($kind in @('stone','oak_planks','glass','oak_slab','oak_stairs','oak_fence','torch','redstone','crafting_table','chest','furnace','water_bucket')){
  $expected=if($kind -eq 'redstone'){'redstone_wire'}elseif($kind -eq 'water_bucket'){'water'}else{$kind}
  Send @(@{t='resetinput';close=$true},(MC "item replace entity @p hotbar.7 with minecraft:$kind"),@{wait_ms=400},@{t='key';k='use';down=$true},@{wait_ms=100},@{t='key';k='use';down=$false},@{wait_ms=600},(MC "execute if block 9 99 -62 minecraft:$expected run say MCB_OK_$kind"))
  $tail=Get-Content "$root\minecraft\logs\latest.log" -Tail 30
  $pass=@($tail|Where-Object {$_ -match ('\[Server\] MCB_OK_'+[regex]::Escape($kind)+'$')}).Count -gt 0
  $results+=@{case='material';item=$kind;expected=$expected;pass=$pass}
  if($kind -eq 'water_bucket'){Send @((MC 'fill 5 96 -66 13 100 -58 minecraft:air replace minecraft:water'))}else{Send @((MC 'setblock 9 99 -62 minecraft:air'))}
  $results|ConvertTo-Json -Depth 8|Set-Content "$root\logs\building-catalog-v024.json" -Encoding utf8
 }
 $results|ConvertTo-Json -Depth 8
}finally{
 Send @(@{t='resetinput';close=$true},(MC 'item replace entity @p hotbar.7 with minecraft:grass_block 64'),(HostOp 'test_mode' @{on=$false}),(HostOp 'building_mode'),(HostOp 'pose' @{p=$spots[0];r=@(45,0)}),@{t='slot';n=7})
}
