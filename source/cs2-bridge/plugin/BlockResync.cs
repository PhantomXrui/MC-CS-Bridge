using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3 = System.Numerics.Vector3;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    sealed class BlockResyncHold
    {
        public required CCSPlayerController Player;
        public required uint Controller, Pawn;
        public required long Life, Deadline;
        public required string Map;
        public required V3 Position, Velocity;
        public required MoveType_t Move, ActualMove;
        public required bool AddedFrozen;
        public long RequestId;
    }

    const uint ResyncFrozenFlag = (uint)PlayerFlags.FL_FROZEN;
    const float ResyncExternalTeleportDistance = 128f;
    BlockResyncHold? blockResyncHold;
    long nextBlockResyncId = Environment.TickCount64 << 16;

    // Invoke BEFORE removing old native colliders. Keep sending the ordinary host
    // pose while held: Minecraft still needs the human's position and orientation.
    // False means no pawn was held; the caller can still send an ordinary blocksync.
    bool BeginBlockResync(CCSPlayerController player)
    {
        if (!connected || !mcReady || !player.IsValid || player.IsBot || !player.PawnIsAlive
            || player.PlayerPawn.Value is not { IsValid: true } pawn || pawn.AbsOrigin is not {} origin)
            return false;

        try
        {
            if (blockResyncHold is {} old && !SameBlockResyncLife(old, out _))
                ReleaseBlockResync("pawn_changed");
            var hold = blockResyncHold;
            if (hold == null)
            {
                var velocity = pawn.AbsVelocity;
                hold = new BlockResyncHold
                {
                    Player = player, Controller = player.EntityHandle.Raw, Pawn = player.PlayerPawn.Raw,
                    Life = ActorLife(player), Map = Server.MapName,
                    Position = new V3(origin.X, origin.Y, origin.Z),
                    Velocity = new V3(velocity.X, velocity.Y, velocity.Z),
                    Move = pawn.MoveType, ActualMove = pawn.ActualMoveType,
                    AddedFrozen = (pawn.Flags & ResyncFrozenFlag) == 0,
                    Deadline = Environment.TickCount64 + 3000
                };
                blockResyncHold = hold; // Save recovery data before the first mutation.
                HoldBlockResyncPawn(hold, pawn);
                // This timer backs up the per-tick deadline check. Repeated sync
                // requests retain the first deadline and can never extend a hold.
                AddTimer(3f, () =>
                {
                    if (ReferenceEquals(blockResyncHold, hold)) ReleaseBlockResync("timeout");
                });
            }
            if (Environment.TickCount64 >= hold.Deadline)
            {
                ReleaseBlockResync("timeout");
                return false;
            }

            hold.RequestId = ++nextBlockResyncId;
            long id = hold.RequestId;
            var center = WorldGeometry.ToMC(hold.Position);
            int[] cell = [(int)MathF.Floor(center.X), (int)MathF.Floor(center.Y), (int)MathF.Floor(center.Z)];
            // The caller first queues clear/resetinput and removes native entities.
            // Explicit feet coordinates avoid waiting for a later MC pose update.
            Server.NextFrame(() =>
            {
                try
                {
                    if (!ReferenceEquals(blockResyncHold, hold) || hold.RequestId != id) return;
                    if (!connected || !mcReady || !SameBlockResyncLife(hold, out _))
                    {
                        ReleaseBlockResync("request_invalidated");
                        return;
                    }
                    Send(new { t = "blocksync", id, r = 24, center = cell });
                    Log($"block resync begin id={id} pawn={hold.Pawn} life={hold.Life} center={string.Join(',', cell)} remainingMs={Math.Max(0, hold.Deadline - Environment.TickCount64)}");
                }
                catch (Exception error)
                {
                    ReleaseBlockResync("request_error");
                    Log("block resync request failed: " + error);
                }
            });
            return true;
        }
        catch (Exception error)
        {
            ReleaseBlockResync("begin_error");
            Log("block resync begin failed: " + error);
            return false;
        }
    }

    bool SameBlockResyncLife(BlockResyncHold hold, out CCSPlayerPawn? pawn)
    {
        pawn = null;
        var player = hold.Player;
        if (Server.MapName != hold.Map || !player.IsValid || player.EntityHandle.Raw != hold.Controller
            || !player.PawnIsAlive || player.PlayerPawn.Raw != hold.Pawn || ActorLife(player) != hold.Life)
            return false;
        pawn = player.PlayerPawn.Value;
        return pawn is { IsValid: true } && pawn.Health > 0;
    }

    static void HoldBlockResyncPawn(BlockResyncHold hold, CCSPlayerPawn pawn)
    {
        pawn.MoveType = MoveType_t.MOVETYPE_NONE;
        pawn.ActualMoveType = MoveType_t.MOVETYPE_NONE;
        pawn.Flags |= ResyncFrozenFlag;
        pawn.Teleport(new Vector(hold.Position.X, hold.Position.Y, hold.Position.Z), null, new Vector(0, 0, 0));
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fFlags");
    }

    // Call once per host tick, after draining control traffic but before early
    // returns for disconnection, death or unsupported sessions. No camera pause.
    void TickBlockResync(bool sessionHealthy)
    {
        if (blockResyncHold is not {} hold) return;
        try
        {
            if (!sessionHealthy || Environment.TickCount64 >= hold.Deadline)
            {
                ReleaseBlockResync(sessionHealthy ? "timeout" : "unhealthy");
                return;
            }
            if (!SameBlockResyncLife(hold, out var pawn))
            {
                ReleaseBlockResync("pawn_changed");
                return;
            }
            var at = pawn!.AbsOrigin;
            // Native prediction/physics can still move the pawn slightly after
            // colliders disappear. That is the drift this hold must correct, not
            // evidence of an external teleport. Explicit director movement must
            // call ReleaseBlockResync first; a large unsolicited move releases too.
            if (at == null || V3.DistanceSquared(new V3(at.X, at.Y, at.Z), hold.Position)
                    > ResyncExternalTeleportDistance * ResyncExternalTeleportDistance)
            {
                ReleaseBlockResync("position_changed", false);
                return;
            }
            // Reassert on every tick, as for native bot holds. MOVETYPE_NONE alone
            // did not prevent the local human from sinking through a rebuilt floor.
            HoldBlockResyncPawn(hold, pawn);
        }
        catch (Exception error)
        {
            ReleaseBlockResync("tick_error");
            Log("block resync tick failed: " + error.Message);
        }
    }

    // Returns true for resync control frames, including stale responses. A late
    // ready for an older request must never release a newer pawn/request.
    bool HandleBlockResync(JsonElement message)
    {
        if (!message.TryGetProperty("t", out var typeValue)) return false;
        string? type = typeValue.GetString();
        if (type is not ("blocksbegin" or "blocksready" or "blockscancel")) return false;
        if (!message.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out long id)
            || blockResyncHold is not {} hold || id != hold.RequestId) return true;
        if (type == "blocksready")
        {
            bool limited = message.TryGetProperty("limited", out var limitedValue)
                && limitedValue.ValueKind == JsonValueKind.True;
            ReleaseBlockResync(limited ? "ready_limited" : "ready");
        }
        else if (type == "blockscancel") ReleaseBlockResync("cancelled");
        return true;
    }

    // Always call on unload/map end and from the outer Tick exception handler.
    // Never retain a frozen flag across plugin instances. Restoring only the same
    // live pawn prevents a delayed acknowledgement from altering a new life.
    void ReleaseBlockResync(string reason, bool restorePose = true)
    {
        var hold = blockResyncHold;
        blockResyncHold = null;
        if (hold == null) return;
        try
        {
            if (SameBlockResyncLife(hold, out var pawn))
            {
                bool ownedMove = pawn!.MoveType == MoveType_t.MOVETYPE_NONE;
                bool ownedActualMove = pawn.ActualMoveType == MoveType_t.MOVETYPE_NONE;
                var current = pawn.AbsOrigin;
                bool stillNearAnchor = current != null
                    && V3.DistanceSquared(new V3(current.X, current.Y, current.Z), hold.Position)
                        <= ResyncExternalTeleportDistance * ResyncExternalTeleportDistance;
                // A ready may be drained before this tick reasserts the hold. Put
                // ordinary physics drift back on the restored support one last time.
                bool restoreOwnedPose = restorePose && stillNearAnchor;
                // Restore movement and our bit first; a failed teleport must never
                // leave a valid player frozen with no recovery state remaining.
                if (ownedMove || restoreOwnedPose) pawn.MoveType = hold.Move;
                if (ownedActualMove || restoreOwnedPose) pawn.ActualMoveType = hold.ActualMove;
                if (hold.AddedFrozen) pawn.Flags &= ~ResyncFrozenFlag;
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fFlags");
                if (restoreOwnedPose)
                    pawn.Teleport(new Vector(hold.Position.X, hold.Position.Y, hold.Position.Z), null,
                        new Vector(hold.Velocity.X, hold.Velocity.Y, hold.Velocity.Z));
            }
        }
        catch (Exception error) { Log("block resync release failed: " + error); }
        Log($"block resync end id={hold.RequestId} life={hold.Life} reason={reason}");
    }

    object BlockResyncSnapshot() => blockResyncHold is {} hold
        ? new { active = true, id = hold.RequestId, pawn = hold.Pawn, life = hold.Life,
            remainingMs = Math.Max(0, hold.Deadline - Environment.TickCount64) }
        : new { active = false, id = 0L, pawn = 0U, life = 0L, remainingMs = 0L };
}
