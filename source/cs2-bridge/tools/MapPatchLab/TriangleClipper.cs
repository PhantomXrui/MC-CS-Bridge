using System;
using System.Collections.Generic;
using System.Numerics;

namespace MapPatchLab;

public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>Triangle-mesh subtraction only; does not modify hulls, materials, topology or resource metadata.</summary>
public static class TriangleClipper
{
    // Source-unit tolerances, not block sizes. Classification itself uses exact double signs.
    private const double DuplicateDistanceSquared = 1e-20;
    private const double MinimumTwiceArea = 1e-10;

    private readonly record struct Point(double X, double Y, double Z)
    {
        public Point(Vector3 p) : this(p.X, p.Y, p.Z) { }
        public Vector3 Float() => new((float)X, (float)Y, (float)Z);
        public double Coordinate(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
        public Point AtAxis(int axis, double value) => axis == 0 ? new(value, Y, Z) : axis == 1 ? new(X, value, Z) : new(X, Y, value);
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Point operator *(Point a, double value) => new(a.X * value, a.Y * value, a.Z * value);
    }

    /// <summary>
    /// Return the part of a triangle outside the closed AABB. Surface coplanar with a box face
    /// is removed where it lies within the other four planes. A mere edge/point contact removes
    /// no area. Each emitted triangle retains the original winding; no cap/wall faces are added.
    /// Intersections use doubles and exact plane classification, followed by float output.
    /// Consecutive points within 1e-10 Source units and triangles of area at most 5e-11 are discarded.
    /// Finite, ordered positive-volume bounds are required. A degenerate input triangle returns empty.
    /// </summary>
    public static IReadOnlyList<Triangle> SubtractBox(Triangle tri, Vector3 min, Vector3 max)
    {
        if (!Finite(min) || !Finite(max) || min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            throw new ArgumentException("Subtraction bounds must be finite and have positive volume.");
        if (!Finite(tri.A) || !Finite(tri.B) || !Finite(tri.C))
            throw new ArgumentException("Triangle vertices must be finite.");
        var normal = Normal(tri);
        if (Length(normal) <= MinimumTwiceArea) return Array.Empty<Triangle>();
        // Avoid splitting a triangle against unrelated infinite planes. Besides
        // retaining exact original bytes outside the cut, this prevents float
        // roundoff from appearing as a tiny false-positive area removal.
        if (IntersectionArea(tri, min, max) <= MinimumTwiceArea / 2) return new[] { tri };

        var result = new List<Triangle>();
        var remainder = new List<Point> { new(tri.A), new(tri.B), new(tri.C) };
        for (int axis = 0; axis < 3 && remainder.Count >= 3; axis++)
        {
            Split(remainder, axis, new Point(min).Coordinate(axis), true, out var insideLow, out var outsideLow);
            Emit(outsideLow, normal, result);
            remainder = insideLow;
            if (remainder.Count < 3) break;
            Split(remainder, axis, new Point(max).Coordinate(axis), false, out var insideHigh, out var outsideHigh);
            Emit(outsideHigh, normal, result);
            remainder = insideHigh;
        }
        // The remaining convex polygon lies inside all six planes and is intentionally discarded.
        return result;
    }

    public static double Area(Triangle triangle) => Length(Normal(triangle)) / 2;

    public static double IntersectionArea(Triangle tri, Vector3 min, Vector3 max)
    {
        var lo = Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C));
        var hi = Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C));
        if (lo.X > max.X || hi.X < min.X || lo.Y > max.Y || hi.Y < min.Y || lo.Z > max.Z || hi.Z < min.Z) return 0;
        var polygon = new List<Point> { new(tri.A), new(tri.B), new(tri.C) };
        for (var axis = 0; axis < 3 && polygon.Count >= 3; axis++)
        {
            Split(polygon, axis, new Point(min).Coordinate(axis), true, out var low, out _);
            polygon = low;
            if (polygon.Count < 3) break;
            Split(polygon, axis, new Point(max).Coordinate(axis), false, out var high, out _);
            polygon = high;
        }
        if (polygon.Count < 3) return 0;
        double area = 0;
        for (var i = 1; i + 1 < polygon.Count; i++)
        {
            var a = polygon[i] - polygon[0]; var b = polygon[i + 1] - polygon[0];
            var cross = new Point(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            area += Length(cross) / 2;
        }
        return area;
    }

    private static void Split(List<Point> polygon, int axis, double boundary, bool greater,
        out List<Point> inside, out List<Point> outside)
    {
        inside = new List<Point>(polygon.Count + 1);
        outside = new List<Point>(polygon.Count + 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            Point a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            double sa = a.Coordinate(axis) - boundary, sb = b.Coordinate(axis) - boundary;
            if (!greater) { sa = -sa; sb = -sb; }
            bool aInside = sa >= 0, bInside = sb >= 0;
            (aInside ? inside : outside).Add(a);
            if (aInside == bInside) continue;
            double t = Math.Clamp(sa / (sa - sb), 0, 1);
            // Pin the clipped coordinate to the exact plane instead of accumulating roundoff.
            Point crossing = (a + (b - a) * t).AtAxis(axis, boundary);
            inside.Add(crossing); outside.Add(crossing);
        }
        Compact(inside); Compact(outside);
    }

    private static void Compact(List<Point> polygon)
    {
        for (int i = polygon.Count - 1; i > 0; i--)
            if (Dot(polygon[i] - polygon[i - 1], polygon[i] - polygon[i - 1]) <= DuplicateDistanceSquared)
                polygon.RemoveAt(i);
        if (polygon.Count > 1 && Dot(polygon[0] - polygon[^1], polygon[0] - polygon[^1]) <= DuplicateDistanceSquared)
            polygon.RemoveAt(polygon.Count - 1);
    }

    private static void Emit(List<Point> polygon, Point normal, List<Triangle> result)
    {
        if (polygon.Count < 3) return;
        Vector3 a = polygon[0].Float();
        for (int i = 1; i + 1 < polygon.Count; i++)
        {
            var tri = new Triangle(a, polygon[i].Float(), polygon[i + 1].Float());
            var candidateNormal = Normal(tri);
            // Test after float conversion: intersections can collapse at map-coordinate precision.
            if (Length(candidateNormal) <= MinimumTwiceArea) continue;
            double facing = Dot(normal, candidateNormal);
            if (facing == 0) continue;
            if (facing < 0) tri = new Triangle(tri.A, tri.C, tri.B);
            result.Add(tri);
        }
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Point Normal(Triangle t) => Cross(new Point(t.B) - new Point(t.A), new Point(t.C) - new Point(t.A));
    private static Point Cross(Point a, Point b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Length(Point p) => Math.Sqrt(Dot(p, p));

    /// <summary>Analytic area/winding/contact checks and an independent planar point-coverage test.</summary>
    public static void RunSelfTests()
    {
        static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("TriangleClipper self-test failed: " + name);
        }
        static double Total(IReadOnlyList<Triangle> triangles)
        {
            double total = 0; foreach (var t in triangles) total += Area(t); return total;
        }
        static IReadOnlyList<Triangle> Verify(Triangle t, Vector3 min, Vector3 max, double expected, string name)
        {
            var result = SubtractBox(t, min, max);
            Require(Math.Abs(Total(result) - expected) <= Math.Max(1e-6, expected * 1e-6), name + " area");
            foreach (var part in result)
            {
                Require(Area(part) > 0, name + " nondegenerate");
                Require(Dot(Normal(t), Normal(part)) > 0, name + " winding");
                var centre = (part.A + part.B + part.C) / 3;
                Require(!(centre.X > min.X && centre.X < max.X && centre.Y > min.Y && centre.Y < max.Y
                    && centre.Z > min.Z && centre.Z < max.Z), name + " centroid outside box");
            }
            return result;
        }

        var flat = new Triangle(new(0, 0, 0), new(4, 0, 0), new(0, 4, 0));
        Verify(flat, new(5, 5, -1), new(6, 6, 1), 8, "fully outside");
        Require(Verify(flat, new(-1, -1, -1), new(5, 5, 1), 0, "fully inside").Count == 0, "inside emits no triangles");
        Verify(flat, new(0, -1, -1), new(2, 5, 1), 2, "partial cut with new intersections");
        var large = new Triangle(new(0, 0, 0), new(10, 0, 0), new(0, 10, 0));
        var hole = Verify(large, new(2, 2, -1), new(3, 3, 1), 49, "interior box creates hole");
        Verify(new Triangle(flat.C, flat.B, flat.A), new(0, -1, -1), new(2, 5, 1), 2, "reverse winding");
        Verify(new Triangle(new(0, 0, 0), new(4, 0, 4), new(0, 4, 0)), new(0, -1, -1), new(2, 5, 5), 2 * Math.Sqrt(2), "oblique plane");
        Verify(new Triangle(new(0, 0, 0), new(8, 0, 8), new(0, 8, 8)), new(1, 1, 2), new(5, 5, 7),
            20.5 * Math.Sqrt(3), "oblique cut crossing five box planes");
        Verify(flat, new(-1, -1, 0), new(5, 5, 1), 0, "coplanar closed box face");
        Verify(flat, new(-2, -1, -1), new(0, 5, 1), 8, "edge contact removes no area");
        Verify(flat, new(4, -1, -1), new(5, 1, 1), 8, "vertex contact removes no area");
        var offset = new Vector3(16384, -8192, 1024);
        Verify(new Triangle(large.A + offset, large.B + offset, large.C + offset), new Vector3(2, 2, -1) + offset,
            new Vector3(3, 3, 1) + offset, 49, "translated map coordinates");
        Require(SubtractBox(new Triangle(Vector3.Zero, Vector3.One, new Vector3(2)), new(-1), new(3)).Count == 0, "collinear input");
        Require(SubtractBox(new Triangle(Vector3.Zero, Vector3.Zero, Vector3.UnitX), new(-1), new(3)).Count == 0, "duplicate input vertices");
        bool rejected = false;
        try { SubtractBox(flat, Vector3.One, Vector3.Zero); } catch (ArgumentException) { rejected = true; }
        Require(rejected, "invalid box rejected");
        rejected = false;
        try { SubtractBox(new Triangle(new(float.NaN), flat.B, flat.C), new(-1), new(5)); } catch (ArgumentException) { rejected = true; }
        Require(rejected, "nonfinite triangle rejected");

        // Independent 2D point-in-triangle coverage: no retained gap and no overlapping output interiors.
        // Irrational-looking sample offsets avoid triangle/box boundary coincidences in this fixture.
        for (int xi = 0; xi < 80; xi++) for (int yi = 0; yi < 80; yi++)
        {
            double x = (xi + .173) / 8, y = (yi + .419) / 8;
            bool expected = x + y < 10 && !(x > 2 && x < 3 && y > 2 && y < 3);
            int covered = 0;
            foreach (var t in hole) if (ContainsXY(t, x, y)) covered++;
            Require(covered == (expected ? 1 : 0), "independent coverage at " + x + "," + y);
        }

        static bool ContainsXY(Triangle t, double x, double y)
        {
            static double Side(Vector3 a, Vector3 b, double px, double py) => ((double)b.X - a.X) * (py - a.Y) - ((double)b.Y - a.Y) * (px - a.X);
            double a = Side(t.A, t.B, x, y), b = Side(t.B, t.C, x, y), c = Side(t.C, t.A, x, y);
            return (a > 0 && b > 0 && c > 0) || (a < 0 && b < 0 && c < 0);
        }
    }
}
