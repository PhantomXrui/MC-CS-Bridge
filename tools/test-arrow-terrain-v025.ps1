$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
$control="$root\source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll"
function Send($a){[IO.File]::WriteAllText("$root\temp\arrow-terrain-command.json",(ConvertTo-Json -InputObject @($a) -Depth 12 -Compress));& dotnet $control "$root\temp\arrow-terrain-command.json"|Out-Null;if($LASTEXITCODE){throw 'Bridge command failed'}}
function MC($c){@{t='cmd';c=$c}}
$results=@();$ownedBlock=$false
function ArrowCase($tag,$pos,$motion){
 Send @((MC "summon minecraft:arrow $pos {Tags:[`"$tag`"],Motion:[$motion],NoGravity:1b,pickup:0b}"),@{wait_ms=200},(MC "data get entity @e[type=minecraft:arrow,tag=$tag,limit=1] Pos"),(MC "data get entity @e[type=minecraft:arrow,tag=$tag,limit=1] inGround"),@{wait_ms=150})
 $lines=Get-Content "$root\minecraft\logs\latest.log" -Tail 20
 $p=$lines|Where-Object {$_ -match 'following entity data: \['}|Select-Object -Last 1
 if($p -notmatch 'following entity data: \[([^\]]+)\]'){throw "No position result for $tag"}
 $xyz=@($Matches[1] -split ','|ForEach-Object {[double](($_ -replace '[dDfF]$','').Trim())})
 $g=$lines|Where-Object {$_ -match 'following entity data: [01]b'}|Select-Object -Last 1
 if($g -notmatch 'following entity data: ([01])b'){throw "No ground result for $tag"}
 $result=@{case=$tag;pos=$xyz;inGround=($Matches[1] -eq '1')}
 Send @((MC "kill @e[type=minecraft:arrow,tag=$tag]"));return $result
}
try{
 Send @(@{t='resetinput';close=$true},@{t='director';host='cs2';op='pose';p=@(340,2460,97.269684);r=@(45,0)},@{wait_ms=3000},(MC 'execute if block 12 102 -62 minecraft:air run say MCB_V025_FREE'),(MC 'execute if block 9 98 -62 minecraft:barrier run say MCB_V025_HOST_BARRIER'),@{wait_ms=200})
 $tail=Get-Content "$root\minecraft\logs\latest.log" -Tail 30
 if(-not($tail -match '\[Server\] MCB_V025_FREE$')){throw 'MC test cell occupied; preserved user block'}
 if(-not($tail -match '\[Server\] MCB_V025_HOST_BARRIER$')){throw 'Ground not streamed; cannot test coarse overlap'}
 $r=ArrowCase 'mccs_v025_graze' '9.1 98.8 -61.5' '1.0d,0.0d,0.0d';$r.pass=($r.pos[0] -gt 11.5 -and -not $r.inGround);$results+=$r
 $r=ArrowCase 'mccs_v025_floor' '9.5 100 -61.5' '0.0d,-2.0d,0.0d';$r.expectedSurfaceY=98.38786;$r.pass=($r.inGround -and [Math]::Abs($r.pos[1]-98.43786) -lt .015);$results+=$r
 Send @((MC 'setblock 12 102 -62 minecraft:glass'));$ownedBlock=$true
 $r=ArrowCase 'mccs_v025_glass' '10.5 102.5 -61.5' '2.5d,0.0d,0.0d';$r.pass=($r.inGround -and [Math]::Abs($r.pos[0]-11.95) -lt .015);$results+=$r
 Send @((MC 'setblock 12 102 -62 minecraft:oak_slab[type=bottom]'))
 $r=ArrowCase 'mccs_v025_slab_gap' '10.5 102.75 -61.5' '2.5d,0.0d,0.0d';$r.pass=($r.pos[0] -gt 13 -and -not $r.inGround);$results+=$r
 $r=ArrowCase 'mccs_v025_slab_solid' '10.5 102.25 -61.5' '2.5d,0.0d,0.0d';$r.pass=($r.inGround -and [Math]::Abs($r.pos[0]-11.95) -lt .015);$results+=$r
 $results|ConvertTo-Json -Depth 6|Tee-Object -FilePath "$root\logs\arrow-terrain-v025.json"
}finally{
 if($ownedBlock){Send @((MC 'setblock 12 102 -62 minecraft:air'))}
 foreach($tag in @('graze','floor','glass','slab_gap','slab_solid')){Send @((MC "kill @e[type=minecraft:arrow,tag=mccs_v025_$tag]"))}
}
