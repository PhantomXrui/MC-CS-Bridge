using System.Globalization;
using System.Numerics;
using MapPatchLab;
using SteamDatabase.ValvePak;

// Generic offline map cutter. Reads only the local map VPK; writes only build-cache.
// Usage (one or more boxes; repeat --box or repeat --min/--max pairs):
//   MapPatchLab --map de_inferno --box "172,2566,-20,572,2966,180" --box "1925,205,-20,2135,655,180" --box "2179,205,-20,2379,430,180" --box "2179,430,-20,2379,655,180" --physics-only --inplace
//   MapPatchLab --map de_inferno --min -1880,1760,-120 --max -1760,1880,-32 [--root E:\MC-CS-Bridge]
//   MapPatchLab --map de_inferno --min 172,2566,-20 --max 572,2966,180 --min 1925,205,-20 --max 2135,655,180 --physics-only --inplace
var root = @"E:\MC-CS-Bridge";
var map = "de_inferno";
var boxes = new List<CutBox>();
Vector3? pendingMin = null;
var physicsOnly = false;
var inplace = false;
var diagnose = false;
var hullsOnly = false;
var grow = false;
string? probeHull = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = args[++i]; break;
        case "--map": map = args[++i]; break;
        case "--box": boxes.Add(ParseBox(args[++i])); break;
        case "--min":
            if (pendingMin != null) throw new ArgumentException("--min must be followed by --max before the next --min");
            pendingMin = ParseVector(args[++i]); break;
        case "--max":
            if (pendingMin == null) throw new ArgumentException("--max must follow its --min");
            boxes.Add(new CutBox(pendingMin.Value, ParseVector(args[++i]))); pendingMin = null; break;
        case "--physics-only": physicsOnly = true; break;
        case "--inplace": inplace = true; break;
        case "--diagnose": diagnose = true; break;
        case "--hulls-only": hullsOnly = true; break;
        case "--grow-box": grow = true; break;
        case "--probe-hull": probeHull = args[++i]; break;
        default: throw new ArgumentException("unknown argument: " + args[i]);
    }
}

if (pendingMin != null) throw new ArgumentException("--min is missing its --max");
if (boxes.Count == 0) throw new ArgumentException("at least one --box (or --min/--max pair) is required");
// Diagnostic modes are read-only and may inspect overlapping boxes.
if (diagnose || probeHull != null) { if (boxes.Any(box => !box.HasPositiveVolume)) throw new ArgumentException("every box needs positive volume"); }
else CutBox.Validate(boxes);

MapNamespaceBuilder.OldMapName = map;
MapNamespaceBuilder.NewMapName = inplace ? map : map + "_mccs";

var source = Path.Combine(root, "games", "Counter-Strike 2", "csgo", "maps", map + ".vpk");
var output = Path.Combine(root, "build-cache", "map-patch-" + map);
Directory.CreateDirectory(output);

using var package = new Package();
package.Read(source);

Console.WriteLine("cut boxes: " + string.Join(" | ", boxes.Select(box => "[" + box.Describe() + "]")));
if (probeHull != null)
{
    var indices = probeHull.Split(',').Select(int.Parse).ToArray();
    package.ReadEntry(package.FindEntry($"maps/{map}/world_physics.vphys_c")!, out byte[] probeBytes, false);
    using var probeResource = new ValveResourceFormat.Resource();
    probeResource.Read(new MemoryStream(probeBytes), false);
    var probePhysics = probeResource.DataBlock as ValveResourceFormat.ResourceTypes.PhysAggregateData
        ?? ((ValveResourceFormat.ResourceTypes.Model)probeResource.DataBlock).GetEmbeddedPhys()
        ?? throw new InvalidDataException("No embedded physics.");
    HullProbe.Dump(probePhysics, indices[0], indices[1], indices.Length > 2 ? indices[2] : 6);
    return;
}
if (diagnose)
{
    CutDiagnostics.Run(package, map, boxes, output, hullsOnly, grow);
    return;
}
var cut = MapCutRunner.Run(package, map, boxes, output, !physicsOnly);
Console.WriteLine("cut report written: " + Path.Combine(output, "native-cut-report.json"));

var built = MapNamespaceBuilder.Build(package, output);
Console.WriteLine("map package: " + Path.Combine(output, MapNamespaceBuilder.NewMapName + ".vpk"));
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(built, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

static Vector3 ParseVector(string text)
{
    var parts = text.Split(',');
    if (parts.Length != 3) throw new ArgumentException("expected x,y,z: " + text);
    return new Vector3(
        float.Parse(parts[0], CultureInfo.InvariantCulture),
        float.Parse(parts[1], CultureInfo.InvariantCulture),
        float.Parse(parts[2], CultureInfo.InvariantCulture));
}

static CutBox ParseBox(string text)
{
    var parts = text.Split(',');
    if (parts.Length != 6) throw new ArgumentException("expected minx,miny,minz,maxx,maxy,maxz: " + text);
    var values = parts.Select(part => float.Parse(part, CultureInfo.InvariantCulture)).ToArray();
    return new CutBox(new Vector3(values[0], values[1], values[2]), new Vector3(values[3], values[4], values[5]));
}
