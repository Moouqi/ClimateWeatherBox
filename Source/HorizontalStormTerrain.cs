namespace ClimateWeather;

internal static class HorizontalStormTerrain
{
    internal static bool TryApply(WorldTile center, float scale, bool tropical)
    {
        if (MapBox.width <= 0 || MapBox.height <= 0) return false;
        BrushData brush = Brush.get((int)(scale * 6f));
        bool crossesSeam = false;
        foreach (var offset in brush.pos)
        {
            int x = center.x + offset.x;
            if (x < 0 || x >= MapBox.width) { crossesSeam = true; break; }
        }
        // 内部区域仍走原版，仅在原版会裁掉破坏范围时替换地块枚举。
        if (!crossesSeam) return false;
        using var tiles = new ListPool<WorldTile>();
        // 先收集并去重再执行副作用，小地图的大笔刷也不会多次命中同一格。
        HorizontalBrush.Collect(center, brush, tiles);
        bool damage = MapAction.checkTileDamageGaiaCovenant(center, pDamage: true);
        foreach (WorldTile tile in tiles)
        {
            if (tile?.Type == null) continue;
            // 跨缝登陆范围同样遵守热带气旋不抽干海洋的约束。
            if (tropical && (tile.main_type?.ocean == true || tile.Type.ocean)) continue;
            if (tile.Type.ocean)
            {
                MapAction.removeLiquid(tile);
                if (Randy.randomChance(0.15f)) TornadoEffect.spawnBurst(tile, "rain", scale);
            }
            if (damage)
            {
                if (tile.top_type != null || tile.Type.life)
                    MapAction.decreaseTile(tile, pDamage: false);
                if (tile.Type.lava)
                {
                    LavaHelper.removeLava(tile);
                    TornadoEffect.spawnBurst(tile, "lava", scale);
                }
            }
            if (tile.hasBuilding() && tile.building.asset.can_be_damaged_by_tornado)
                tile.building.getHit(1f);
            if (tile.isTemporaryFrozen()) tile.unfreeze(10);
            if (tile.isOnFire()) tile.stopFire();
        }
        return true;
    }
}
