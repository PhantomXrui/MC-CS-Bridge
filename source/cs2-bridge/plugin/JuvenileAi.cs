using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3 = System.Numerics.Vector3;
using CsVector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace MinecraftBridge;

/// <summary>
/// 小 T（juvenile）的进攻 AI：
///  - 所有小 T 共用同一个目标（离群体中心最近的 CT 或 MC 敌对生物代理），并按队形偏移移动，轨迹高度一致；
///  - 只有小 T 会主动进攻；大 T 不进入这里；
///  - 接近目标后周期性造成伤害（对 MC 怪物发 mobdmg，对 CT 直接扣血）。
/// </summary>
public sealed partial class MinecraftBridgePlugin
{
    long lastJuvenileAiMs;
    readonly Dictionary<uint, long> juvenileHitAt = new();

    void TickJuvenileAi(List<CCSPlayerController> players)
    {
        long now = Environment.TickCount64;
        if (now - lastJuvenileAiMs < 100) return; // 10 Hz 足够，避免每 tick 传送到处飞
        lastJuvenileAiMs = now;
        var kids = players.Where(p => p.IsValid && p.IsBot && p.TeamNum == 2 && IsJuvenile(p)
            && p.PawnIsAlive && p.PlayerPawn.Value is { IsValid: true }).ToArray();
        if (kids.Length == 0) return;

        var targets = new List<(V3 Pos, uint Index, bool IsMob)>();
        foreach (var p in players.Where(p => p.IsValid && p.PawnIsAlive && p.TeamNum == 3
                     && p.PlayerPawn.Value?.AbsOrigin is not null))
            targets.Add((Origin(p), p.Index, false));
        foreach (var pair in mobProxies)
        {
            var m = players.FirstOrDefault(q => q.IsValid && q.Index == pair.Key && q.PawnIsAlive
                && q.PlayerPawn.Value?.AbsOrigin is not null);
            if (m != null) targets.Add((Origin(m), m.Index, true));
        }
        if (targets.Count == 0) return;

        var centroid = new V3(kids.Average(k => Origin(k).X), kids.Average(k => Origin(k).Y), kids.Average(k => Origin(k).Z));
        var target = targets.OrderBy(t => (t.Pos - centroid).LengthSquared()).First();
        for (int i = 0; i < kids.Length; i++)
        {
            var kid = kids[i];
            var o = Origin(kid);
            var to = target.Pos - o;
            float dist = to.Length();
            if (dist < 70f) { TryJuvenileHit(kid, target, now); continue; }
            var dir = to / dist;
            float step = Math.Min(dist - 60f, 20f); // 10 Hz × 20 = 200 CS 单位/秒
            var side = new V3(-dir.Y, dir.X, 0);
            float offset = (i - (kids.Length - 1) / 2f) * 28f;
            var next = o + dir * step + side * offset;
            var pawn = kid.PlayerPawn.Value!;
            float yaw = (float)(Math.Atan2(-dir.Y, dir.X) * 180.0 / Math.PI);
            pawn.Teleport(new CsVector(next.X, next.Y, next.Z), new QAngle(0, yaw, 0), new CsVector(0, 0, 0));
        }
    }

    static V3 Origin(CCSPlayerController p)
    {
        var o = p.PlayerPawn.Value!.AbsOrigin!;
        return new V3(o.X, o.Y, o.Z);
    }

    void TryJuvenileHit(CCSPlayerController kid, (V3 Pos, uint Index, bool IsMob) target, long now)
    {
        if (now - juvenileHitAt.GetValueOrDefault(target.Index) < 1000) return;
        juvenileHitAt[target.Index] = now;
        if (target.IsMob && mobProxies.TryGetValue(target.Index, out var proxy))
        {
            Send(new { t = "mobdmg", id = proxy.id, d = 4 });
            return;
        }
        var p = Utilities.GetPlayers().FirstOrDefault(q => q.IsValid && q.Index == target.Index && q.PawnIsAlive);
        var pawn = p?.PlayerPawn.Value;
        if (pawn == null || pawn.Health <= 1) return;
        pawn.Health = Math.Max(1, pawn.Health - 1);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        PlayVoice("比特_T仔抓到他们了.wav");
        if (pawn.Health <= 1) PlayVoice("比特_干得漂亮T仔.wav");
    }
}
