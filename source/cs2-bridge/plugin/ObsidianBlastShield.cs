using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using V3 = System.Numerics.Vector3;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    long c4ShieldBlockedHits;
    long lastTestBlastDiagnostic;
    readonly HashSet<CPlantedC4> testPlantedBombs = new();
    (uint proxy, bool planted, bool defused, bool targetBombed)? testBombRules;
    Dictionary<string, string>? c4TestConvars;
    string? c4TestMap;
    bool c4TestWasWarmup, c4TestObserversRegistered;
    long c4TestExplosions, c4TestHurts, c4TestNativeBlasts;
    V3? lastBombOrigin;

    // C4 disappears the instant it explodes, so remember where the live bomb is each tick.
    void TrackPlantedC4()
    {
        var bomb = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(b => b.IsValid && !b.HasExploded);
        if (bomb?.AbsOrigin is { } at) lastBombOrigin = new V3(at.X, at.Y, at.Z);
    }

    // Pre-cut Inferno destruction zones. Each MC fill range is the matching CS cut box
    // mapped with WorldGeometry.ToMC (MC x = CSx/40, MC z = -CSy/40, MC y = CSz/40 + 96)
    // and widened to whole MC blocks; blocks 98..99 are the natural ground blocks of both
    // sites (floor CS z 160..161), so the dirt surface is flush with the un-cut ground.
    //   B site: CS x 172..572   y 2566..2966  z -20..180
    //   A site: CS x 1925..2135 y 205..655    z -20..180 (hull-free patch inside the site)
    //           CS x 2179..2379 y 205..430    z -20..180 (east extension, hull-free)
    //           CS x 2179..2379 y 430..655    z -20..180 (east extension, hull-free)
    // The 2135..2179 band is skipped because straddling hulls there cannot be cut.
    // A fill covers the union of the three A zones; B stays exactly as before.
    internal const string InfernoBRange = "4 98 -75 14 99 -64";
    internal const string InfernoARange = "48 98 -17 59 99 -5";
    internal static string FillCommand(string range, string block) => "fill " + range + " " + block;

    // C4 blast -> Minecraft side. Inside a pre-cut Inferno zone the MC fill layer is
    // cleared, which uncovers the native pit (only C4 goes through this path).
    void SpawnMinecraftTnt(V3 cs)
    {
        var center = WorldGeometry.ToMC(cs);
        if (cs.X is >= 172 and <= 572 && cs.Y is >= 2566 and <= 2966)
        {
            Send(new { t = "cmd", c = FillCommand(InfernoBRange, "minecraft:air") });
            Send(new { t = "cmd", c = "summon tnt 9 101 -70 {fuse:10s}" });
            Log($"C4 B-site collapse -> MC fill cleared at {center.X:F1},{center.Y:F1},{center.Z:F1}");
            return;
        }
        if (cs.X is >= 1925 and <= 2379 && cs.Y is >= 205 and <= 655)
        {
            Send(new { t = "cmd", c = FillCommand(InfernoARange, "minecraft:air") });
            Send(new { t = "cmd", c = "summon tnt 50 101 -11 {fuse:10s}" });
            Log($"C4 A-site collapse -> MC fill cleared at {center.X:F1},{center.Y:F1},{center.Z:F1}");
            return;
        }
        var offsets = new (float dx, float dz, int fuse)[]
        {
            (0, 0, 10), (2.5f, 0, 12), (-2.5f, 0, 12), (0, 2.5f, 12),
            (0, -2.5f, 12), (1.8f, 1.8f, 14), (-1.8f, -1.8f, 14)
        };
        foreach (var (dx, dz, fuse) in offsets)
        {
            Send(new
            {
                t = "cmd",
                c = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "summon tnt {0:F2} {1:F2} {2:F2} {{fuse:{3}s}}", center.X + dx, center.Y, center.Z + dz, fuse)
            });
        }
        Log($"C4 explosion -> Minecraft TNT ring at {center.X:F1},{center.Y:F1},{center.Z:F1}");
    }

    // Testing-only director entry point. It takes CS world coordinates and calls the
    // exact SpawnMinecraftTnt path EventBombExploded uses, without creating a native
    // planted_c4 whose fuse has to be driven by the round state.
    void TestBlast(JsonElement message)
    {
        if (!testing || !message.TryGetProperty("pos", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 3) return;
        var cs = new V3(values[0].GetSingle(), values[1].GetSingle(), values[2].GetSingle());
        if (!FiniteBlastPoint(cs)) return;
        Log($"test blast requested at cs={cs.X:F1},{cs.Y:F1},{cs.Z:F1}");
        SpawnMinecraftTnt(cs);
    }

    // Call after bridge/build readiness checks, before restricting damage to the human.
    // C4 protection applies equally to the local player and native bot pawns.
    bool TryBlockC4Blast(DynamicHook hook, CEntityInstance entity, CTakeDamageInfo info)
    {
        if (!entity.IsValid || entity.DesignerName != "player" || info.Damage <= 0
            || (info.BitsDamageType & DamageTypes_t.DMG_BLAST) == 0)
            return false;

        var bomb = info.Inflictor.Value;
        if (testing && Environment.TickCount64 - lastTestBlastDiagnostic >= 250)
        {
            lastTestBlastDiagnostic = Environment.TickCount64;
            var attacker = info.Attacker.Value;
            string inflictorName = bomb?.IsValid == true ? bomb.DesignerName : "none";
            string attackerName = attacker?.IsValid == true ? attacker.DesignerName : "none";
            Log($"test native blast victim={entity.Index} damage={info.Damage} inflictor={inflictorName} attacker={attackerName} materials={blockMaterials.Count} damagePos={info.DamagePosition} reportedPos={info.ReportedPosition}");
        }
        if (bomb == null || !bomb.IsValid || bomb.DesignerName != "planted_c4")
        {
            // Some damage paths use the same bomb as the attacker. Never treat an
            // arbitrary explosion, planter, held weapon_c4, or grenade as planted C4.
            bomb = info.Attacker.Value;
            if (bomb == null || !bomb.IsValid || bomb.DesignerName != "planted_c4")
                return false;
        }
        if (testing) c4TestNativeBlasts++;
        if (blockMaterials.Count == 0) return false;

        var pawn = entity.As<CCSPlayerPawn>();
        var origin = pawn.AbsOrigin;
        var blast = bomb.AbsOrigin;
        if (origin == null || blast == null || pawn.Health <= 0) return false;

        // Match the current standing/crouching pawn's torso rather than its feet.
        float chestHeight = Math.Clamp(pawn.ViewOffset.Z * .65f, 18f, 48f);
        var from = WorldGeometry.ToMC(new V3(blast.X, blast.Y, blast.Z));
        var to = WorldGeometry.ToMC(new V3(origin.X, origin.Y, origin.Z + chestHeight));
        if (!ObsidianBlocksBlast(blockMaterials, from, to, out var block)) return false;

        float damage = info.Damage;
        info.Damage = 0;
        info.ShouldBleed = false;
        info.ShouldSpark = false;
        info.DamageFlags |= TakeDamageFlags_t.DFLAG_SUPPRESS_EFFECTS
            | TakeDamageFlags_t.DFLAG_SUPPRESS_PHYSICS_FORCE;
        hook.SetReturn(0);
        c4ShieldBlockedHits++;
        Log($"obsidian C4 shield victim={entity.Index} bomb={bomb.Index} damage={damage} block={block} hp={pawn.Health}");
        return true;
    }

    // Pure grid traversal: cost depends on ray length, not on the number of built
    // blocks. Both obsidian variants are full cubes and protect under this rule.
    internal static bool ObsidianBlocksBlast(
        IReadOnlyDictionary<(int x, int y, int z), string> materials,
        V3 from, V3 to, out (int x, int y, int z) blocker)
    {
        blocker = default;
        if (!FiniteBlastPoint(from) || !FiniteBlastPoint(to)) return false;
        var delta = to - from;
        if (delta.LengthSquared() < 1e-10f) return false;
        int x = (int)MathF.Floor(from.X), y = (int)MathF.Floor(from.Y), z = (int)MathF.Floor(from.Z);
        int sx = Math.Sign(delta.X), sy = Math.Sign(delta.Y), sz = Math.Sign(delta.Z);
        double tx = NextCellBoundary(from.X, delta.X, x, sx);
        double ty = NextCellBoundary(from.Y, delta.Y, y, sy);
        double tz = NextCellBoundary(from.Z, delta.Z, z, sz);
        double dx = sx == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(delta.X);
        double dy = sy == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(delta.Y);
        double dz = sz == 0 ? double.PositiveInfinity : 1.0 / Math.Abs(delta.Z);

        // Native C4 radius is far smaller than this bound. Corrupt/unbounded rays
        // safely keep native damage instead of stalling the server damage hook.
        for (int visited = 0; visited < 4096; visited++)
        {
            var cell = (x, y, z);
            if (materials.TryGetValue(cell, out var material)
                && (material == "minecraft:obsidian" || material == "minecraft:crying_obsidian")
                && SegmentEntersBlastCube(from, delta, cell))
            {
                blocker = cell;
                return true;
            }

            double next = Math.Min(tx, Math.Min(ty, tz));
            if (next >= 1 || double.IsInfinity(next)) return false;
            // Advance every tied axis: touching only a cube's edge/corner does
            // not create protection from a block the segment never enters.
            if (tx <= next + 1e-10) { x += sx; tx += dx; }
            if (ty <= next + 1e-10) { y += sy; ty += dy; }
            if (tz <= next + 1e-10) { z += sz; tz += dz; }
        }
        return false;
    }

    static bool FiniteBlastPoint(V3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && Math.Abs(p.X) < 1_000_000 && Math.Abs(p.Y) < 1_000_000 && Math.Abs(p.Z) < 1_000_000;

    static double NextCellBoundary(float value, float delta, int cell, int step) => step == 0
        ? double.PositiveInfinity : ((double)(step > 0 ? cell + 1 : cell) - value) / delta;

    static bool SegmentEntersBlastCube(V3 from, V3 delta, (int x, int y, int z) cell)
    {
        double enter = 0, exit = 1;
        int[] axes = { cell.x, cell.y, cell.z };
        for (int axis = 0; axis < 3; axis++)
        {
            double min = axes[axis] + 1e-6, max = axes[axis] + 1 - 1e-6;
            double p = from[axis], d = delta[axis];
            if (Math.Abs(d) < 1e-12)
            {
                if (p <= min || p >= max) return false;
                continue;
            }
            double a = (min - p) / d, b = (max - p) / d;
            if (a > b) (a, b) = (b, a);
            enter = Math.Max(enter, a);
            exit = Math.Min(exit, b);
            if (exit <= enter) return false;
        }
        return exit > enter;
    }

    // Explicit offline director test only. This creates a real native bomb;
    // damage must arrive through the normal native pre-damage hook above.
    // Prepare the test round BEFORE positioning actors or building the test wall:
    // ending warmup may respawn players and briefly rebuild bridge colliders.
    void PrepareNativeC4Test()
    {
        if (!testing) return;
        try { PrepareNativeC4TestCore(); }
        catch (Exception error) { Log("C4 test prepare failed: " + error); }
    }

    void PrepareNativeC4TestCore()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault(p => p.IsValid)?.GameRules;
        if (rules == null) { Log("C4 test prepare skipped: game rules unavailable"); return; }
        if (c4TestConvars != null)
        {
            Log($"C4 test already prepared warmup={rules.WarmupPeriod} freeze={rules.FreezePeriod} paused={rules.ServerPaused}");
            return;
        }
        var saved = new Dictionary<string, string>();
        foreach (string name in new[] { "mp_freezetime", "mp_warmup_pausetimer", "mp_ignore_round_win_conditions", "mp_warmuptime" })
        {
            var cvar = ConVar.Find(name);
            if (cvar == null) { Log("C4 test prepare skipped: missing " + name); return; }
            // The installed older API's StringValue is for string-backed values;
            // reading numeric convars through it can throw. Use the declared type.
            string value = cvar.Type switch
            {
                ConVarType.Bool => cvar.GetPrimitiveValue<bool>() ? "1" : "0",
                ConVarType.Int32 => cvar.GetPrimitiveValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ConVarType.UInt32 => cvar.GetPrimitiveValue<uint>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ConVarType.Float32 => cvar.GetPrimitiveValue<float>().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unsupported C4 test convar type {name}: {cvar.Type}")
            };
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numeric)
                || !double.IsFinite(numeric)) { Log("C4 test prepare skipped: nonnumeric " + name); return; }
            saved[name] = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        ClearTestPlantedC4();
        c4TestConvars = saved;
        c4TestMap = Server.MapName;
        c4TestWasWarmup = rules.WarmupPeriod;
        EnsureC4TestObservers();
        Server.ExecuteCommand("mp_freezetime 0");
        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        if (rules.WarmupPeriod) Server.ExecuteCommand("mp_warmup_end");
        Log("C4 test active-round preparation requested; wait for warmup=false freeze=false before positioning test actors");
        AddTimer(1f, () =>
        {
            if (!testing || c4TestConvars == null) return;
            var current = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault(p => p.IsValid)?.GameRules;
            if (current != null) Log($"C4 test round state warmup={current.WarmupPeriod} freeze={current.FreezePeriod} paused={current.ServerPaused} planted={current.BombPlanted}");
        });
    }

    void FinishNativeC4Test(bool restoreWarmup = true)
    {
        ClearTestPlantedC4();
        if (c4TestConvars is not { } saved) return;
        c4TestConvars = null;
        foreach (var entry in saved) Server.ExecuteCommand(entry.Key + " " + entry.Value);
        if (restoreWarmup && c4TestWasWarmup && Server.MapName == c4TestMap)
        {
            Server.ExecuteCommand("mp_warmup_start");
            Server.ExecuteCommand("mp_warmup_pausetimer " + saved["mp_warmup_pausetimer"]);
        }
        Log("C4 test settings restored");
        c4TestMap = null;
    }

    void EnsureC4TestObservers()
    {
        if (c4TestObserversRegistered) return;
        c4TestObserversRegistered = true;
        RegisterEventHandler<EventBombExploded>((ev, eventInfo) =>
        {
            if (testing && testPlantedBombs.Count > 0)
            {
                c4TestExplosions++;
                Log($"test native bomb_exploded site={ev.Site} actor={ev.Userid?.Index}");
            }
            if (lastBombOrigin is { } blastPoint)
            {
                SpawnMinecraftTnt(blastPoint);
                lastBombOrigin = null;
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerHurt>((ev, eventInfo) =>
        {
            if (testing && testPlantedBombs.Count > 0)
            {
                c4TestHurts++;
                Log($"test C4-window player_hurt victim={ev.Userid?.Index} bot={ev.Userid?.IsBot} weapon={ev.Weapon} damage={ev.DmgHealth} health={ev.Health}");
            }
            return HookResult.Continue;
        });
    }

    void TestPlantC4(JsonElement message)
    {
        if (!testing || !message.TryGetProperty("pos", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 3) return;
        var mc = new V3(values[0].GetSingle(), values[1].GetSingle(), values[2].GetSingle());
        if (!FiniteBlastPoint(mc)) return;
        float delay = message.TryGetProperty("delay", out var time)
            ? Math.Clamp(time.GetSingle(), 2f, 30f) : 5f;
        if (!float.IsFinite(delay)) return;
        ClearTestPlantedC4();
        if (Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").Any(b => b.IsValid && !b.HasExploded))
        {
            Log("test planted C4 skipped: an existing native bomb is present");
            return;
        }
        var rulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p => p.IsValid);
        var rules = rulesProxy?.GameRules;
        if (rulesProxy == null || rules == null)
        {
            Log("test planted C4 skipped: game rules unavailable");
            return;
        }
        if (rules.WarmupPeriod || rules.FreezePeriod || rules.ServerPaused)
        {
            Log($"test planted C4 skipped: inactive round warmup={rules.WarmupPeriod} freeze={rules.FreezePeriod} paused={rules.ServerPaused}; use test_c4_prepare then reposition and retry");
            return;
        }
        EnsureC4TestObservers();
        long explosionsBefore = c4TestExplosions, hurtsBefore = c4TestHurts;
        long blastsBefore = c4TestNativeBlasts, shieldsBefore = c4ShieldBlockedHits;
        var bomb = Utilities.CreateEntityByName<CPlantedC4>("planted_c4");
        if (bomb == null || !bomb.IsValid) return;
        testBombRules = (rulesProxy.EntityHandle.Raw, rules.BombPlanted, rules.BombDefused, rules.TargetBombed);
        testPlantedBombs.Add(bomb);
        var cs = WorldGeometry.ToCS(mc);
        bomb.Teleport(new Vector(cs.X, cs.Y, cs.Z), new QAngle(0, 0, 0), new Vector(0, 0, 0));
        // Configure ticking before DispatchSpawn, matching the real CS2 Retakes
        // planting path rather than assuming a later field edit starts thinking.
        bomb.BombSite = 0;
        bomb.BombTicking = true;
        bomb.BombDefused = false;
        bomb.HasExploded = false;
        bomb.CannotBeDefused = true;
        bomb.DispatchSpawn();
        bomb.TimerLength = delay;
        bomb.C4Blow = Server.CurrentTime + delay;
        rules.BombPlanted = true;
        rules.BombDefused = false;
        Utilities.SetStateChanged(bomb, "CPlantedC4", "m_bBombTicking");
        Utilities.SetStateChanged(bomb, "CPlantedC4", "m_flC4Blow");
        var planter = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && !p.IsBot);
        if (planter != null) new EventBombPlanted(true) { Userid = planter, Site = 0 }.FireEvent(false);
        Log($"test planted C4 id={bomb.Index} mc={mc} delay={delay} warmup={rules.WarmupPeriod} freeze={rules.FreezePeriod} think={bomb.NextThinkTick}");
        AddTimer(delay + 3f, () =>
        {
            bool exploded = bomb.IsValid && bomb.HasExploded;
            Log($"test C4 summary explodedFlag={exploded} explosionEvents={c4TestExplosions - explosionsBefore} nativeC4Hits={c4TestNativeBlasts - blastsBefore} hurtEvents={c4TestHurts - hurtsBefore} shieldHits={c4ShieldBlockedHits - shieldsBefore}");
            if (bomb.IsValid)
            {
                Log($"test planted C4 result id={bomb.Index} exploded={bomb.HasExploded} ticking={bomb.BombTicking} shieldHits={c4ShieldBlockedHits}");
                bomb.Remove();
            }
            testPlantedBombs.Remove(bomb);
            if (testPlantedBombs.Count == 0) RestoreTestBombRules();
        });
    }

    void ClearTestPlantedC4()
    {
        foreach (var bomb in testPlantedBombs) if (bomb.IsValid) bomb.Remove();
        testPlantedBombs.Clear();
        RestoreTestBombRules();
    }

    void RestoreTestBombRules()
    {
        if (testBombRules is not { } saved) return;
        testBombRules = null;
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault(p => p.IsValid && p.EntityHandle.Raw == saved.proxy);
        if (proxy?.GameRules is not { } rules) return;
        // Do not overwrite a real bomb planted while a test was in progress.
        if (Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").Any(b => b.IsValid && b.BombTicking && !b.HasExploded)) return;
        rules.BombPlanted = saved.planted;
        rules.BombDefused = saved.defused;
        rules.TargetBombed = saved.targetBombed;
    }
}
