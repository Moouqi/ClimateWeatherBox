using System.Collections.Generic;
using HarmonyLib;

namespace ClimateWeather;

[HarmonyPatch(typeof(TornadoEffect), nameof(TornadoEffect.tornadoActionForce))]
internal static class HorizontalStormForce
{
    private const int Radius = 10;
    private static readonly HashSet<MapChunk> Chunks = new();
    private static readonly HashSet<Actor> Seen = new();

    private static bool Prefix(WorldTile pTile)
    {
        if (pTile == null || ClimateSystem.Active?.HorizontalWrap != true ||
            MapBox.width <= 0 || MapBox.height <= 0) return true;
        if (pTile.x - Radius >= 0 && pTile.x + Radius < MapBox.width) return true;

        using var actors = new ListPool<Actor>();
        Chunks.Clear(); Seen.Clear();
        try
        {
            // 只检查固定 21×21 范围涉及的区块，不扫描全世界单位，亦不改全局 Finder。
            for (int oy = -Radius; oy <= Radius; oy++)
            for (int ox = -Radius; ox <= Radius; ox++)
            {
                int x = pTile.x + ox, y = pTile.y + oy;
                if (!HorizontalTopology.NormalizeCell(ref x, y, MapBox.width, MapBox.height, true)) continue;
                MapChunk chunk = World.world.GetTileSimple(x, y)?.chunk;
                if (chunk == null || !Chunks.Add(chunk)) continue;
                foreach (Actor actor in chunk.objects.units_all)
                {
                    if (actor == null || !actor.isAlive() || actor.current_tile == null || !Seen.Add(actor)) continue;
                    if (HorizontalTopology.WithinWrappedRadius(pTile.x, pTile.y,
                        actor.current_tile.x, actor.current_tile.y, MapBox.width, Radius))
                        actors.Add(actor);
                }
            }
        }
        finally { Chunks.Clear(); Seen.Clear(); }

        // 收集结束后再施加效果，避免回调或单位换区块破坏枚举；每个单位只处理一次。
        foreach (Actor actor in actors)
        {
            WorldTile tile = actor.current_tile;
            if (!actor.isAlive() || tile == null) continue;
            if (!HorizontalTopology.WithinWrappedRadius(pTile.x, pTile.y,
                tile.x, tile.y, MapBox.width, Radius)) continue;
            actor.makeStunned(4f);
            // 原版吸力以风暴中心为 start。使用离单位最近的中心镜像，不改变实际单位坐标。
            float centerX = tile.x + HorizontalTopology.Delta(tile.x, pTile.x, MapBox.width);
            actor.calculateForce(centerX, pTile.y, tile.x, tile.y, 3f, 0f,
                pCheckCancelJobOnLand: true);
            HorizontalForcedMovement.Track(actor);
        }
        return false;
    }
}
