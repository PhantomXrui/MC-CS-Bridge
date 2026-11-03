namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin {
    const string TerrainLabMap = "de_dust2_mccs";
    // Maps that have a generated build-cache/map-physics/<map>/world.tri and can run the bridge.
    static bool SupportedMap(string name) => name is "de_dust2" or TerrainLabMap or "de_nuke" or "de_inferno" or "de_inferno_mccs";

    // This is the deliberately pre-cut experimental area, not general map destruction.
    // Edge rasterization of the original-map BVH must not fill the cutout with barriers.
    static bool MutableTerrainCell(string mapName, int x, int y, int z) =>
        mapName == TerrainLabMap && x is >= 7 and <= 8 && z is >= -63 and <= -62 && y is >= 90 and <= 93;
}
