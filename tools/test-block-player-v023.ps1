$ErrorActionPreference='Stop'
$root=if ($env:MCCS_ROOT) { $env:MCCS_ROOT } else { Split-Path -Parent $PSScriptRoot }
# This is a live, world-modifying development test. See docs/testing.md.
$env:MCCS_ROOT=$root
foreach ($dir in @('temp','logs')) { [IO.Directory]::CreateDirectory((Join-Path $root $dir)) | Out-Null }
function Send($a){$f="$root\temp\block-player-command.json";[IO.File]::WriteAllText($f,(ConvertTo-Json -InputObject @($a) -Depth 8 -Compress));& dotnet "$root\source\cs2-bridge\tools\BridgeControl\bin\Debug\net8.0\BridgeControl.dll" $f|Out-Null;if($LASTEXITCODE){throw 'Command failed'}}
function HostOp($op,$more=@{}){$r=@{t='director';host='cs2';op=$op};foreach($k in $more.Keys){$r[$k]=$more[$k]};$r}
function Inspect{Send @((HostOp 'inspect'));Get-Content "$root\logs\cs2-live-state.json" -Raw|ConvertFrom-Json}
try{
 Send @((HostOp 'test_mode' @{on=$true}),(HostOp 'pose' @{p=@(340,2460,97.26968);r=@(45,0)}),@{t='slot';n=7},@{wait_ms=600},@{t='key';k='use';down=$true},@{wait_ms=100},@{t='key';k='use';down=$false},@{wait_ms=500})
 $placed=Inspect
 if(-not ($placed.blocks|Where-Object {($_ -join ',') -eq '9,99,-62'})){throw 'Expected grass block not placed'}
 Send @((HostOp 'pose' @{p=@(280,2460,97.26968);r=@(15,0)}),@{wait_ms=600},(HostOp 'capture'))
 $poses=@()
 foreach($phase in @('blocked','open')){
  if($phase -eq 'open'){Send @(@{t='cmd';c='setblock 9 99 -62 minecraft:air'},@{wait_ms=500})}
  $a=@((HostOp 'pose' @{p=@(280,2460,97.26968);r=@(0,0)}),@{wait_ms=300})
  for($i=0;$i -lt 6;$i++){$a+=@((HostOp 'velocity' @{v=@(400,0,0)}),@{wait_ms=150})}
  $a+=@((HostOp 'velocity' @{v=@(0,0,0)}));Send $a
  $s=Inspect;$p=$s.players|Where-Object {-not $_.bot}|Select-Object -First 1
  $poses+=@{phase=$phase;pos=$p.pos;hp=$p.hp;alive=$p.alive;blocks=$s.validBlocks}
 }
 [ordered]@{time=(Get-Date).ToString('o');method='Repeated native CS player velocity toward a real right-click-placed grass block';results=$poses;pass=($poses[0].pos[0] -lt 360 -and $poses[1].pos[0] -gt 420)}|ConvertTo-Json -Depth 8|Tee-Object -FilePath "$root\logs\block-player-collision-v023.json"
}finally{Send @(@{t='key';k='use';down=$false},@{t='cmd';c='setblock 9 99 -62 minecraft:air'},(HostOp 'velocity' @{v=@(0,0,0)}),(HostOp 'pose' @{p=@(280,2460,97.26968);r=@(15,0)}),(HostOp 'test_mode' @{on=$false}))}
