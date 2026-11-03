using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Drawing;
using V3=System.Numerics.Vector3;
using System.Threading.Channels;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin : BasePlugin
{
    public override string ModuleName => "Minecraft Local Passthrough Bridge";
    public override string ModuleVersion => "0.4.3";
    public override string ModuleAuthor => "Codex; MC exporter by universal-modder";
    static readonly string Root = ResolveRoot();
    static string ResolveRoot() {
        var root = Environment.GetEnvironmentVariable("MCCS_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Set MCCS_ROOT to an absolute bridge workspace path before launching CS2.");
        return Path.GetFullPath(root);
    }
    const float Units = 40f;
    // Vertical mapping between CS Z and MC Y. Per-map: start-map.ps1 exports MC_CS_YOFFSET so the
    // main floor lands on an integer block boundary (blocks then rest flush instead of hovering).
    static readonly float YOffset = ParseYOffset();
    static float ParseYOffset() => float.TryParse(Environment.GetEnvironmentVariable("MC_CS_YOFFSET"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0f ? value : 96f;
    MemoryMappedFile? map;
    MemoryMappedViewAccessor? view;
    readonly CancellationTokenSource stop = new();
    readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    readonly ConcurrentQueue<string> incoming = new();
    readonly Channel<(string path,string text,bool append)> diagnostics = Channel.CreateBounded<(string,string,bool)>(new BoundedChannelOptions(256){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    readonly HashSet<(int,int,int)> ground = new();
    readonly Dictionary<(int,int,int),List<CPhysicsPropOverride>> blocks = new();
    const string CubeModel="models/dev/dev_cube.vmdl";
    long frame, previousKeys;
    float[] watermarkOrigin=new[]{0f,0f,0f};
    float watermarkYaw,watermarkPitch;
    int previousSlot = -1, lastWidth, lastHeight;
    volatile bool connected;
    bool lastBuild, mcReady, needsResync=true, lastInputActive, linkWasHealthy;
    int connectionGeneration, observedGeneration;
    long lastMCPulse;
    bool wasAlive;
    bool renderEnabled=true;
    string? previousMap;
    DateTime nextLog;
    readonly Dictionary<uint,uint> hiddenViewModels=new();
    volatile WorldGeometry? geometry;
    volatile (int x,int y,int z)[]? surfaceCells;
    volatile SurfaceIndex? surfaceIndex;
    IEnumerator<(int x,int y,int z)>? groundWork;
    long nextGroundScan;
    readonly double[] tickTimes=new double[640];
    int tickSamples;
    readonly Dictionary<int,V3> projectilePositions=new();
    readonly Dictionary<int,V3> mobs=new();
    readonly Dictionary<int,string> mobKinds=new();
    readonly Dictionary<int,V3> mobAim=new();
    bool IsBarrageBoss(int id)=>mobKinds.GetValueOrDefault(id) is "wither" or "ender_dragon";
    V3 MobAim(int id,V3 at)=>mobAim.GetValueOrDefault(id,at+new V3(0,.9f,0));
    const double TerroristWitherBulletDamage=0.25;
    double MobBulletDamage(int id,CCSPlayerController? attacker,double normalDamage)=>
        attacker is {IsBot:true,TeamNum:2} && IsBarrageBoss(id) ? TerroristWitherBulletDamage : normalDamage;
    string? geometryMap;
    uint pointerSerial;
    bool testing;
    readonly Dictionary<uint,(int id,string name)> mobProxies=new();
    readonly HashSet<uint> nativeMobBots=new();
    HashSet<uint>? mobSpawnExisting;
    long nextMobBotAttempt;
    CPhysicsPropOverride? probe;
    long lastActorHit;
    readonly Dictionary<long,double> damageRemainders=new();
    readonly Dictionary<uint,(uint pawn,long life)> actorLives=new();
    long nextActorLife=Environment.TickCount64<<16;
    long ActorLife(CCSPlayerController player) {
        if(!actorLives.TryGetValue(player.Index,out var entry)||entry.pawn!=player.PlayerPawn.Raw)
            actorLives[player.Index]=entry=(player.PlayerPawn.Raw,++nextActorLife);
        return entry.life;
    }

    public override void Load(bool hotReload)
    {
        // Only the explicitly launched insecure local client may load this bridge.
        if (!Environment.CommandLine.Split(' ').Any(x => x.Trim('"').Equals("-insecure", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("MinecraftBridge requires the official -insecure local-test option.");
        map = MemoryMappedFile.CreateOrOpen(@"Local\MC_CS_Bridge_Pose", 4096);
        view = map.CreateViewAccessor();
        view.Write(0, 0x504D5343); view.Write(4, 1); view.Write(28, 1);
        view.Write(24,0);view.Write(96,0L);view.Write(136,0);view.Write(184,view.ReadInt64(176));
        view.Write(200,1); // Bounded render-time pose prediction; local director can compare it disabled.
        view.Write(232,0);view.Write(236,0); // Requested/applied first, rear, front camera.
        RegisterListener<Listeners.OnTick>(Tick);
        EnsureC4TestObservers();
        LoadDanceNative();
        LoadUniqueTAgents();
        LoadCombatFeedback();
        RegisterEventHandler<EventPlayerSpawn>((ev,info)=>{if(ev.Userid!=null){if(actorLives.Remove(ev.Userid.Index,out var old))damageRemainders.Remove(old.life);ActorLife(ev.Userid);HandleInteractionSpawn(ev.Userid);}return HookResult.Continue;});
        RegisterListener<Listeners.OnMapEnd>(()=>{ClearInteractionSandbox();ReleaseBlockResync("map_end");FinishNativeC4Test(false);SuspendInput(true);ClearHostEntities();previousMap=null;geometry=null;surfaceCells=null;geometryMap=null;ground.Clear();});
        RegisterListener<Listeners.OnMapStart>(name=>{previousMap=null;needsResync=true;AddTimer(3f,ApplyBuildMode);});
        if(!hotReload)AddTimer(3f,ApplyBuildMode);
        RegisterEventHandler<EventRoundStart>((ev,info)=>{
            AddTimer(.4f,()=>{needsResync=true;SuspendInput(true);Log("round start: rebuilding Minecraft collision");});
            AddTimer(1f,()=>EnsureBreedingSpawnCapacity());
            return HookResult.Continue;
        });
        RegisterEventHandler<EventBulletImpact>((ev,info)=>{
            var pawn=ev.Userid?.PlayerPawn.Value;if(pawn?.AbsOrigin==null || !connected)return HookResult.Continue;
            var start=WorldGeometry.ToMC(new V3(pawn.AbsOrigin.X,pawn.AbsOrigin.Y,pawn.AbsOrigin.Z+pawn.ViewOffset.Z));
            var end=WorldGeometry.ToMC(new V3(ev.X,ev.Y,ev.Z));
            foreach(var mob in mobs){if(mobProxies.Values.Any(p=>p.id==mob.Key))continue;var center=MobAim(mob.Key,mob.Value);if(SegmentDistance(start,end,center)<.7f && !Occluded(start,center)){Send(new {t="mobdmg",id=mob.Key,d=MobBulletDamage(mob.Key,ev.Userid,6.0)});Log("CS bullet -> MC mob "+mob.Key);break;}}
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerHurt>((ev,info)=>{
            if(ev.Userid!=null && mobProxies.TryGetValue(ev.Userid.Index,out var proxy)){
                Send(new {t="mobdmg",id=proxy.id,d=MobBulletDamage(proxy.id,ev.Attacker,ev.DmgHealth/5.0)});
                var pawn=ev.Userid.PlayerPawn.Value;if(pawn!=null){pawn.Health=1000;Utilities.SetStateChanged(pawn,"CBaseEntity","m_iHealth");}
                Log($"native mob target {proxy.id} hit by CS actor {ev.Attacker?.Index} damage={ev.DmgHealth}");
            }
            // CT 打到小 T：只吃 30% 伤害（回补 70%），大 T 不受影响。
            if(ev.Userid!=null && ev.Attacker!=null && ev.Attacker.TeamNum==3 && IsJuvenile(ev.Userid)){
                var kid=ev.Userid.PlayerPawn.Value;
                if(kid!=null){int restore=(int)(ev.DmgHealth*0.7);if(restore>0){kid.Health=Math.Min(100,kid.Health+restore);Utilities.SetStateChanged(kid,"CBaseEntity","m_iHealth");}}
            }
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest=>manifest.AddResource(CubeModel));
        RegisterListener<Listeners.OnClientPutInServer>(slot => AddTimer(3.0f, () => {
            var p=Utilities.GetPlayerFromSlot(slot);
            if(p!=null && p.IsValid && !p.IsBot && Utilities.GetPlayers().Count(x=>x.IsValid && !x.IsBot)==1) {
                p.ChangeTeam(CsTeam.CounterTerrorist);
                AddTimer(1.0f,()=>{if(p.IsValid){p.Respawn();Log("local test player joined CT");}});
            }
        }));
        AddCommand("css_mc_status", "Report local Minecraft bridge state", (player, info) => info.ReplyToCommand($"MC bridge: pipe={connected}; tick={frame}; map={Server.MapName}; ground={ground.Count}"));
        AddCommand("css_mccs_fill_t", "Repair stale local team counts and fill available T spawns", (player, info) => FillLocalTTeam());
        AddCommand("css_mccs_camera", "Cycle Minecraft first/rear/front camera", (player, info) => {
            if (!LocalUniqueTSession() || !connected || !mcReady || view == null
                || view.ReadInt32(24) == 0 || view.ReadInt32(28) == 0 || view.ReadInt32(136) != 0) return;
            // CS key bindings only dispatch during gameplay, unlike renderer polling
            // which also sees console text and unreliable OS cursor visibility.
            int next = (Math.Clamp(view.ReadInt32(232), 0, 2) + 1) % 3;
            view.Write(232, next);
            Log($"Camera key command requested mode={next}");
        });
        AddCommand("css_mc_demo", "Build a small real Minecraft test pillar", (player, info) => {
            Send(new {t="cmd", c="setblock ~3 ~ ~ minecraft:gold_block"});
            Send(new {t="cmd", c="setblock ~3 ~1 ~ minecraft:gold_block"});
        });
        _ = Task.Run(PipeLoop);
        _ = Task.Run(async()=>{await foreach(var entry in diagnostics.Reader.ReadAllAsync()){try{if(entry.append)await File.AppendAllTextAsync(entry.path,entry.text);else await File.WriteAllTextAsync(entry.path,entry.text);}catch(IOException){}catch(UnauthorizedAccessException){}}});
        Log("loaded; insecure option verified; no CS2 network sockets used");
    }
    void WriteDiagnostic(string name,string text,bool append=false)=>diagnostics.Writer.TryWrite((Path.Combine(Root,"logs",name),text,append));
    void Log(string message)=>WriteDiagnostic("cs2-bridge.log",DateTime.Now.ToString("O")+" "+message+Environment.NewLine,true);
    void Send(object data) {if(connected)outgoing.Writer.TryWrite(JsonSerializer.Serialize(data));}
    void SuspendInput(bool close) {
        ReleaseMinecraftMovement("suspend");
        ResetBuildingMotion();ReleaseTestDuck();
        shieldRaised=false;shieldReceivedAt=0;
        ReleaseAvatarCamera();
        if(view==null)return;
        view.Write(24,0);view.Write(96,0L);view.Write(148,0);view.Write(136,0);
        view.Write(184,view.ReadInt64(176));previousKeys=0;lastInputActive=false;
        Send(new {t="resetinput",close});
        if(lastBuild){Server.ExecuteCommand("r_drawviewmodel 1");lastBuild=false;}
    }
    void ClearHostEntities() {
        StopDance();
        ResetBuildingMotion();
        ClearActorControls();
        ReleaseAvatarCamera();
        groundWork?.Dispose();groundWork=null;nextGroundScan=0;
        ClearBlockColliders();
        if(probe!=null&&probe.IsValid)probe.Remove();probe=null;
        foreach(var pair in mobProxies.ToArray()) {var p=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid&&p.Index==pair.Key);if(p!=null)RestoreMobProxy(p,pair.Value.name);}
        mobProxies.Clear();mobs.Clear();mobKinds.Clear();mobAim.Clear();projectilePositions.Clear();damageRemainders.Clear();
        foreach(var vm in Utilities.FindAllEntitiesByDesignerName<CBaseViewModel>("predicted_viewmodel"))if(hiddenViewModels.TryGetValue(vm.Index,out var effects)){vm.Effects=effects;Utilities.SetStateChanged(vm,"CBaseEntity","m_fEffects");}
        hiddenViewModels.Clear();
    }
    async Task PipeLoop()
    {
        while (!stop.IsCancellationRequested) {
            try {
                using var pipe = new NamedPipeClientStream(".", "MC-CS-Bridge-v1", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, stop.Token);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush=true };
                while(outgoing.Reader.TryRead(out _)){}
                while(incoming.TryDequeue(out _)){}
                connected=true;Interlocked.Increment(ref connectionGeneration);
                Log("local relay connected");
                var read = Task.Run(async () => { while(!session.IsCancellationRequested) { var line=await reader.ReadLineAsync(session.Token); if(line==null) break; if(line.Length<1048576 && incoming.Count<1024) incoming.Enqueue(line); } }, session.Token);
                var write = Task.Run(async () => { await foreach(var line in outgoing.Reader.ReadAllAsync(session.Token)) await writer.WriteLineAsync(line.AsMemory(),session.Token); }, session.Token);
                await Task.WhenAny(read,write); session.Cancel();
                try { await Task.WhenAll(read,write); } catch(OperationCanceledException) { }
            } catch(Exception e) when (!stop.IsCancellationRequested) { Log("relay wait: "+e.GetType().Name); }
            finally { connected=false; }
            try { await Task.Delay(1000,stop.Token); } catch(OperationCanceledException) { break; }
        }
    }
    static float[] Position(Vector p) => [p.X/Units, p.Z/Units+YOffset, -p.Y/Units];
    void Tick()
    {
        if(view==null) return;
        long tickStart=System.Diagnostics.Stopwatch.GetTimestamp();
        try {
            var players=Utilities.GetPlayers();
            TrackPlantedC4();
            var humans=players.Where(p=>p.IsValid && !p.IsBot).ToArray();
            var address=humans.Length==1?humans[0].IpAddress:null;
            bool allowed=humans.Length==1 && (string.IsNullOrEmpty(address)||address.StartsWith("127.")||address.StartsWith("loopback")||address=="::1");
            if(observedGeneration!=connectionGeneration){ReleaseBlockResync("generation");observedGeneration=connectionGeneration;mcReady=false;lastMCPulse=0;needsResync=true;ground.Clear();lastWidth=lastHeight=0;previousSlot=-1;SuspendInput(true);ClearHostEntities();}
            // Read control/health traffic even while the player has no living pawn.
            for(int n=0;n<128 && incoming.TryDequeue(out var pending);n++){
                JsonDocument parsed;try{parsed=JsonDocument.Parse(pending);}catch(JsonException){Log("discarded malformed relay frame");continue;}using var message=parsed;var root=message.RootElement;var type=root.GetProperty("t").GetString();
                if(type=="hello"){Log("Minecraft handshake received");ReleaseMinecraftMovement("minecraft_hello");movementRecoveryAt=0;lastActorHit=0;needsResync=true;if(Server.MapName=="de_inferno"){Send(new{t="cmd",c=FillCommand(InfernoBRange,"minecraft:dirt")});Send(new{t="cmd",c=FillCommand(InfernoARange,"minecraft:dirt")});}continue;}
                if(HandleBlockResync(root))continue;
                if(type=="ui"){lastMCPulse=Environment.TickCount64;mcReady=!root.TryGetProperty("ready",out var ready)||ready.GetBoolean();view.Write(136,root.GetProperty("open").GetBoolean()?1:0);continue;}
                if(type=="mobdamage"||type=="uistate"){Log(pending);WriteDiagnostic(type=="uistate"?"mc-ui-state.json":"mc-mob-damage.json",pending);continue;}
                if(allowed && humans[0].PlayerPawn.Value is {IsValid:true} p){HandleIncoming(pending,humans[0],p);}
                else {needsResync=true;}
            }
            bool linkHealthy=connected && mcReady && Environment.TickCount64-lastMCPulse<1500;
            TickBlockResync(allowed && linkHealthy && SupportedMap(Server.MapName));
            // Refuse non-local/multiple-human sessions. Bots remain normal local server actors.
            if(!allowed) { StopDance();ClearActorControls();SuspendInput(true); return; }
            var player=humans[0]; var pawn=player.PlayerPawn.Value;
            if(!linkHealthy){if(linkWasHealthy){SuspendInput(true);ClearHostEntities();ground.Clear();needsResync=true;Log("MC heartbeat lost: native entities and input released");}linkWasHealthy=false;view.Write(24,0);return;}
            linkWasHealthy=true;
            TickActorInteractions(players);
            if(pawn==null || !pawn.IsValid || pawn.AbsOrigin==null) {SuspendInput(true);return;}
            if(previousMap!=Server.MapName) { previousMap=Server.MapName; ground.Clear();ClearHostEntities();needsResync=true;LoadGeometry(previousMap);Log("map="+previousMap); }
            if(!SupportedMap(Server.MapName)){SuspendInput(true);return;}
            TickDance(player,players);
            if(needsResync){bool held=!MinecraftMovementActive && BeginBlockResync(player);ClearHostEntities();Send(new {t="resetinput",close=true});Send(new {t="clear"});ground.Clear();if(!held)Send(new {t="blocksync",r=24});needsResync=false;Log("MC state resynchronized");}
            if(frame%4==0)UpdateMobProxies(players);
            if(wasAlive&&!player.PawnIsAlive)SuspendInput(true);
            if(!wasAlive&&player.PawnIsAlive){previousSlot=-1;Send(new {t="resetinput",close=true});Log("local player respawn input reset");}
            wasAlive=player.PawnIsAlive;
            if(frame%3==0)SendPlayerVitals(player,pawn);
            int width=view.ReadInt32(80),height=view.ReadInt32(84);
            if(width>=640 && height>=360 && (width!=lastWidth || height!=lastHeight)) {lastWidth=width;lastHeight=height;Send(new {t="view",w=width,h=height});}
            bool movementBuild=player.PawnIsAlive && geometry!=null && view.ReadInt32(28)!=0 && Environment.TickCount64-view.ReadInt64(112)<1000;
            // Fullscreen transitions/minimization pause Present, not the local physics session.
            // Releasing here briefly re-enabled CS physics and re-anchored MC below thin floors.
            // Link health, pawn lifetime and mcstate timeouts still release a genuinely lost owner.
            TickMinecraftMovement(player,pawn,player.PawnIsAlive && geometry!=null);
            TickBuildingMotion(player,pawn,movementBuild);
            var feet=Position(pawn.AbsOrigin);
            StreamGround(players);
            var camera=new[]{feet[0],feet[1]+pawn.ViewOffset.Z/Units,feet[2]};
            if(minecraftMovement is {} movementCamera)camera=[movementCamera.RenderEye.X,movementCamera.RenderEye.Y,movementCamera.RenderEye.Z];
            // V_angle is the player's input orientation; EyeAngles can reflect a view entity.
            var yaw=-pawn.V_angle.Y-90f; var pitch=pawn.V_angle.X;
            var fov=(float)(2*Math.Atan(Math.Tan(90*Math.PI/360)/ (4.0/3))*180/Math.PI);
            bool build=player.PawnIsAlive && geometry!=null && view.ReadInt32(28)!=0 && Environment.TickCount64-view.ReadInt64(112)<1000;
            var lookYaw=yaw;var lookPitch=pitch;
            int cameraMode=UpdateAvatarCamera(pawn,build||MinecraftMovementActive,ref camera,ref yaw,ref pitch);
            long seq=++frame;
            view.Write(8,seq*2+1);
            view.Write(16,Environment.TickCount64); view.Write(24,player.PawnIsAlive&&geometry!=null&&renderEnabled?1:0);
            view.Write(208,System.Diagnostics.Stopwatch.GetTimestamp());
            view.Write(216,System.Diagnostics.Stopwatch.Frequency);
            float[] pose=[camera[0],camera[1],camera[2],yaw,pitch,fov,feet[0],feet[1],feet[2]];
            watermarkOrigin=[camera[0],camera[1],camera[2]];watermarkYaw=yaw;watermarkPitch=pitch;
            if(frame%2==0)UpdateFollowWatermark();
            if(frame%10==0)RescueFallenPlayer(players,player);
            view.WriteArray(32,pose,0,pose.Length); view.Write(72,Units);
            view.Write(1968,lookYaw);view.Write(1972,lookPitch);view.Write(1976,cameraMode);
            view.Write(8,seq*2+2);
            bool inputActive=build && (view.ReadInt32(164)!=0||testing);
            bool movementInputActive=MinecraftMovementActive && Environment.TickCount64-view.ReadInt64(112)<1000 && (view.ReadInt32(164)!=0||testing);
            if(lastInputActive&&!inputActive)Send(new {t="resetinput",close=!build});
            lastInputActive=inputActive;
            if(frame%4==0)Send(new {t="control",active=inputActive||movementInputActive,close=!build});
            Send(new {t="cam",f=seq,p=camera,r=new[]{yaw,pitch,0},fov,fp=cameraMode==0,front=cameraMode==2,pl=feet,h=lookYaw,look=new[]{lookYaw,lookPitch},sneak=HostSneaking(player,pawn),grounded=(pawn.Flags&1)!=0,body=HostBody(pawn),eye=pawn.ViewOffset.Z/Units,mcMove=MinecraftMovementActive,moveSession=minecraftMovement?.Session??0,map=Server.MapName,anchor=minecraftMovement is {} movement?new[]{movement.Anchor.X,movement.Anchor.Y,movement.Anchor.Z}:feet});
            if(build!=lastBuild) { lastBuild=build; Server.ExecuteCommand(build ? "r_drawviewmodel 0" : "r_drawviewmodel 1");
                if(player.PawnIsAlive)player.PrintToChat(build ? "[MC] MC道具模式：右键放置，左键挖掘，E背包；F7切回CS。" : "[MC] CS枪械模式：按F7切回MC道具模式。"); }
            if(build) {
                var weapon=pawn.WeaponServices?.ActiveWeapon.Value;
                if(weapon!=null && weapon.IsValid) {weapon.NextPrimaryAttackTick=Server.TickCount+2;weapon.NextSecondaryAttackTick=Server.TickCount+2;}
            }
            var keys=inputActive ? view.ReadInt64(96) : 0;
            bool ui=view.ReadInt32(136)!=0;
            UpdateMinecraftMovementInput(movementInputActive,ui);
            uint serial=view.ReadUInt32(160); // 164..167 is the separate focus flag.
            pointerSerial=serial; // Pointer events are also queued below, preserving short clicks.
            long written=view.ReadInt64(176),read=view.ReadInt64(184);
            if(written-read>64)read=written-64;
            for(;read<written;read++) {
                long at=256+(read%64)*24;int kind=view.ReadInt32(at);
                if(kind==5){int key=view.ReadInt32(at+4);bool down=view.ReadInt32(at+16)!=0;string[] moves=["forward","back","left","right","jump","sneak","sprint"];if(MinecraftMovementActive && key>=0 && key<moves.Length && (!down || (movementInputActive&&!ui)))Send(new {t="move",session=minecraftMovement!.Session,k=moves[key],down});continue;}
                if(!inputActive)continue;
                if(kind==3){
                    int key=view.ReadInt32(at+4);bool down=view.ReadInt32(at+16)!=0;
                    string[] actions=["attack","use","inventory","drop","swap"];
                    if(key>=0&&key<actions.Length&&(!ui||!down))Send(new {t="key",k=actions[key],down});
                    continue;
                }
                if(!ui)continue;
                if(kind==4){Send(new {t="pointer",x=view.ReadSingle(at+4),y=view.ReadSingle(at+8),buttons=view.ReadInt32(at+12),wheel=view.ReadInt32(at+16),shift=view.ReadInt32(at+20)!=0});continue;}
                if(kind==1)Send(new {t="uikey",key=view.ReadInt32(at+4),code=view.ReadInt32(at+8),mods=view.ReadInt32(at+12),down=view.ReadInt32(at+16)!=0});
                else if(kind==2){int cp=view.ReadInt32(at+4);if(cp>=32&&cp<=0x10ffff)Send(new {t="uitext",text=char.ConvertFromUtf32(cp)});}
            }
            view.Write(184,read);
            // Native input events above preserve taps between server ticks.
            // The shared bit mask is diagnostic only; do not deliver twice.
            previousKeys=keys;
            int slot=view.ReadInt32(104);
            if(inputActive && !ui && slot>=0 && slot<=8 && slot!=previousSlot) {previousSlot=slot;Send(new {t="slot",n=slot});}
            if(frame%8==0) {
                // Sample only observed support beneath real grounded players, not an invented flat map.
                var columns=new List<int>();
                foreach(var p in players.Where(p=>p.PawnIsAlive)) {
                    var pp=p.PlayerPawn.Value; if(pp?.AbsOrigin==null || (pp.Flags&1)==0 || (!p.IsBot && MinecraftMovementActive))continue;
                    // A pawn can overhang the edge of a real MC block while still grounded.
                    // Do not turn that neighbouring air cell into an invisible host barrier.
                    if(pp.GroundEntity.Value is {} support && colliderScales.ContainsKey(support.Index))continue;
                    var f=Position(pp.AbsOrigin); int x=(int)Math.Floor(f[0]), y=(int)Math.Floor(f[1]-0.01f),z=(int)Math.Floor(f[2]);
                    if(MutableTerrainCell(Server.MapName,x,y,z))continue;
                    if(ground.Add((x,y,z)))columns.AddRange([x,z,y,y]);
                }
                if(columns.Count>0)Send(new {t="ground",c=columns});
            }
            if(frame%3==0) {
                // Keep moving target bodies close to MC's 20 Hz simulation, independently of terrain probes.
                var bots=players.Where(p=>p.IsBot && (p.TeamNum==2||p.TeamNum==3) && p.PawnIsAlive && !mobProxies.ContainsKey(p.Index) && p.PlayerPawn.Value?.AbsOrigin!=null).Select(p=>{var pp=p.PlayerPawn.Value!;var pos=Position(pp.AbsOrigin!);return new double[]{p.Index,pos[0],pos[1],pos[2],pp.Health,ActorLife(p),(pp.Flags&1)!=0?1:0,p.TeamNum,ActorScale(p),IsJuvenile(p)?1:0};}).ToArray();
                Send(new {t="peds",stride=10,p=bots});
            }
            if(DateTime.UtcNow>=nextLog) {nextLog=DateTime.UtcNow.AddSeconds(5);Log($"tick={frame} pipe={connected} feet={string.Join(',',feet)} yaw={yaw} build={build}");}
        } catch(Exception e) {ReleaseMinecraftMovement("host_error");ReleaseBlockResync("host_error");view.Write(24,0);if(DateTime.UtcNow>=nextLog){nextLog=DateTime.UtcNow.AddSeconds(5);Log("tick error: "+e);}}
        finally{
            tickTimes[tickSamples++]=System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
            if(tickSamples==tickTimes.Length){var sorted=tickTimes.Order().ToArray();tickSamples=0;WriteDiagnostic("plugin-timing.json",JsonSerializer.Serialize(new {time=DateTime.Now,meanMs=sorted.Average(),p95Ms=sorted[(int)(sorted.Length*.95)],maxMs=sorted[^1],ground=ground.Count}));}
        }
    }
    void ApplyBuildMode(){
        ClearInteractionSandbox();
        if(SupportedMap(Server.MapName) && File.Exists(Path.Combine(Root,"games/Counter-Strike 2/csgo/cfg/mccs_build_mode.cfg"))) {
            Server.ExecuteCommand("exec mccs_build_mode");
            AddTimer(1f,()=>StartDefaultTAgents());
            Log("Local unlimited-warmup config applied; preparing default10 T agents");
        }
    }
    void StreamGround(List<CCSPlayerController> players){
        var index=surfaceIndex;if(index==null)return;
        if(groundWork==null){
            if(Environment.TickCount64<nextGroundScan)return;
            var centers=mobs.Values.Select(p=>new[]{p.X,p.Y,p.Z}).ToList();
            centers.AddRange(players.Where(p=>p.PawnIsAlive&&!mobProxies.ContainsKey(p.Index)&&p.PlayerPawn.Value?.AbsOrigin!=null).Select(p=>Position(p.PlayerPawn.Value!.AbsOrigin!)));
            groundWork=index.Near(centers).GetEnumerator();
        }
        long start=System.Diagnostics.Stopwatch.GetTimestamp();var columns=new List<int>(512);
        for(int visited=0;visited<2048&&columns.Count<512;visited++){
            if(!groundWork.MoveNext()){groundWork.Dispose();groundWork=null;nextGroundScan=Environment.TickCount64+1000;break;}
            var c=groundWork.Current;if(!MutableTerrainCell(Server.MapName,c.x,c.y,c.z)&&ground.Add(c))columns.AddRange([c.x,c.z,c.y,c.y]);
            if((visited&63)==0&&System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds>=.75)break;
        }
        if(columns.Count>0)Send(new {t="ground",c=columns});
    }
    void HandleIncoming(string message,CCSPlayerController player,CCSPlayerPawn pawn)
    {
        using var doc=JsonDocument.Parse(message);var m=doc.RootElement;var type=m.GetProperty("t").GetString();
        if(type!="director" && !SupportedMap(Server.MapName))return;
        if(type=="hello") {Log("Minecraft handshake received");ReleaseMinecraftMovement("minecraft_hello");movementRecoveryAt=0;Send(new {t="blocksync",r=16});}
        else if(type=="mcstate")ApplyMinecraftMovement(m,pawn);
        else if(type is "playerheal" or "playerabsorption" or "vitalsfx")ApplyPlayerVitals(m,player,pawn,type);
        else if(type=="actorcontrol")ApplyActorControl(m);
        else if(type=="actorbreed")ApplyActorBreed(m,player);
        else if(type=="spawnjuvenile")ApplySpawnJuvenile(m);
        else if(type is "actorinteractionstate" or "actorinteractstate" or "blockinspect") {Log(message);WriteDiagnostic(type+".json",message);}
        else if(type=="shieldstate"){shieldRaised=m.GetProperty("blocking").GetBoolean();shieldReceivedAt=Environment.TickCount64;}
        else if(type=="ui")view!.Write(136,m.GetProperty("open").GetBoolean()?1:0);
        else if(type=="director" && m.TryGetProperty("host",out var host) && host.GetString()=="cs2") {
            var op=m.GetProperty("op").GetString();
            if(op=="capture")view!.Write(120,view.ReadInt32(120)+1);
            else if(op=="dance")SetDance(m.TryGetProperty("on",out var danceEnabled)?danceEnabled.GetBoolean():!danceOn,player);
            else if(op=="dance_toggle")SetDance(!danceOn,player);
            else if(op=="dance_stick")SendDanceStick();
            else if(op=="watermark")PlaceWatermark(player);
            else if(op=="watermark_follow")ToggleFollowWatermark(player);
            else if(op=="watermark_item")GiveWatermarkItem();
            else if(op=="watermark_clear")ClearWatermark();
            else if(op=="dance_state")WriteDiagnostic("dance-state.json",JsonSerializer.Serialize(DanceSnapshot(),new JsonSerializerOptions{WriteIndented=true}));
            else if(op=="building_mode")ApplyBuildMode();
            else if(op=="combat_mode"){ClearInteractionSandbox();Server.ExecuteCommand("exec mccs_combat_mode");}
            else if(op=="ct_interactions")ApplyCTInteractionMode(player);
            else if(op=="actor_controls")WriteDiagnostic("actor-controls.json",JsonSerializer.Serialize(new{holds=actorHolds.Select(p=>new{id=p.Key,life=p.Value.Life,mode=p.Value.Mode,p=new[]{p.Value.Position.X,p.Value.Position.Y,p.Value.Position.Z}}),pendingBirth,children=ActorChildrenSnapshot()}));
            else if(op=="block_colliders")InspectBlockColliders(m);
            else if(op=="building_motion")WriteDiagnostic("building-motion.json",JsonSerializer.Serialize(BuildingMotionSnapshot(player,pawn)));
            else if(op=="minecraft_movement")WriteDiagnostic("minecraft-movement.json",JsonSerializer.Serialize(MinecraftMovementSnapshot()));
            else if(op=="test_movement" && testing){movementTestKeys=(ulong)Math.Clamp(m.GetProperty("mask").GetInt32(),0,127);if(m.TryGetProperty("clear",out var clearTrace)&&clearTrace.GetBoolean())minecraftMovementTrace.Clear();}
            else if(op=="movement_mode"){minecraftMovementEnabled=m.GetProperty("on").GetBoolean();movementRecoveryAt=0;movementLastError=null;ReleaseMinecraftMovement("mode_changed");}
            else if(op=="minecraft_movement_keys"){Server.ExecuteCommand("exec mccs_minecraft_movement");}
            else if(op=="native_movement"){minecraftMovementEnabled=false;ReleaseMinecraftMovement("native_mode_requested");ReleaseTestDuck();ResetBuildingMotion();Server.ExecuteCommand("exec mccs_native_movement");Log("Native CS movement: no sneak edge correction or underfoot lift; requested original movement keys");}
            else if(op=="test_duck" && testing)SetTestDuck(pawn,m.GetProperty("on").GetBoolean());
            else if(op=="resync")needsResync=true;
            else if(op=="test_damage" && testing)TestNativeDamage(player,m);
            else if(op=="test_c4" && testing)TestPlantC4(m);
            else if(op=="test_blast" && testing)TestBlast(m);
            else if(op=="test_c4_prepare" && testing)PrepareNativeC4Test();
            else if(op=="test_c4_finish")FinishNativeC4Test();
            else if(op=="test_bot_fire" && testing){bool on=m.GetProperty("on").GetBoolean();Server.ExecuteCommand(on?"bot_stop 0":"bot_stop 1");Server.ExecuteCommand(on?"bot_zombie 0":"bot_zombie 1");}
            else if(op=="test_turn" && testing)Server.ExecuteCommand(m.GetProperty("on").GetBoolean()?"+turnleft":"-turnleft");
            else if(op=="depth") {view!.Write(124,m.GetProperty("debug").GetInt32());view.Write(128,m.GetProperty("near").GetSingle());view.Write(132,m.GetProperty("reversed").GetBoolean()?2:0);}
            else if(op=="build")view!.Write(28,m.GetProperty("on").GetBoolean()?1:0);
            else if(op=="benchmark_render")renderEnabled=m.GetProperty("on").GetBoolean();
            else if(op=="benchmark_fps")Server.ExecuteCommand("fps_max "+Math.Clamp(m.GetProperty("fps").GetInt32(),0,240));
            else if(op=="pose_smoothing")view!.Write(200,m.GetProperty("on").GetBoolean()?1:0);
            else if(op=="camera")view!.Write(232,Math.Clamp(m.GetProperty("mode").GetInt32(),0,2));
            else if(op=="freeze_bots")Server.ExecuteCommand(m.GetProperty("on").GetBoolean()?"bot_stop 1":"bot_stop 0");
            else if(op=="test_mode") {bool on=m.GetProperty("on").GetBoolean();testing=on;movementTestKeys=null;minecraftMovementTrace.Clear();ReleaseTestDuck();ResetBuildingMotion();Server.ExecuteCommand(on?"bot_stop 1":"bot_stop 0");Server.ExecuteCommand(on?"bot_zombie 1":"bot_zombie 0");Server.ExecuteCommand(on?"mp_ignore_round_win_conditions 1":"mp_ignore_round_win_conditions 0");if(on){ReleaseBlockResync("test_respawn");player.Respawn();}else {FinishNativeC4Test();Server.ExecuteCommand("-forward");Server.ExecuteCommand("-turnleft");}}
            else if(op=="walk" && testing)Server.ExecuteCommand(m.GetProperty("on").GetBoolean()?"+forward":"-forward");
            else if(op=="respawn"){ReleaseBlockResync("respawn");player.Respawn();}
            else if(op=="test_death" && testing)pawn.CommitSuicide(false,true);
            else if(op=="restart_round" && testing)Server.ExecuteCommand("mp_restartgame 1");
            else if(op=="test_map" && testing && m.TryGetProperty("map",out var targetMap) && targetMap.GetString() is {} target && SupportedMap(target))Server.ExecuteCommand("changelevel "+target);
            else if(op=="reload_map" && testing && SupportedMap(Server.MapName))Server.ExecuteCommand("changelevel "+Server.MapName);
            else if(op=="probe_spawn"){
                if(probe!=null&&probe.IsValid)probe.Remove();probe=Utilities.CreateEntityByName<CPhysicsPropOverride>("prop_physics_override");
                if(probe!=null){probe.SetModel(CubeModel);probe.DispatchSpawn();probe.AcceptInput("SetScale",probe,probe,"0.5");probe.AcceptInput("EnableMotion");probe.AcceptInput("DisableGravity");probe.Teleport(new Vector(120,2300,-80),new QAngle(0,0,0),new Vector(1000,0,0));}
            }
            else if(op=="probe_remove"){if(probe!=null&&probe.IsValid)probe.Remove();probe=null;}
            else if(op=="test_bot"){
                var bot=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.IsValid && !mobProxies.ContainsKey(p.Index) && (!m.TryGetProperty("team",out var team)||p.TeamNum==team.GetInt32()) && (!m.TryGetProperty("id",out var targetId)||p.Index==targetId.GetInt32()));var b=bot?.PlayerPawn.Value;
                if(bot!=null){bot.Respawn();b=bot.PlayerPawn.Value;if(b!=null){var p=m.GetProperty("p");b.Teleport(new Vector(p[0].GetSingle(),p[1].GetSingle(),p[2].GetSingle()),new QAngle(0,180,0),new Vector(0,0,0));b.Health=100;Utilities.SetStateChanged(b,"CBaseEntity","m_iHealth");Log("test bot id="+bot.Index);}}
            }
            else if(op=="pose") {
                ReleaseBlockResync("director_pose");
                ReleaseMinecraftMovement("director_pose");ResetBuildingMotion();
                var pos=m.GetProperty("p");var ang=m.GetProperty("r");
                pawn.Teleport(new Vector(pos[0].GetSingle(),pos[1].GetSingle(),pos[2].GetSingle()),new QAngle(ang[0].GetSingle(),ang[1].GetSingle(),0),new Vector(0,0,0));
            }
            else if(op=="velocity") {var v=m.GetProperty("v");pawn.Teleport(null,null,new Vector(v[0].GetSingle(),v[1].GetSingle(),v[2].GetSingle()));}
            else if(op=="interaction_peaceful") { interactionSandbox=true;Server.ExecuteCommand("bot_ignore_players 1"); }
            else if(op=="team_capacity")WriteTeamCapacity();
            else if(op=="fill_t_team")FillLocalTTeam();
            else if(op=="inspect") {
                var state=new {time=DateTime.Now,map=Server.MapName,connected,mcReady,lastMCPulse,vitalsLife,playerAbsorption,resistanceLevel,playerFireproof,lastVitalsEvent,active=view!.ReadInt32(24),inputActive=lastInputActive,ui=view.ReadInt32(136),cameraMode=view.ReadInt32(236),cameraRequested=view.ReadInt32(232),cameraDistance=view.ReadSingle(240),cameraEntity=avatarCamera?.IsValid==true?avatarCamera.Index:0,hiddenAvatarEntities=hiddenAvatar.Count,uiEventsWritten=view.ReadInt64(176),uiEventsRead=view.ReadInt64(184),validBlocks=blocks.Count,blockBodies=BlockBodyCount,c4ShieldBlockedHits,blockResync=BlockResyncSnapshot(),movement=MinecraftMovementSnapshot(),probe=probe?.IsValid==true&&probe.AbsOrigin!=null?new[]{probe.AbsOrigin.X,probe.AbsOrigin.Y,probe.AbsOrigin.Z}:null,blocks=blocks.Keys.Select(k=>new[]{k.Item1,k.Item2,k.Item3}),mobs=mobs.Select(p=>new{id=p.Key,kind=mobKinds.GetValueOrDefault(p.Key),pos=new[]{p.Value.X,p.Value.Y,p.Value.Z}}),proxies=mobProxies.Select(p=>new{bot=p.Key,mob=p.Value.id}),
                    players=Utilities.GetPlayers().Where(p=>p.IsValid).Select(p=>new {id=p.Index,team=p.TeamNum,name=p.PlayerName,bot=p.IsBot,alive=p.PawnIsAlive,hp=p.PlayerPawn.Value?.Health,pos=p.PlayerPawn.Value?.AbsOrigin==null?null:new[]{p.PlayerPawn.Value.AbsOrigin.X,p.PlayerPawn.Value.AbsOrigin.Y,p.PlayerPawn.Value.AbsOrigin.Z},enemy=p.PlayerPawn.Value?.Bot?.Enemy.Raw,angles=p.PlayerPawn.Value==null?null:new[]{p.PlayerPawn.Value.EyeAngles.X,p.PlayerPawn.Value.EyeAngles.Y}})};
                WriteDiagnostic("cs2-live-state.json",JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));
            }
            Log("local director "+op);
        }
        else if(type=="pteleport") {
            if(MinecraftMovementActive)return; // The authoritative MC state carries this teleport.
            var p=m.GetProperty("pos");pawn.Teleport(new Vector(p[0].GetSingle()*Units,-p[2].GetSingle()*Units,(p[1].GetSingle()-YOffset)*Units),null,new Vector(0,0,0));
            Log("Minecraft teleport applied to host");
        }
        else if(type=="mobs") {mobs.Clear();mobKinds.Clear();mobAim.Clear();foreach(var item in m.GetProperty("m").EnumerateArray()){int id=item[0].GetInt32();mobs[id]=new V3(item[2].GetSingle(),item[3].GetSingle(),item[4].GetSingle());mobKinds[id]=item[1].GetString()??"";if(item.GetArrayLength()>=8)mobAim[id]=new V3(item[5].GetSingle(),item[6].GetSingle(),item[7].GetSingle());}}
        else if(type=="proj") {
            var seen=new HashSet<int>();foreach(var item in m.GetProperty("p").EnumerateArray()){
                int id=item[0].GetInt32();seen.Add(id);var pos=new V3(item[2].GetSingle(),item[3].GetSingle(),item[4].GetSingle());
                if(projectilePositions.TryGetValue(id,out var old)){
                    bool stopped=false;var hitCS=V3.Zero;
                    if(geometry?.Ray(WorldGeometry.ToCS(old),WorldGeometry.ToCS(pos),out hitCS)==true){var hit=WorldGeometry.ToMC(hitCS);Send(new {t="projhit",id,pos=new[]{hit.X,hit.Y,hit.Z},stick=true});stopped=true;}
                    // Actor hits are resolved by Minecraft's real projectile collision and damage rules.
                    if(stopped){projectilePositions.Remove(id);continue;}
                }
                projectilePositions[id]=pos;
            }
            foreach(var id in projectilePositions.Keys.Where(id=>!seen.Contains(id)).ToArray())projectilePositions.Remove(id);
        }
        else if(type=="blocks") ApplyBlockUpdates(m);
        else if(type=="explosion") {
            // Explosion exposure, block occlusion and damage already ran against MC actor proxies.
            // Applying the old radius approximation here would hurt every victim twice.
            Log("MC explosion observed: "+message);
        }
        else if(type=="actorhit") {
            long serial=m.GetProperty("seq").GetInt64();if(serial<=lastActorHit)return;lastActorHit=serial;
            double amount=m.GetProperty("d").GetDouble();if(!double.IsFinite(amount)||amount<=0)return;
            int index=m.GetProperty("h").GetInt32();long life=m.GetProperty("life").GetInt64();
            var victim=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid&&p.IsBot&&p.Index==index&&p.PawnIsAlive&&ActorLife(p)==life);
            if(victim==null||mobProxies.ContainsKey(victim.Index))return;
            string kind=m.GetProperty("k").GetString()??"generic";
            double scaled=Math.Min(amount*5,10000)+damageRemainders.GetValueOrDefault(life);
            int damage=(int)Math.Floor(scaled+0.0001);damageRemainders[life]=scaled-damage;
            var target=victim.PlayerPawn.Value!;int before=target.Health;
            bool playerOwned=m.TryGetProperty("player",out var owner)&&owner.GetBoolean();
            // Offline practice: a real player-owned MC damage event kills the target bot.
            // Keep vanilla hit/occlusion validation and native hurt/blood/death feedback.
            bool oneHit=playerOwned && before>0;
            if(oneHit)damage=Math.Max(damage,before);
            if(damage>0)HurtNative(victim,damage,kind,playerOwned?player:null,ReadCombatPoint(m,"from",pawn.AbsOrigin!),ReadCombatPoint(m,"hit",target.AbsOrigin!));
            WriteDiagnostic("combat-events.jsonl",JsonSerializer.Serialize(new{time=DateTime.Now,seq=serial,id=index,life,kind,mcDamage=amount,oneHit,damage,before,after=target.Health})+Environment.NewLine,true);
        }
    }
    void UpdateMobProxies(List<CCSPlayerController> players){
        if(mobSpawnExisting!=null){
            var added=players.FirstOrDefault(p=>p.IsBot&&p.TeamNum==3&&!mobSpawnExisting.Contains(p.Index));
            if(added!=null){nativeMobBots.Add(added.Index);mobSpawnExisting=null;}
            else if(Environment.TickCount64>=nextMobBotAttempt)mobSpawnExisting=null;
        }
        foreach(var pair in mobProxies.ToArray()){
            var player=players.FirstOrDefault(p=>p.Index==pair.Key);
            if(player==null){mobProxies.Remove(pair.Key);nativeMobBots.Remove(pair.Key);continue;}
            if(!mobs.ContainsKey(pair.Value.id)||IsBarrageBoss(pair.Value.id)){RestoreMobProxy(player,pair.Value.name);mobProxies.Remove(pair.Key);}
        }
        // Barrage bosses use bullet ray hits and directed barrage; no extra invisible CT slot is needed.
        foreach(var mob in mobs.Where(m=>!IsBarrageBoss(m.Key))){
            if(mobProxies.Count>=4)break;if(mobProxies.Values.Any(p=>p.id==mob.Key))continue;
            var target=players.FirstOrDefault(p=>p.IsBot&&p.PawnIsAlive&&p.TeamNum==3&&nativeMobBots.Contains(p.Index)&&!mobProxies.ContainsKey(p.Index));
            if(target==null){
                if(nativeMobBots.Count<Math.Min(4,mobs.Count)&&mobSpawnExisting==null&&Environment.TickCount64>=nextMobBotAttempt){
                    mobSpawnExisting=players.Select(p=>p.Index).ToHashSet();nextMobBotAttempt=Environment.TickCount64+1500;
                    Server.ExecuteCommand("bot_add_ct");
                }
                break;
            }
            mobProxies[target.Index]=(mob.Key,target.PlayerName);target.PlayerName="MC_BRIDGE_"+mob.Key;Utilities.SetStateChanged(target,"CBasePlayerController","m_iszPlayerName");
            target.PlayerPawn.Value!.ItemServices?.As<CCSPlayer_ItemServices>().RemoveWeapons();Log($"MC ally {mob.Key} represented by dedicated CT bot {target.Index}");
        }
        foreach(var pair in mobProxies){
            var player=players.FirstOrDefault(p=>p.Index==pair.Key);var pawn=player?.PlayerPawn.Value;if(pawn==null||!mobs.TryGetValue(pair.Value.id,out var at))continue;
            if(!player!.PawnIsAlive){player.Respawn();continue;}
            var pos=WorldGeometry.ToCS(at);pawn.MoveType=MoveType_t.MOVETYPE_NONE;pawn.ActualMoveType=MoveType_t.MOVETYPE_NONE;
            pawn.Teleport(new Vector(pos.X,pos.Y,pos.Z),null,new Vector(0,0,0));pawn.Health=1000;pawn.Render=Color.FromArgb(0,255,255,255);pawn.RenderMode=RenderMode_t.kRenderTransColor;
            Utilities.SetStateChanged(pawn,"CBaseModelEntity","m_clrRender");Utilities.SetStateChanged(pawn,"CBaseModelEntity","m_nRenderMode");
            var weapon=pawn.WeaponServices?.ActiveWeapon.Value;if(weapon!=null){weapon.NextPrimaryAttackTick=Server.TickCount+8;weapon.NextSecondaryAttackTick=Server.TickCount+8;}
        }
        // If a guest mob disappears before its bot is assigned, discard only our newly created bot.
        if(mobs.Count==0)foreach(var id in nativeMobBots.ToArray()){
            var p=players.FirstOrDefault(p=>p.Index==id);if(p!=null)RestoreMobProxy(p,p.PlayerName);else nativeMobBots.Remove(id);
        }
    }
    void RestoreMobProxy(CCSPlayerController player,string name){
        if(nativeMobBots.Remove(player.Index)){
            player.PlayerName="MC_BRIDGE_REMOVE_"+player.Index;Utilities.SetStateChanged(player,"CBasePlayerController","m_iszPlayerName");
            Server.ExecuteCommand("bot_kick "+player.PlayerName);return;
        }
        // Compatibility cleanup for a proxy created by an older plugin version.
        player.PlayerName=name;Utilities.SetStateChanged(player,"CBasePlayerController","m_iszPlayerName");var pawn=player.PlayerPawn.Value;
        if(pawn!=null){pawn.Render=Color.White;pawn.RenderMode=RenderMode_t.kRenderNormal;pawn.MoveType=MoveType_t.MOVETYPE_WALK;pawn.ActualMoveType=MoveType_t.MOVETYPE_WALK;Utilities.SetStateChanged(pawn,"CBaseModelEntity","m_clrRender");}
        player.Respawn();
    }
    static float SegmentDistance(V3 start,V3 end,V3 point){var d=end-start;float t=d.LengthSquared()<1e-8?0:Math.Clamp(V3.Dot(point-start,d)/d.LengthSquared(),0,1);return V3.Distance(start+d*t,point);}
    bool Occluded(V3 start,V3 end)=>geometry?.Ray(WorldGeometry.ToCS(start),WorldGeometry.ToCS(end),out _)==true;
    void LoadGeometry(string mapName){
        geometryMap=mapName;geometry=null;surfaceCells=null;surfaceIndex=null;
        if(mapName.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))return;
        var path=Path.Combine(Root,"build-cache/map-physics",mapName,"world.tri");if(!File.Exists(path)){Log("No static geometry cache for "+mapName);return;}
        _=Task.Run(()=>{try{var g=new WorldGeometry(path);if(geometryMap!=mapName)return;geometry=g;Log("Loaded map triangles="+g.Count);var cells=g.SurfaceCells().ToArray();var index=new SurfaceIndex(cells);if(geometryMap==mapName){surfaceCells=cells;surfaceIndex=index;Log("Prepared indexed map barrier cells="+cells.Length);}}catch(Exception e){Log("Geometry cache failed: "+e.Message);}});
    }
    public override void Unload(bool hotReload)
    {
        UnloadDanceNative();
        ClearInteractionSandbox();ReleaseBlockResync("unload");
        ClearActorControls();
        FinishNativeC4Test();
        UnloadCombatFeedback();
        SuspendInput(true);
        Server.ExecuteCommand("-turnleft");
        if(testing){Server.ExecuteCommand("bot_stop 0");Server.ExecuteCommand("bot_zombie 0");Server.ExecuteCommand("mp_ignore_round_win_conditions 0");}
        if(probe!=null&&probe.IsValid)probe.Remove();
        Server.ExecuteCommand("-forward");foreach(var pair in mobProxies){var p=Utilities.GetPlayers().FirstOrDefault(p=>p.Index==pair.Key&&p.IsValid);if(p!=null)RestoreMobProxy(p,pair.Value.name);}mobProxies.Clear();
        foreach(var vm in Utilities.FindAllEntitiesByDesignerName<CBaseViewModel>("predicted_viewmodel"))if(hiddenViewModels.TryGetValue(vm.Index,out var effects)){vm.Effects=effects;Utilities.SetStateChanged(vm,"CBaseEntity","m_fEffects");}
        ClearBlockColliders();
        stop.Cancel(); outgoing.Writer.TryComplete(); view?.Write(24,0);view?.Dispose();map?.Dispose();
        Log("unloaded");
        diagnostics.Writer.TryComplete();
    }
}
