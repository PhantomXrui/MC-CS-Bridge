using System.Numerics;
using MinecraftBridge;

int assertions = 0;
void Check(bool value, string name)
{
    assertions++;
    if (!value) throw new InvalidOperationException("FAIL: " + name);
}
void Near(Vector2 actual, Vector2 expected, string name) => Check(Vector2.Distance(actual, expected) < .0001f,
    $"{name}: expected {expected}, got {actual}");
Vector2 Clip(float x, float y, Func<Vector2, bool> support, float step = 2)
    => BuildingMotionMath.ClipSneak(new(x, y), step, support);

Near(Clip(7, 5, _ => true), new(7, 5), "supported plane leaves motion alone");
Near(Clip(7, 5, p => p.X <= 3), new(3, 5), "straight edge preserves tangent motion");
Near(Clip(-7, 5, p => p.X >= -3), new(-3, 5), "negative straight edge");
Near(Clip(7, -5, p => p.Y >= -1), new(7, -1), "second horizontal axis edge");
Near(Clip(7, 7, p => p.X <= 3 && p.Y <= 3), new(3, 3), "orthogonal corner");
Near(Clip(5, 5, p => p.X + p.Y <= 6), new(3, 3), "diagonal edge after individually supported axes");
Near(Clip(5, 1, p => p.X + p.Y <= 5), new(3, 0), "diagonal reduction can finish on one axis");
Near(Clip(3, 9, p => p.X <= 0), new(0, 9), "along-edge sliding survives blocked axis");
Near(Clip(5, 7, _ => false), Vector2.Zero, "no support anywhere stops horizontal motion");
Near(Clip(.5f, 0, p => p == Vector2.Zero), Vector2.Zero, "substep motion does not reverse sign");
Near(Clip(3, -9, _ => false, 20), Vector2.Zero, "step larger than displacement");
Near(Clip(103, 21, p => p.X <= 63 && p.Y <= 21, 20), new(63, 21), "large step preserves supported axis");
Near(Clip(100, 80, p => p.X <= 60 && p.Y <= 60, 20), new(60, 60), "large displacement and step");

int calls = 0;
bool CountSupport(Vector2 _) { calls++; return false; }
Near(BuildingMotionMath.ClipSneak(Vector2.Zero, 2, CountSupport), Vector2.Zero, "zero input");
Check(calls == 0, "zero input does not probe support");
foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
{
    Near(BuildingMotionMath.ClipSneak(new(invalid, 4), 2, CountSupport), Vector2.Zero, "nonfinite X rejected");
    Near(BuildingMotionMath.ClipSneak(new(4, invalid), 2, CountSupport), Vector2.Zero, "nonfinite Y rejected");
    Near(BuildingMotionMath.ClipSneak(new(4, 4), invalid, CountSupport), Vector2.Zero, "nonfinite step rejected");
}
Near(BuildingMotionMath.ClipSneak(new(4, 4), 0, CountSupport), Vector2.Zero, "zero step rejected");
Near(BuildingMotionMath.ClipSneak(new(4, 4), -2, CountSupport), Vector2.Zero, "negative step rejected");
Check(calls == 0, "invalid arithmetic inputs never reach callback");
Near(BuildingMotionMath.ClipSneak(new(float.MaxValue, 4), float.Epsilon, CountSupport), Vector2.Zero, "unrepresentable progress stops");
Check(calls <= 1, "no-progress detection is immediate");
calls = 0;
Near(BuildingMotionMath.ClipSneak(new(100_000, 100_000), .01f, CountSupport), Vector2.Zero, "work budget exhaustion stops");
Check(calls <= BuildingMotionMath.MaxSupportQueries, "support queries strictly bounded");

