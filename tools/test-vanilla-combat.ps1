param([string]$Only='', [switch]$Extended, [switch]$EdgeCases)
$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
$control=Join-Path $root 'source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll'
function Send-Local($items){
 $f=Join-Path $root 'temp\vanilla-combat-command.json'
 [IO.File]::WriteAllText($f,(ConvertTo-Json -InputObject @($items) -Depth 15 -Compress),[Text.UTF8Encoding]::new($false))
 & dotnet $control $f | Out-Null
 if($LASTEXITCODE){throw 'Local director failed'}
}
function Host-Op($op,$extra=@{}){$m=@{t='director';host='cs2';op=$op};foreach($k in $extra.Keys){$m[$k]=$extra[$k]};return $m}
function MC($c){return @{t='cmd';c=$c}}
function Key($key,$down){return @{t='key';k=$key;down=$down}}
$target='@e[type=minecraft:villager,tag=gta_proxy,x=14.5,y=98.43838,z=-61.5,distance=..2,limit=1]'
$cases=@(
 @{name='bow_full_draw'; commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:bow'),(MC 'item replace entity @p weapon.offhand with minecraft:arrow 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1300},(Key 'use' $false));wait=1500},
 @{name='crossbow';commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:crossbow'),(MC 'item replace entity @p weapon.offhand with minecraft:arrow 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1700},(Key 'use' $false),@{wait_ms=400},(Key 'use' $true),@{wait_ms=100},(Key 'use' $false));wait=1500},
 @{name='trident';commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:trident'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1300},(Key 'use' $false));wait=1500},
 @{name='splash_harming';commands=@((MC 'summon minecraft:splash_potion 14.5 99.3 -61.5 {Item:{id:"minecraft:splash_potion",count:1,components:{"minecraft:potion_contents":{potion:"minecraft:harming"}}}}'));wait=1700},
 @{name='poison_effect';commands=@((MC "effect give $target minecraft:poison 5 0 true"));wait=3500},
 @{name='wither_effect';commands=@((MC "effect give $target minecraft:wither 5 0 true"));wait=3500},
 @{name='fire';commands=@((MC "data merge entity $target {Fire:80s}"));wait=3000},
 @{name='tnt';commands=@((MC 'summon minecraft:tnt 14.5 99.3 -61.5 {fuse:10s}'));wait=1800}
)
if($Extended){$cases=@(
 @{name='diamond_sword';commands=@((Host-Op 'pose' @{p=@(500,2460,97.53518);r=@(0,0)}),(MC 'item replace entity @p weapon.mainhand with minecraft:diamond_sword'),@{wait_ms=900},(Key 'attack' $true),@{wait_ms=100},(Key 'attack' $false));wait=1000},
 @{name='diamond_axe';commands=@((Host-Op 'pose' @{p=@(500,2460,97.53518);r=@(0,0)}),(MC 'item replace entity @p weapon.mainhand with minecraft:diamond_axe'),@{wait_ms=1200},(Key 'attack' $true),@{wait_ms=100},(Key 'attack' $false));wait=1000},
 @{name='mace';commands=@((Host-Op 'pose' @{p=@(500,2460,97.53518);r=@(0,0)}),(MC 'item replace entity @p weapon.mainhand with minecraft:mace'),@{wait_ms=1600},(Key 'attack' $true),@{wait_ms=100},(Key 'attack' $false));wait=1000},
 @{name='flame_bow';commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:bow[minecraft:enchantments={"minecraft:flame":1}]'),(MC 'item replace entity @p weapon.offhand with minecraft:arrow 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1300},(Key 'use' $false));wait=3000},
 @{name='poison_arrow';commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:bow'),(MC 'item replace entity @p weapon.offhand with minecraft:tipped_arrow[minecraft:potion_contents={potion:"minecraft:poison"}] 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1300},(Key 'use' $false));wait=3000},
 @{name='lingering_harming';commands=@((MC 'summon minecraft:lingering_potion 14.5 99.3 -61.5 {Item:{id:"minecraft:lingering_potion",count:1,components:{"minecraft:potion_contents":{potion:"minecraft:harming"}}}}'));wait=3000},
 @{name='crossbow_firework';commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:crossbow'),(MC 'item replace entity @p weapon.offhand with minecraft:firework_rocket[minecraft:fireworks={flight_duration:1,explosions:[{shape:"small_ball",colors:[16711680]}]}] 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1700},(Key 'use' $false),@{wait_ms=400},(Key 'use' $true),@{wait_ms=100},(Key 'use' $false));wait=1800}
)}
if($EdgeCases){$cases=@(
 @{name='campfire';commands=@((MC 'setblock 14 98 -62 minecraft:campfire[lit=true]'));wait=2500;cleanup=@((MC 'setblock 14 98 -62 minecraft:barrier'))},
 @{name='magma_block';commands=@((MC 'setblock 14 98 -62 minecraft:magma_block'),(Host-Op 'test_bot' @{p=@(580,2460,120.1)}));wait=2500;cleanup=@((MC 'setblock 14 98 -62 minecraft:barrier'))},
 @{name='wall_blocks_arrow';expectDamage=$false;commands=@((MC 'fill 11 99 -62 11 101 -62 minecraft:obsidian'),(MC 'item replace entity @p weapon.mainhand with minecraft:bow'),(MC 'item replace entity @p weapon.offhand with minecraft:arrow 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1300},(Key 'use' $false));wait=1500;cleanup=@((MC 'fill 11 99 -62 11 101 -62 minecraft:air'))},
 @{name='power_bow';commands=@((MC 'item replace entity @p weapon.mainhand with minecraft:bow[minecraft:enchantments={"minecraft:power":3}]'),(MC 'item replace entity @p weapon.offhand with minecraft:arrow 64'),@{wait_ms=400},(Key 'use' $true),@{wait_ms=1300},(Key 'use' $false));wait=1500},
 @{name='lava';commands=@((MC 'setblock 14 98 -62 minecraft:lava'));wait=2500;cleanup=@((MC 'fill 12 97 -64 16 100 -60 minecraft:air replace minecraft:lava'))},
 @{name='falling_anvil';commands=@((MC 'setblock 14 98 -62 minecraft:barrier'),(MC 'summon minecraft:falling_block 14.5 103 -61.5 {BlockState:"minecraft:anvil",Time:1,HurtEntities:1b,FallHurtAmount:2.0f,FallHurtMax:40,DropItem:0b}'));wait=2500;cleanup=@((MC 'fill 14 98 -62 14 100 -62 minecraft:air replace minecraft:anvil'))},
 @{name='respawn_clears_effects';expectDamage=$false;commands=@((MC "effect give $target minecraft:wither 20 1 true"),@{wait_ms=300},(Host-Op 'test_bot' @{p=@(580,2460,97.53518)}));wait=3000}
)}
$results=@()
foreach($case in $cases){
 if($Only -and $case.name -ne $Only){continue}
 Send-Local @((Key 'use' $false),(Key 'attack' $false),(MC 'kill @e[type=minecraft:area_effect_cloud,x=14.5,y=99,z=-61.5,distance=..8]'),(Host-Op 'test_bot' @{p=@(580,2460,97.53518)}),@{wait_ms=800},(MC "effect clear $target"),(MC "effect give $target minecraft:invisibility infinite 0 true"),(MC "data merge entity $target {Fire:0s}"),(Host-Op 'pose' @{p=@(340,2460,97.26968);r=@(0,0)}),(Host-Op 'inspect'))
 $state=Get-Content (Join-Path $root 'logs\cs2-live-state.json') -Raw|ConvertFrom-Json
 $bot=$state.players|Where-Object {$_.bot -and $_.pos[0] -eq 580}|Select-Object -First 1
 if(-not $bot){throw 'No stationary target bot'}
 $started=Get-Date
 Send-Local @(@($case.commands)+@(@{wait_ms=$case.wait},(Host-Op 'inspect')))
 $after=Get-Content (Join-Path $root 'logs\cs2-live-state.json') -Raw|ConvertFrom-Json
 $victim=$after.players|Where-Object id -eq $bot.id
 $events=@();$eventFile=Join-Path $root 'logs\combat-events.jsonl'
 if(Test-Path $eventFile){$events=@(Get-Content $eventFile|ForEach-Object{$_|ConvertFrom-Json}|Where-Object{[datetime]$_.time -ge $started -and [datetime]$_.time -le [datetime]$after.time -and $_.id -eq $bot.id})}
 $pass=if($case.ContainsKey('expectDamage') -and -not $case.expectDamage){$victim.hp -eq $bot.hp}else{$victim.hp -lt $bot.hp}
 $result=[ordered]@{case=$case.name;time=$started.ToString('o');before=$bot.hp;after=$victim.hp;alive=$victim.alive;pass=$pass;events=$events}
 $results+=$result
 $result|ConvertTo-Json -Depth 8 -Compress|Write-Output
 $results|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $root ('logs\combat-v023-'+$(if($Only){$Only}elseif($Extended){'extended'}elseif($EdgeCases){'edges'}else{'suite'})+'.json')) -Encoding utf8
 if($case.cleanup){Send-Local $case.cleanup}
}
