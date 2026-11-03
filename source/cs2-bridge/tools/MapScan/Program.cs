using System.Numerics;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

// Read-only: print the world-space AABB of every compiled model in a directory.
// Usage: MapScan <dir> [name-filter]
if (args.Length == 0) throw new ArgumentException("directory required");
var dir = args[0];
var filter = args.Length > 1 ? args[1] : string.Empty;

Console.WriteLine("name\tmeshes\tminX\tminY\tminZ\tmaxX\tmaxY\tmaxZ");
foreach (var path in Directory.EnumerateFiles(dir, "*.vmdl_c").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
{
    var name = Path.GetFileName(path);
    if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    try
    {
        using var fs = File.OpenRead(path);
        var res = new Resource { FileName = name };
        res.Read(fs);

        if (res.DataBlock is not Model model)
        {
            Console.WriteLine($"{name}\tNOT_MODEL\t{res.DataBlock?.GetType().Name ?? "null"}");
            continue;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var meshes = 0;
        foreach (var (mesh, _, _) in model.GetEmbeddedMeshes())
        {
            meshes++;
            mesh.GetBounds();
            min = Vector3.Min(min, mesh.MinBounds);
            max = Vector3.Max(max, mesh.MaxBounds);
        }

        Console.WriteLine(meshes == 0
            ? $"{name}\t0\t-\t-\t-\t-\t-\t-"
            : $"{name}\t{meshes}\t{min.X:F1}\t{min.Y:F1}\t{min.Z:F1}\t{max.X:F1}\t{max.Y:F1}\t{max.Z:F1}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"{name}\tERROR\t{e.GetType().Name}: {e.Message}");
    }
}
