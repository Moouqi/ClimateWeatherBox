using HarmonyLib;
using ai;

namespace ClimateWeather;

[HarmonyPatch(typeof(ActorTool), nameof(ActorTool.checkHomeDocks))]
internal static class HorizontalBoatHome
{
    internal static bool Prefix(Actor pActor)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || pActor?.asset?.is_boat != true) return true;
        Building home = pActor.getHomeBuilding();
        Docks docks = home?.component_docks;
        if (docks?.tiles_ocean == null) return true;
        if (!home.isUsable() || home.isAbandoned() || home.isUnderConstruction()) return true;
        // 只保留仍能通过水路抵达的既有母港，不重新登记船数，也不跳过超额清理。
        foreach (WorldTile water in docks.tiles_ocean)
            if (HorizontalOceanConnectivity.Connected(pActor.current_tile, water)) return false;
        return true;
    }

    internal static void Postfix(Actor pActor)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || pActor?.asset?.is_boat != true ||
            pActor.getHomeBuilding() != null || pActor.current_tile?.region?.island == null) return;
        // 已丢失母港的存量船可按原版规则寻找空位；不挤占满额港口或免除无港清理。
        foreach (TileIsland ocean in HorizontalOceanConnectivity.OtherConnectedOceans(pActor.current_tile.region.island))
        {
            if (ocean.docks == null) continue;
            foreach (Docks dock in ocean.docks)
            {
                Building building = dock.building;
                if (building == null || !building.isUsable() || building.isAbandoned() ||
                    building.isUnderConstruction() || !building.hasCity() ||
                    building.city.kingdom != pActor.kingdom || building.city.kingdom.isEnemy(pActor.kingdom) ||
                    dock.isFull(pActor.asset.boat_type)) continue;
                dock.addBoatToDock(pActor);
                return;
            }
        }
    }
}