// Independent footprint-vs-platform geometry: support means positive XY overlap at the same height.
// Randomized deltas must stay inside the requested component ranges and retain actual support.
var random = new Random(40821);
for (int i = 0; i < 4000; i++)
{
    float half = .1f + (float)random.NextDouble() * 12;
    float edgeX = (float)random.NextDouble() * 15, edgeY = (float)random.NextDouble() * 15;
    var wanted = new Vector2((float)(random.NextDouble() * 100 - 50), (float)(random.NextDouble() * 100 - 50));
    bool Platform(Vector2 d) => d.X - half < edgeX && d.Y - half < edgeY && d.X + half > -30 && d.Y + half > -30;
    var got = BuildingMotionMath.ClipSneak(wanted, 2, Platform);
    Check(float.IsFinite(got.X) && float.IsFinite(got.Y), "random finite result");
    Check(got.X >= Math.Min(0, wanted.X) && got.X <= Math.Max(0, wanted.X)
        && got.Y >= Math.Min(0, wanted.Y) && got.Y <= Math.Max(0, wanted.Y), "random result never accelerates or reverses");
    Check(Platform(got), "random result remains supported by independent footprint geometry");
    Near(BuildingMotionMath.ClipSneak(got, 2, Platform), got, "random supported result is stable");
    Near(BuildingMotionMath.ClipSneak(-wanted, 2, p => Platform(-p)), -got, "random reflection symmetry");
}

var aMin = Vector3.Zero; var aMax = new Vector3(10);
Check(BuildingMotionMath.IntersectsStrict(aMin, aMax, new(9), new(20)), "strict overlapping volumes");
Check(!BuildingMotionMath.IntersectsStrict(aMin, aMax, new(10, 0, 0), new(20, 10, 10)), "touching face is not penetration");
Check(!BuildingMotionMath.IntersectsStrict(aMin, aMax, new(10, 10, 0), new(20, 20, 10)), "touching edge is not penetration");
Check(!BuildingMotionMath.IntersectsStrict(aMin, aMax, new(10), new(20)), "touching corner is not penetration");
Check(BuildingMotionMath.IntersectsStrict(aMin, aMax, new(2), new(3)), "contained volume intersects");
Check(!BuildingMotionMath.IntersectsStrict(aMin, aMax, new(9.999f, 0, 0), new(20), .01f), "overlap smaller than tolerance ignored");
Check(!BuildingMotionMath.IntersectsStrict(aMax, aMin, aMin, aMax), "reversed box rejected");
Check(!BuildingMotionMath.IntersectsStrict(aMin, aMin, aMin, aMax), "zero volume rejected");
Check(!BuildingMotionMath.IntersectsStrict(new(float.NaN), aMax, aMin, aMax), "nonfinite box rejected");
Check(!BuildingMotionMath.IntersectsStrict(aMin, aMax, aMin, aMax, -1), "negative tolerance rejected");

var bodyMin = new Vector3(-16, -16, 38); var bodyMax = new Vector3(16, 16, 110);
var blockMin = new Vector3(-20, -20, 0); var blockMax = new Vector3(20, 20, 40);
Check(BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, blockMin, blockMax, 8, out float lift), "small underfoot insertion permits correction");
Check(Math.Abs(lift - 2.03125f) < .00001f, "underfoot lift includes separation");
Check(!BuildingMotionMath.IntersectsStrict(bodyMin + new Vector3(0, 0, lift), bodyMax + new Vector3(0, 0, lift), blockMin, blockMax), "corrected body clears inserted block");
Check(!BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, blockMin, blockMax, 2, out lift) && lift == 0, "budget includes clearance");
Check(!BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, new(10, -20, 0), new(50, 20, 40), 8, out _), "side insertion must not lift player");
Check(!BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, new(0, -20, 0), blockMax, 8, out _), "centre exactly on side boundary rejected");
Check(!BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, new(-20, -20, 100), new(20, 20, 140), 8, out _), "head obstruction not an underfoot insertion");
Check(!BuildingMotionMath.TryUnderfootLift(new(-16, -16, 40), new(16, 16, 112), blockMin, blockMax, 8, out _), "already standing on surface not lifted");
Check(!BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, blockMin, blockMax, float.NaN, out _), "invalid lift limit rejected");
Check(!BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, blockMin, blockMax, 8, out _, -1), "negative separation rejected");
// The helper deliberately does not pretend to validate head clearance: the caller must reject this ceiling.
BuildingMotionMath.TryUnderfootLift(bodyMin, bodyMax, blockMin, blockMax, 8, out lift);
Check(BuildingMotionMath.IntersectsStrict(bodyMin + new Vector3(0, 0, lift), bodyMax + new Vector3(0, 0, lift), new(-20, -20, 111), new(20, 20, 120)), "caller head-space check catches low ceiling");

Console.WriteLine($"PASS: {assertions} assertions; 4000 independent platform fixtures; bounded query/invalid-input checks; AABB and underfoot/head-clearance cases.");
