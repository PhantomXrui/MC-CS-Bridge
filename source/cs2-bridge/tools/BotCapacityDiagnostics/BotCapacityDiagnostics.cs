using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotCapacityDiagnostics;

// Read-only local helper: does not alter rules, spawn points, clients, or quotas.
public sealed class BotCapacityDiagnosticsPlugin : BasePlugin
{
    public override string ModuleName => "MC CS Local Bot Capacity Diagnostics";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Codex";
    const string OutputPath = @"D:\MC-CS-Bridge\logs\cs2-capacity.json";

    public override void Load(bool hotReload)
    {
        if (!Environment.CommandLine.Split(' ').Any(x => x.Trim('"').Equals("-insecure", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("BotCapacityDiagnostics requires the isolated -insecure local client.");
        AddCommand("css_mccs_capacity", "Write read-only local bot capacity diagnostics", (player, info) =>
            info.ReplyToCommand(Capture("command")));
        AddTimer(1.0f, () => Capture(hotReload ? "hot_load" : "load"));
    }

    static string Capture(string reason)
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
            time = DateTimeOffset.Now.ToString("O"), reason, map = Server.MapName,
            maxPlayers, rulesAvailable = rules is not null, gameRules = fields,
            spawnEntityCounts = new { terrorist = spawnsT, counterTerrorist = spawnsCT },
            players, errors
        };
        var summary = $"[MCCS capacity] maxPlayers={maxPlayers}; T max={fields.GetValueOrDefault("MaxNumTerrorists")}, spawns={spawnsT}; CT max={fields.GetValueOrDefault("MaxNumCTs")}, spawns={spawnsCT}; controllers={players.Length}; errors={errors.Count}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            File.WriteAllText(OutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) { summary += "; output failed: " + exception.Message; }
        Server.PrintToConsole(summary);
        return summary;
    }
}
