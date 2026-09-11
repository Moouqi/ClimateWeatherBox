using System.Collections.Generic;

namespace ClimateWeather;

// 领土专用查询，不改 TileZone.neighbours 或 MapRegion 的全局岛屿归属。
internal static class HorizontalTerritoryTopology
{
    internal static void CollectNeighbours(TileZone zone, List<TileZone> output)
    {
        output.Clear();
        if (zone == null) return;
        foreach (TileZone next in zone.neighbours)
            if (next != null && next != zone && !output.Contains(next)) output.Add(next);
        if (ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 1) return;
        // 从实际边缘地块反查区域，不假设地图宽度能被区域尺寸整除。
        foreach (WorldTile tile in zone.tiles)
        {
            if (tile == null || (tile.x != 0 && tile.x != MapBox.width - 1)) continue;
            int opposite = tile.x == 0 ? MapBox.width - 1 : 0;
            TileZone next = World.world.GetTileSimple(opposite, tile.y)?.zone;
            if (next != null && next != zone && !output.Contains(next)) output.Add(next);
        }
    }

    internal static bool CollectSeamRegions(TileZone from, TileZone to, MapRegion source,
        List<MapRegion> output)
    {
        output.Clear();
        if (from == null || to == null || from == to || source == null ||
            !source.isTypeGround() || ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 1)
            return false;
        foreach (WorldTile tile in from.tiles)
        {
            if (tile == null || tile.region != source || !Passable(tile) ||
                (tile.x != 0 && tile.x != MapBox.width - 1)) continue;
            int opposite = tile.x == 0 ? MapBox.width - 1 : 0;
            WorldTile landing = World.world.GetTileSimple(opposite, tile.y);
            // 同一行的陆地边必须真实接触，不能仅凭两个区域都有陆地跨海扩张。
            if (landing?.zone != to || !Passable(landing) || landing.region == null ||
                !landing.region.isTypeGround()) continue;
            if (!output.Contains(landing.region)) output.Add(landing.region);
        }
        return output.Count > 0;
    }

    internal static bool CanPeacefullyClaim(TileZone zone, City city)
    {
        // 原版 canBeClaimedByCity 可能允许窃取敌国土地，本阶段必须额外限定为无主地。
        return zone != null && city != null && !zone.hasCity() && zone.tiles_with_ground > 0 &&
            zone.canBeClaimedByCity(city);
    }

    internal static bool HasLandBridge(TileZone from, TileZone to)
    {
        if (from == null || to == null || from == to ||
            ClimateSystem.Active?.HorizontalWrap != true || MapBox.width <= 1) return false;
        foreach (WorldTile tile in from.tiles)
        {
            if (!Passable(tile) || (tile.x != 0 && tile.x != MapBox.width - 1)) continue;
            WorldTile landing = World.world.GetTileSimple(tile.x == 0 ? MapBox.width - 1 : 0, tile.y);
            if (landing?.zone == to && Passable(landing)) return true;
        }
        return false;
    }

    private static bool Passable(WorldTile tile) => tile?.Type != null && tile.Type.ground &&
        !tile.Type.block && !tile.Type.lava && !tile.Type.liquid;
}
