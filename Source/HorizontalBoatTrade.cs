using HarmonyLib;
using ai;

namespace ClimateWeather;

[HarmonyPatch(typeof(ActorTool), nameof(ActorTool.getDockTradeTarget), new[] { typeof(TileIsland), typeof(Actor) })]
internal static class HorizontalBoatTrade
{
    private static void Postfix(TileIsland pIsland, Actor pActor, ref Docks __result)
    {
        if (__result != null || pActor == null) return;
        foreach (TileIsland ocean in HorizontalOceanConnectivity.OtherConnectedOceans(pIsland))
        {
            // 继续使用原版港口筛选，保留敌对、废弃、可用性和本港排除条件。
            Docks target = ActorTool.getDockTradeTarget(ocean.docks, pActor);
            if (target == null) continue;
            __result = target;
            return;
        }
    }
}
