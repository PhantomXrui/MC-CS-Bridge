$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
$control=Join-Path $root 'source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll'
function Send-Local($items){$f=Join-Path $root 'temp\damage-command.json';[IO.File]::WriteAllText($f,(ConvertTo-Json -InputObject @($items) -Depth 12 -Compress),[Text.UTF8Encoding]::new($false));& dotnet $control $f|Out-Null;if($LASTEXITCODE){throw 'Control failed'}}
function Host-Message($op,$p=@{}){$m=@{t='director';host='cs2';op=$op};foreach($k in $p.Keys){$m[$k]=$p[$k]};return $m}
Send-Local @((Host-Message 'freeze_bots' @{on=$true}),@{wait_ms=500},(Host-Message 'test_bot' @{p=@(1200,2300,5)}),@{wait_ms=700},(Host-Message 'inspect'))
$before=Get-Content (Join-Path $root 'logs\cs2-live-state.json') -Raw|ConvertFrom-Json
$bot=$before.players|Where-Object bot|Select-Object -First 1
$culture=[Globalization.CultureInfo]::InvariantCulture
$x=($bot.pos[0]/40).ToString($culture);$y=($bot.pos[2]/40+96+0.9).ToString($culture);$z=(-$bot.pos[1]/40).ToString($culture)
try{
 Send-Local @(@{t='cmd';c="summon minecraft:tnt $x $y $z {fuse:20s}"},@{wait_ms=2600},(Host-Message 'inspect'),(Host-Message 'capture'))
 $after=Get-Content (Join-Path $root 'logs\cs2-live-state.json') -Raw|ConvertFrom-Json
 $victim=$after.players|Where-Object id -eq $bot.id
 [ordered]@{Time=(Get-Date).ToString('o');BotId=$bot.id;BeforeHP=$bot.hp;AfterHP=$victim.hp;Alive=$victim.alive;TntDamagePass=($victim.hp -lt $bot.hp -or -not $victim.alive)}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $root 'logs\damage-test-result.json')
}finally{Send-Local @((Host-Message 'freeze_bots' @{on=$false}))}
