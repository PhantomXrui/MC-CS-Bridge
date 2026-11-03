using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3=System.Numerics.Vector3;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    const string ExtraTSpawnId="mccs_breeding_spawn";

    bool EnsureBreedingSpawnCapacity()
    {
        if(!LocalUniqueTSession() || geometry==null)return false;
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p=>p.IsValid)?.GameRules;
        if(rules==null)return false;
        var spawns=Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
            .Where(p=>p.IsValid&&p.Enabled&&p.AbsOrigin!=null).ToList();
        var original=spawns.Where(p=>p.UniqueHammerID!=ExtraTSpawnId).ToArray();
        if(original.Length==0)return false;
        var positions=spawns.Select(p=>new V3(p.AbsOrigin!.X,p.AbsOrigin.Y,p.AbsOrigin.Z)).ToList();
        // Extend only inside the existing T spawn neighbourhood. Validate floor,
        // headroom and body width against map triangles; never duplicate origins.
        foreach(var anchor in original){
            var a=anchor.AbsOrigin!;
            for(int ring=1;ring<=2 && positions.Count<BreedingTBotLimit;ring++)
            for(int dx=-1;dx<=1 && positions.Count<BreedingTBotLimit;dx++)
            for(int dy=-1;dy<=1 && positions.Count<BreedingTBotLimit;dy++){
                if(dx==0&&dy==0)continue;
                var probe=new V3(a.X+dx*64*ring,a.Y+dy*64*ring,a.Z);
                if(!geometry.Ray(probe+new V3(0,0,32),probe-new V3(0,0,48),out var floor))continue;
                var at=floor+new V3(0,0,2);
                if(positions.Any(p=>V3.DistanceSquared(p,at)<60*60))continue;
                bool clear=true;
                for(int x=-1;x<=1 && clear;x++)for(int y=-1;y<=1 && clear;y++){
                    var foot=at+new V3(x*18,y*18,0);
                    if(!geometry.Ray(foot+new V3(0,0,16),foot-new V3(0,0,12),out var support)
                        ||Math.Abs(support.Z-floor.Z)>5 || geometry.Ray(foot+new V3(0,0,6),foot+new V3(0,0,76),out _))clear=false;
                }
                for(int z=10;z<=70 && clear;z+=20){
                    var center=at+new V3(0,0,z);
                    for(int x=-1;x<=1 && clear;x++)for(int y=-1;y<=1 && clear;y++)
                        if(geometry.Ray(center,center+new V3(x*20,y*20,0),out _))clear=false;
                }
                if(!clear)continue;
                var spawn=Utilities.CreateEntityByName<CInfoPlayerTerrorist>("info_player_terrorist");
                if(spawn==null)continue;
                spawn.UniqueHammerID=ExtraTSpawnId;
                spawn.Teleport(new Vector(at.X,at.Y,at.Z),anchor.AbsRotation,new Vector(0,0,0));
                spawn.DispatchSpawn();spawn.Enabled=true;spawn.Priority=anchor.Priority;
                positions.Add(at);Log($"Added validated T spawn at {at}");
            }
            if(positions.Count>=BreedingTBotLimit)break;
        }
        int actual=positions.Count;
        // Counts reflect real enabled entities created above, not imaginary slots.
        rules.SpawnPointCount_Terrorist=actual;
        rules.MaxNumTerrorists=Math.Min(BreedingTBotLimit,actual);
        return actual>=BreedingTBotLimit;
    }

    void StartDefaultTAgents(int attempt=0)
    {
        if(!SupportedMap(Server.MapName))return;
        if(!connected||!mcReady||!LocalUniqueTSession()||geometry==null||!uniqueTPrecached){
            if(attempt<60)AddTimer(.5f,()=>StartDefaultTAgents(attempt+1));return;
        }
        EnsureBreedingSpawnCapacity();
        Server.ExecuteCommand("bot_stop 1; css_mccs_fill_t_unique 10");
        Log("Default local scene: 10 T agents at native spawn points, breeding cap20");
    }

    void FillLocalTTeam(int? maxTBots = null)
    {
        var players = Utilities.GetPlayers().Where(p => p.IsValid && p.Connected.ToString() == "PlayerConnected").ToArray();
        var humans = players.Where(p => !p.IsBot).ToArray();
        var address = humans.Length == 1 ? humans[0].IpAddress : null;
        if (humans.Length != 1 || !(string.IsNullOrEmpty(address) || address.StartsWith("127.") || address.StartsWith("loopback") || address == "::1")) return;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p => p.IsValid)?.GameRules;
        if (rules == null) return;
        int t = players.Count(p => p.TeamNum == 2), ct = players.Count(p => p.TeamNum == 3);
        // A long-running paused-round sandbox can retain pre-kick team counts.
        // Reconcile only from connected controllers; never fabricate spawn capacity.
        Log($"Team counts reconcile T {rules.NumTerrorist}->{t}, CT {rules.NumCT}->{ct}");
        rules.NumTerrorist = t;
        rules.NumCT = ct;
        rules.NumSpawnableTerrorist = t;
        rules.NumSpawnableCT = ct;
        int realSpawns = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist").Count(p => p.IsValid);
        int target = Math.Min(realSpawns, Math.Min(rules.SpawnPointCount_Terrorist, rules.MaxNumTerrorists));
        target = Math.Min(target, Server.MaxPlayers - players.Count(p => p.TeamNum != 2));
        if (maxTBots.HasValue) target = Math.Min(target, maxTBots.Value + humans.Count(p => p.TeamNum == 2));
        target = Math.Max(0, target);
        int quota = Math.Max(0, target - humans.Count(p => p.TeamNum == 2)) + players.Count(p => p.IsBot && p.TeamNum != 2);
        EnsureBreedingSpawnCapacity();
        interactionSandbox = true;interactionUseTestSpawns=false;
        Server.ExecuteCommand("mp_limitteams 0; mp_autoteambalance 0; bot_ignore_players 1; bot_join_team T; bot_quota_mode normal; bot_quota " + quota);
        Log($"Local T fill requested target={target}, quota={quota}, actualSpawns={realSpawns}");
        AddTimer(3.0f, WriteTeamCapacity);
    }

    // Read-only, on-demand diagnosis; does not change rules, spawns, teams, or quota.
    void WriteTeamCapacity()
    {
        var errors = new Dictionary<string, string>();
        object? Read(string key, Func<object?> read)
        {
            try { return read(); }
            catch (Exception exception) { errors[key] = exception.GetType().Name + ": " + exception.Message; return null; }
        }
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault(p => p.IsValid)?.GameRules;
        var fields = new Dictionary<string, object?>();
        if (rules is not null)
        {
            fields["NumTerrorist"] = Read("NumTerrorist", () => rules.NumTerrorist);
            fields["NumCT"] = Read("NumCT", () => rules.NumCT);
            fields["NumSpawnableTerrorist"] = Read("NumSpawnableTerrorist", () => rules.NumSpawnableTerrorist);
            fields["NumSpawnableCT"] = Read("NumSpawnableCT", () => rules.NumSpawnableCT);
            fields["MaxNumTerrorists"] = Read("MaxNumTerrorists", () => rules.MaxNumTerrorists);
            fields["MaxNumCTs"] = Read("MaxNumCTs", () => rules.MaxNumCTs);
            fields["SpawnPointCount_Terrorist"] = Read("SpawnPointCount_Terrorist", () => rules.SpawnPointCount_Terrorist);
            fields["SpawnPointCount_CT"] = Read("SpawnPointCount_CT", () => rules.SpawnPointCount_CT);
            fields["TerroristSpawnPoints"] = Read("TerroristSpawnPoints", () => rules.TerroristSpawnPoints.Count);
            fields["TerroristSpawnPointsMasterList"] = Read("TerroristSpawnPointsMasterList", () => rules.TerroristSpawnPointsMasterList.Count);
            fields["CTSpawnPoints"] = Read("CTSpawnPoints", () => rules.CTSpawnPoints.Count);
            fields["CTSpawnPointsMasterList"] = Read("CTSpawnPointsMasterList", () => rules.CTSpawnPointsMasterList.Count);
        }
        var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Select(p => new Dictionary<string, object?>
            {
                ["index"] = Read("player.index", () => p.Index),
                ["valid"] = p.IsValid,
                ["slot"] = p.IsValid ? Read($"player.{p.Index}.slot", () => p.Slot) : null,
                ["name"] = p.IsValid ? Read($"player.{p.Index}.name", () => p.PlayerName) : null,
                ["bot"] = p.IsValid ? Read($"player.{p.Index}.bot", () => p.IsBot) : null,
                ["team"] = p.IsValid ? Read($"player.{p.Index}.team", () => p.TeamNum) : null,
                ["connected"] = p.IsValid ? Read($"player.{p.Index}.connected", () => p.Connected.ToString()) : null,
                ["alive"] = p.IsValid ? Read($"player.{p.Index}.alive", () => p.PawnIsAlive) : null,
                ["pawnValid"] = p.IsValid ? Read($"player.{p.Index}.pawnValid", () => p.PlayerPawn.Value?.IsValid ?? false) : null
            }).ToArray();
        var spawnsT = Read("spawnsT", () => Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist").Count(p => p.IsValid));
        var spawnsCT = Read("spawnsCT", () => Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist").Count(p => p.IsValid));
        var maxPlayers = Read("Server.MaxPlayers", () => Server.MaxPlayers);
        var report = new
        {
            time = DateTimeOffset.Now.ToString("O"), reason = "director", map = Server.MapName,
            maxPlayers, rulesAvailable = rules is not null, gameRules = fields,
            spawnEntityCounts = new { terrorist = spawnsT, counterTerrorist = spawnsCT },
            players, spawnPositionsT=Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_player_terrorist")
                .Where(p=>p.IsValid&&p.AbsOrigin!=null).Select(p=>new{id=p.Index,enabled=p.Enabled,extra=p.UniqueHammerID==ExtraTSpawnId,
                    pos=new[]{p.AbsOrigin!.X,p.AbsOrigin.Y,p.AbsOrigin.Z}}).ToArray(), errors
        };
        var summary = $"[MCCS capacity] maxPlayers={maxPlayers}; T max={fields.GetValueOrDefault("MaxNumTerrorists")}, spawns={spawnsT}; CT max={fields.GetValueOrDefault("MaxNumCTs")}, spawns={spawnsCT}; controllers={players.Length}; errors={errors.Count}";
        WriteDiagnostic("cs2-capacity.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Server.PrintToConsole(summary);
        Log(summary);
    }
}
