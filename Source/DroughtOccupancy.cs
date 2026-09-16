namespace ClimateWeather;

internal static class DroughtOccupancy
{
    internal static bool Vegetation(Building building) => building?.asset != null && !building.asset.city_building &&
        (building.asset.building_type == BuildingType.Building_Tree ||
         building.asset.building_type == BuildingType.Building_Plant ||
         building.asset.building_type == BuildingType.Building_Fruits ||
         building.asset.building_type == BuildingType.Building_Wheat);

    internal static bool Mineral(Building building) => building?.asset != null && !building.asset.city_building &&
        building.asset.building_type == BuildingType.Building_Mineral;

    internal static bool Blocks(Building building) => building != null && !Vegetation(building) && !Mineral(building);

    internal static void Terraform(WorldTile tile, TileType main, TopTileType top)
    {
        if (!Mineral(tile.building))
        {
            MapAction.terraformTile(tile, main, top, TerraformLibrary.nothing);
            return;
        }
        // 原版 terraform 即使使用 nothing 也会按群系兼容性销毁矿物。
        // 矿物占位只改地层，并保留寻路/建城和重绘通知，不临时清空地块物体引用。
        TileTypeBase previous = tile.Type;
        tile.setTileTypes(main, top);
        if (previous.can_be_farm != tile.Type.can_be_farm && !tile.zone.hasCity())
            World.world.city_zone_helper.city_place_finder.setDirty();
        if (tile.burned_stages > 0 && !tile.Type.can_be_set_on_fire) tile.removeBurn();
        World.world.resetRedrawTimer();
        MapAction.checkTileState(tile, previous);
    }
}
