using System.Numerics;

namespace MinecraftBridge;

/// <summary>Pure building-movement geometry. CS axes: X/Y horizontal, Z vertical.</summary>
internal static class BuildingMotionMath
{
    public const int MaxSupportQueries = 4096;

    /// <summary>
    /// Clip one horizontal movement using Minecraft's X, Y, then diagonal reduction order.
    /// The callback receives a displacement relative to the unchanged starting body position.
    /// Supply step=2 for 0.05 MC blocks at 40 Source units/block.
    /// This is an edge guard, not a swept collision test: the caller owns grounding,
    /// jump/airborne eligibility, support geometry and vertical movement.
    /// Invalid inputs or an exhausted work budget stop horizontal motion.
    /// </summary>
    public static Vector2 ClipSneak(Vector2 delta, float step, Func<Vector2, bool> hasSupport)
    {
        ArgumentNullException.ThrowIfNull(hasSupport);
        if (!Finite(delta.X) || !Finite(delta.Y) || !Finite(step) || step <= 0) return Vector2.Zero;
        double x = delta.X, y = delta.Y;
        int queries = 0;

        bool Supported(double dx, double dy) => hasSupport(new Vector2((float)dx, (float)dy));
        static double Reduce(double value, double amount) => Math.Abs(value) <= amount ? 0 : value - Math.CopySign(amount, value);

        while (x != 0)
        {
            if (queries++ >= MaxSupportQueries) return Vector2.Zero;
            if (Supported(x, 0)) break;
            double next = Reduce(x, step);
            if (next == x) return Vector2.Zero;
            x = next;
        }
        while (y != 0)
        {
            if (queries++ >= MaxSupportQueries) return Vector2.Zero;
            if (Supported(0, y)) break;
            double next = Reduce(y, step);
            if (next == y) return Vector2.Zero;
            y = next;
        }
        while (x != 0 && y != 0)
        {
            if (queries++ >= MaxSupportQueries) return Vector2.Zero;
            if (Supported(x, y)) break;
            double nextX = Reduce(x, step), nextY = Reduce(y, step);
            if (nextX == x || nextY == y) return Vector2.Zero;
            x = nextX; y = nextY;
        }
        return new Vector2((float)x, (float)y);
    }

    /// <summary>Strict positive-volume AABB overlap; touching faces/edges do not intersect.</summary>
    public static bool IntersectsStrict(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax, float epsilon = 0)
    {
        if (!ValidBox(aMin, aMax) || !ValidBox(bMin, bMax) || !Finite(epsilon) || epsilon < 0) return false;
        return Math.Min((double)aMax.X, bMax.X) - Math.Max((double)aMin.X, bMin.X) > epsilon
            && Math.Min((double)aMax.Y, bMax.Y) - Math.Max((double)aMin.Y, bMin.Y) > epsilon
            && Math.Min((double)aMax.Z, bMax.Z) - Math.Max((double)aMin.Z, bMin.Z) > epsilon;
    }

    /// <summary>
    /// Suggest an upward correction for a newly inserted obstacle immediately under the feet.
    /// The body's horizontal centre must be strictly inside the obstacle, excluding side nudges.
    /// maxLift includes separation; all coordinates and the returned lift use Source units.
    /// The caller MUST test the complete lifted body/head space against all other geometry
    /// before applying this correction. This helper does not authorize placement or teleport.
    /// </summary>
    public static bool TryUnderfootLift(Vector3 bodyMin, Vector3 bodyMax, Vector3 obstacleMin,
        Vector3 obstacleMax, float maxLift, out float lift, float separation = .03125f)
    {
        lift = 0;
        if (!Finite(maxLift) || maxLift <= 0 || !Finite(separation) || separation < 0
            || !IntersectsStrict(bodyMin, bodyMax, obstacleMin, obstacleMax)) return false;
        double centreX = ((double)bodyMin.X + bodyMax.X) / 2;
        double centreY = ((double)bodyMin.Y + bodyMax.Y) / 2;
        if (centreX <= obstacleMin.X || centreX >= obstacleMax.X
            || centreY <= obstacleMin.Y || centreY >= obstacleMax.Y) return false;
        double amount = (double)obstacleMax.Z - bodyMin.Z + separation;
        if (amount <= 0 || amount > maxLift || amount > float.MaxValue) return false;
        lift = (float)amount;
        return true;
    }

    private static bool ValidBox(Vector3 min, Vector3 max) => Finite(min.X) && Finite(min.Y) && Finite(min.Z)
        && Finite(max.X) && Finite(max.Y) && Finite(max.Z) && min.X < max.X && min.Y < max.Y && min.Z < max.Z;
    private static bool Finite(float value) => float.IsFinite(value);
}
