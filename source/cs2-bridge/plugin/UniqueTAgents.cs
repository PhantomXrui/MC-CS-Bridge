using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    readonly UniqueTAllocation uniqueTAllocation = new(64);
    readonly Dictionary<(uint Controller, uint Pawn), int> uniqueTAttempts = new();
    bool uniqueTEnabled, uniqueTPrecached;
    int uniqueTGeneration;

    void LoadUniqueTAgents()
    {
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest => {
            uniqueTPrecached = false;
            foreach (var agent in SimpleDanceAgents) manifest.AddResource(agent.Model);
            uniqueTPrecached = true;
        });
        RegisterListener<Listeners.OnMapEnd>(() => {
            uniqueTEnabled = uniqueTPrecached = false;
            uniqueTGeneration++;
            uniqueTAllocation.Clear();
            uniqueTAttempts.Clear();
        });
        RegisterEventHandler<EventPlayerSpawn>((ev, info) => {
            if (!uniqueTEnabled || ev.Userid is not { IsValid: true, IsBot: true } bot || bot.TeamNum != 2)
                return HookResult.Continue;
            uint controller = bot.EntityHandle.Raw, pawn = bot.PlayerPawn.Raw;
            uniqueTAttempts.Remove((controller, pawn)); // A respawn gets a fresh bounded retry budget.
            int generation = uniqueTGeneration;
            // Let CS assign its default spawn model before applying the chosen agent.
            foreach (float delay in new[] { .15f, .65f }) AddTimer(delay, () => {
                if (generation != uniqueTGeneration || !bot.IsValid || bot.EntityHandle.Raw != controller
                    || bot.PlayerPawn.Raw != pawn || bot.TeamNum != 2) return;
                ApplyUniqueTAgents();
            });
            return HookResult.Continue;
        });
        AddCommand("css_mccs_fill_t_unique", "Fill local T capacity, cycling seven simple F8 agent skins", (player, info) => {
            if (!LocalUniqueTSession()) { info.ReplyToCommand("[MC] 仅限当前离线单人桥接对局。"); return; }
            if (!uniqueTPrecached) { info.ReplyToCommand("[MC] 新探员资源尚未预载；请下次从项目启动器正常启动游戏后使用。"); return; }
            int? count = null;
            if (info.ArgCount > 1) {
                if (!int.TryParse(info.GetArg(1), out int requested) || requested < 0 || requested > 64) {
                    info.ReplyToCommand("[MC] 用法：css_mccs_fill_t_unique [0-64 数量]"); return;
                }
                count = requested;
            }
            // End the current dance while replacing the model/skeleton instances.
            StopDance();
            uniqueTEnabled = true;
            uniqueTAttempts.Clear();
            int generation = ++uniqueTGeneration;
            FillLocalTTeam(count);
            ApplyUniqueTAgents();
            // Quota population is asynchronous. Bound the initial sweep; respawns use the event above.
            for (int i = 1; i <= 10; i++) AddTimer(i * .5f, () => {
                if (generation == uniqueTGeneration) ApplyUniqueTAgents();
            });
            AddTimer(5.5f, () => { if (generation == uniqueTGeneration) ReportUniqueTAgents(); });
            info.ReplyToCommand(count.HasValue ? $"[MC] 正在设置 {count.Value} 名 T（不超过地图容量），循环使用 7 款 F8 探员。" : "[MC] 正在按地图上限补满 T；循环使用 7 款默认骨骼探员，支持 F8，复活后保留。数秒后输出结果。");
        });
        AddCommand("css_mccs_unique_t_status", "Report assigned local T agent skins", (player, info) => {
            if (LocalUniqueTSession()) ReportUniqueTAgents();
        });
    }

    bool LocalUniqueTSession()
    {
        if (!SupportedMap(Server.MapName)) return false;
        var humans = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot
            && p.Connected.ToString() == "PlayerConnected").ToArray();
        if (humans.Length != 1) return false;
        string? address = humans[0].IpAddress;
        return string.IsNullOrEmpty(address) || address.StartsWith("127.")
            || address.StartsWith("loopback") || address == "::1";
    }

    CCSPlayerController[] UniqueTBots() => Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot
        && p.TeamNum == 2 && p.Connected.ToString() == "PlayerConnected"
        && !mobProxies.ContainsKey(p.Index)).OrderBy(p => p.Index).ToArray();

    static string AgentModel(CCSPlayerPawn pawn) =>
        pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName ?? "";

    void ApplyUniqueTAgents()
    {
        if (!uniqueTEnabled || !uniqueTPrecached || !LocalUniqueTSession()) return;
        var bots = UniqueTBots();
        if (!uniqueTAllocation.Reconcile(bots.Select(p => p.EntityHandle.Raw))) {
            Log("Simple T skins: controller capacity exceeded; assignments unchanged."); return;
        }
        var current = bots.Select(p => (p.EntityHandle.Raw, p.PlayerPawn.Raw)).ToHashSet();
        foreach (var stale in uniqueTAttempts.Keys.Where(k => !current.Contains(k)).ToArray()) uniqueTAttempts.Remove(stale);
        foreach (var bot in bots) {
            if (!bot.PawnIsAlive || bot.PlayerPawn.Value is not { IsValid: true } pawn) continue;
            var agent = SimpleDanceAgents[uniqueTAllocation.Assignments[bot.EntityHandle.Raw] % SimpleDanceAgents.Length];
            if (AgentModel(pawn).Equals(agent.Model, StringComparison.OrdinalIgnoreCase)) continue;
            var key = (bot.EntityHandle.Raw, bot.PlayerPawn.Raw);
            int attempts = uniqueTAttempts.GetValueOrDefault(key);
            if (attempts >= 3) continue;
            uniqueTAttempts[key] = attempts + 1;
            try {
                pawn.SetModel(agent.Model);
                if (childActors.TryGetValue(bot.Index, out var child) && child.Controller == bot.EntityHandle.Raw
                    && child.Pawn == bot.PlayerPawn.Raw) ApplyChildScale(child, pawn, true);
                Log($"Unique T agent bot={bot.Index} name={agent.Name} model={agent.Model}");
            } catch (Exception e) { Log($"Unique T skin failed bot={bot.Index}: {e.Message}"); }
        }
    }

    void ReportUniqueTAgents()
    {
        var rows = UniqueTBots().Select(bot => {
            bool assigned = uniqueTAllocation.Assignments.TryGetValue(bot.EntityHandle.Raw, out int index);
            string actual = bot.PlayerPawn.Value is { IsValid: true } pawn ? AgentModel(pawn) : "";
            string expected = assigned ? SimpleDanceAgents[index % SimpleDanceAgents.Length].Model : "";
            return new { bot = bot.Index, name = assigned ? SimpleDanceAgents[index % SimpleDanceAgents.Length].Name : "未分配",
                alive = bot.PawnIsAlive, expected, actual,
                applied = assigned && bot.PawnIsAlive && actual.Equals(expected, StringComparison.OrdinalIgnoreCase) };
        }).ToArray();
        int applied = rows.Count(r => r.applied);
        Server.PrintToConsole($"[MC] T 机器人 {rows.Length} 名；当前存活且皮肤已应用 {applied} 名；可选皮肤 {SimpleDanceAgents.Length} 款。");
        WriteDiagnostic("unique-t-agents.json", JsonSerializer.Serialize(new {
            time = DateTimeOffset.Now, enabled = uniqueTEnabled, precached = uniqueTPrecached,
            catalog = SimpleDanceAgents.Length, selectionMode = "repeat_simple_f8_pool", applied, uniqueModels = rows.Where(r => r.applied).Select(r => r.actual).Distinct().Count(), rows
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
