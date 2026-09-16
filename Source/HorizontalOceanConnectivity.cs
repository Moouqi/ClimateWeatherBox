using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using tools;

namespace ClimateWeather;

// 在原版海域之上维护接缝连通组，不修改原版岛屿、邻区或运输缓存。
internal static class HorizontalOceanConnectivity
{
    private static readonly Dictionary<TileIsland, TileIsland> Parents = new();
    private static readonly Dictionary<TileIsland, long> Sizes = new();
    private static readonly List<int> SeamRows = new();
    internal static IReadOnlyList<int> PatrolSeams => Ready() ? SeamRows : System.Array.Empty<int>();
    private static object _world;
    private static int _width, _height, _builtFrame = -1;
    private static bool _dirty = true;

    internal static void Invalidate() => _dirty = true;
    private static bool Ocean(TileIsland island) => island != null && !island.removed && island.type == TileLayerType.Ocean;
    private static TileIsland Root(TileIsland island)
    {
        if (!Parents.TryGetValue(island, out TileIsland parent)) return island;
        while (parent != Parents[parent]) parent = Parents[parent];
        Parents[island] = parent;
        return parent;
    }
    private static void Union(TileIsland a, TileIsland b)
    {
        if (!Parents.ContainsKey(a)) Parents.Add(a, a);
        if (!Parents.ContainsKey(b)) Parents.Add(b, b);
        a = Root(a); b = Root(b);
        if (a != b) Parents[b] = a;
    }
    private static bool Ready()
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || World.world?.tiles_list == null || MapBox.width < 3) return false;
        if (!ReferenceEquals(_world, World.world.tiles_list) || _width != MapBox.width || _height != MapBox.height)
        {
            _world = World.world.tiles_list; _width = MapBox.width; _height = MapBox.height;
            _dirty = true; _builtFrame = -1;
            Parents.Clear(); Sizes.Clear();
        }
        MapChunkManager manager = World.world.map_chunk_manager;
        if (manager == null || manager._dirty_chunks_regions.Count != 0 || manager._dirty_chunks_links.Count != 0)
        {
            _dirty = true;
            return false;
        }
        if (!_dirty) return true;
        // 地形批量变化时不使用陈旧连通组，也不在同帧反复重建。
        if (_builtFrame == Time.frameCount) return false;
        _builtFrame = Time.frameCount;
        Parents.Clear(); Sizes.Clear(); SeamRows.Clear();
        for (int y = 0; y < _height; y++)
        {
            WorldTile left = World.world.GetTileSimple(0, y), right = World.world.GetTileSimple(_width - 1, y);
            TileIsland a = left?.region?.island, b = right?.region?.island;
            if (left?.isGoodForBoat() == true && right?.isGoodForBoat() == true && Ocean(a) && Ocean(b))
            {
                Union(a, b);
                SeamRows.Add(y);
            }
        }
        // 每个原版海域只计一次面积，多条接缝连接不能重复放大水域。
        foreach (TileIsland island in new List<TileIsland>(Parents.Keys))
        {
            TileIsland root = Root(island);
            Sizes.TryGetValue(root, out long size);
            Sizes[root] = size + island.getTileCount();
        }
        _dirty = false;
        return true;
    }
    internal static bool Connected(TileIsland a, TileIsland b) => Ocean(a) && Ocean(b) && Ready() && Root(a) == Root(b);
    internal static bool Connected(WorldTile a, WorldTile b) => a?.isGoodForBoat() == true && b?.isGoodForBoat() == true &&
        Connected(a.region?.island, b.region?.island);
    internal static bool LargeEnough(TileIsland island)
    {
        if (!Ocean(island) || !Ready()) return false;
        return Sizes.TryGetValue(Root(island), out long size) && size >= 2500;
    }

    internal static IEnumerable<TileIsland> OtherConnectedOceans(TileIsland island)
    {
        if (!Ocean(island) || !Ready()) yield break;
        TileIsland root = Root(island);
        // 沿用接缝索引，仅检查参与环绕的海域，不扫描全图。
        var members = new List<TileIsland>(Parents.Keys);
        foreach (TileIsland candidate in members)
            if (candidate != island && Ocean(candidate) && Root(candidate) == root) yield return candidate;
    }
}

[HarmonyPatch(typeof(MapChunkManager), nameof(MapChunkManager.setDirty))]
internal static class HorizontalOceanDirty
{
    private static void Postfix() => HorizontalOceanConnectivity.Invalidate();
}

[HarmonyPatch(typeof(MapChunkManager), nameof(MapChunkManager.updateDirty))]
internal static class HorizontalOceanRebuild
{
    private static void Prefix(MapChunkManager __instance, out bool __state) => __state =
        __instance._dirty_chunks_regions.Count != 0 || __instance._dirty_chunks_links.Count != 0;
    private static void Postfix(bool __state) { if (__state) HorizontalOceanConnectivity.Invalidate(); }
}

[HarmonyPatch(typeof(Docks), nameof(Docks.getOceanTileInSameOcean))]
internal static class HorizontalOceanDockTarget
{
    private static void Postfix(Docks __instance, WorldTile pTile, ref WorldTile __result)
    {
        if (__result != null || __instance.tiles_ocean == null) return;
        foreach (WorldTile tile in __instance.tiles_ocean)
            if (HorizontalOceanConnectivity.Connected(tile, pTile)) { __result = tile; return; }
    }
}

[HarmonyPatch(typeof(OceanHelper), nameof(OceanHelper.findWaterTileInRegion))]
internal static class HorizontalOceanWaterTarget
{
    private static void Postfix(MapRegion pRegion, WorldTile pBoatTile, ref WorldTile __result)
    {
        if (__result != null || ClimateSystem.Active?.HorizontalWrap != true || pRegion == null) return;
        // 沿用原版边缘候选，不扫描内陆格或伪造新的水面目标。
        foreach (WorldTile tile in pRegion.getEdgeTiles())
            if (HorizontalOceanConnectivity.Connected(tile, pBoatTile)) { __result = tile; return; }
    }
}

[HarmonyPatch(typeof(TileIsland), nameof(TileIsland.goodForDocks))]
internal static class HorizontalOceanDockSize
{
    private static void Postfix(TileIsland __instance, ref bool __result)
    {
        if (!__result) __result = HorizontalOceanConnectivity.LargeEnough(__instance);
    }
}

[HarmonyPatch(typeof(OceanHelper), nameof(OceanHelper.goodForNewDock))]
internal static class HorizontalOceanExistingDocks
{
    private static void Postfix(WorldTile pTile, ref bool __result)
    {
        if (!__result) return;
        // 同城同片海只建一个港口的原版约束，也必须按连通组检查。
        foreach (TileIsland island in OceanHelper._ocean_pools_with_docks)
            if (HorizontalOceanConnectivity.Connected(pTile.region?.island, island)) { __result = false; return; }
    }
}
