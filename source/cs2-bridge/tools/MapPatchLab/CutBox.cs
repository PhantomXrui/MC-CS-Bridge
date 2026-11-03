using System.Numerics;

namespace MapPatchLab;

/// <summary>
/// A closed world-space axis-aligned cut box. A run may carry several boxes; each is
/// applied in order to the same triangle mesh, so the result is the union of the boxes.
/// Boxes must not overlap with each other: the area accounting in the cutters attributes
/// removed surface to exactly one box.
/// </summary>
public readonly record struct CutBox(Vector3 Min, Vector3 Max)
{
    public bool HasPositiveVolume => Max.X > Min.X && Max.Y > Min.Y && Max.Z > Min.Z;

    /// <summary>True when the two boxes share positive volume (touching faces are allowed).</summary>
    public bool OverlapsVolume(CutBox other) =>
        Min.X < other.Max.X && Max.X > other.Min.X &&
        Min.Y < other.Max.Y && Max.Y > other.Min.Y &&
        Min.Z < other.Max.Z && Max.Z > other.Min.Z;

    public bool OverlapsBounds(Vector3 otherMin, Vector3 otherMax) =>
        otherMin.X <= Max.X && otherMax.X >= Min.X &&
        otherMin.Y <= Max.Y && otherMax.Y >= Min.Y &&
        otherMin.Z <= Max.Z && otherMax.Z >= Min.Z;

    public string Describe() => FormattableString.Invariant(
        $"min {Min.X},{Min.Y},{Min.Z} max {Max.X},{Max.Y},{Max.Z}");

    /// <summary>Rejects overlapping/empty boxes before any resource is touched.</summary>
    public static void Validate(IReadOnlyList<CutBox> boxes)
    {
        if (boxes.Count == 0) throw new ArgumentException("at least one cut box is required");
        for (var i = 0; i < boxes.Count; i++)
        {
            if (!boxes[i].HasPositiveVolume) throw new ArgumentException("cut box " + (i + 1) + " must have max greater than min on every axis");
            for (var j = i + 1; j < boxes.Count; j++)
                if (boxes[i].OverlapsVolume(boxes[j]))
                    throw new ArgumentException($"cut boxes {i + 1} and {j + 1} overlap; pass disjoint boxes (a triangle's removed area would otherwise be counted twice)");
        }
    }
}
