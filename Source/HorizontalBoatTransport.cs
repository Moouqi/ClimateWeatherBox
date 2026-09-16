using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ai.behaviours;
using life.taxi;

namespace ClimateWeather;

internal static class HorizontalBoatTransport
{
    private sealed class Trip { internal object Data; }
    private static readonly ConditionalWeakTable<Actor, Trip> Trips = new();
    internal static void Track(Actor actor) => Trips.GetOrCreateValue(actor).Data = actor.data;
    internal static void Forget(Actor actor) => Trips.Remove(actor);
    internal static bool Tracked(Actor actor) => actor != null && Trips.TryGetValue(actor, out Trip t) && ReferenceEquals(t.Data, actor.data);

    internal static WorldTile Shore(WorldTile ocean, WorldTile destination)
    {
        if (ocean == null || destination?.region?.island == null || MapBox.width < 3) return null;
        WorldTile best = null;
        int bestDistance = 17;
        // 只允许船周围四格内的目标岛屿安全岸线，防止旧直线射线穿过整张地图卸客。
        for (int dy = -4; dy <= 4; dy++)
        for (int dx = -4; dx <= 4; dx++)
        {
            int distance = dx * dx + dy * dy;
            if (distance >= bestDistance || ocean.y + dy < 0 || ocean.y + dy >= MapBox.height) continue;
            int x = ((ocean.x + dx) % MapBox.width + MapBox.width) % MapBox.width;
            WorldTile tile = World.world.GetTileSimple(x, ocean.y + dy);
            if (!HorizontalGroundMovement.Walkable(tile) || tile.region?.island != destination.region.island) continue;
            best = tile; bestDistance = distance;
        }
        return best;
    }
}

[HarmonyPatch(typeof(TileIsland), nameof(TileIsland.reachableByCityFrom))]
internal static class HorizontalTransportCityReachability
{
    private static void Postfix(TileIsland __instance, TileIsland pIsland, ref bool __result)
    {
        if (__result || __instance == pIsland || pIsland == null || __instance.removed || pIsland.removed ||
            ClimateSystem.Active?.HorizontalWrap != true) return;
        // 不把补充结果写入原版缓存；水域改动后由连通图立即失效。
        foreach (TileIsland a in __instance.getConnectedIslands())
        foreach (TileIsland b in pIsland.getConnectedIslands())
            if (HorizontalOceanConnectivity.Connected(a, b) && a.goodForDocks()) { __result = true; return; }
    }
}

[HarmonyPatch(typeof(BehTaxiFindShipTile), nameof(BehTaxiFindShipTile.execute))]
internal static class HorizontalTransportBoardingTarget
{
    private static bool Prefix(Actor pActor, ref BehResult __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || pActor?.current_tile == null) return true;
        TaxiRequest request = TaxiManager.getRequestForActor(pActor);
        if (request == null || !request.hasAssignedBoat() || request.state != TaxiRequestState.Loading) return true;
        WorldTile ocean = request.getBoat().actor.current_tile;
        if (ocean == null) return true;
        // 旧射线会把离船几十格的同岛地块选为终点；所有接客都必须走到船旁。
        WorldTile shore = HorizontalBoatTransport.Shore(ocean, pActor.current_tile);
        __result = BehResult.Stop;
        if (shore != null) { pActor.beh_tile_target = shore; __result = BehResult.Continue; }
        return false;
    }
}

[HarmonyPatch(typeof(BehTaxiEmbark), nameof(BehTaxiEmbark.execute))]
internal static class HorizontalTransportEmbark
{
    private static void Postfix(Actor pActor, ref BehResult __result)
    {
        if (__result != BehResult.Stop || ClimateSystem.Active?.HorizontalWrap != true ||
            pActor?.current_tile == null || pActor.is_inside_boat) return;
        TaxiRequest request = TaxiManager.getRequestForActor(pActor);
        if (request == null || !request.hasAssignedBoat() || request.state != TaxiRequestState.Loading) return;
        Boat boat = request.getBoat();
        WorldTile tile = boat.actor.current_tile;
        if (tile == null || Math.Abs(tile.x - pActor.current_tile.x) <= MapBox.width * .5f ||
            HorizontalWorkTargets.Distance(tile, pActor.current_tile) >= 25) return;
        pActor.beh_tile_target = null;
        pActor.embarkInto(boat);
        __result = BehResult.Continue;
    }
}

[HarmonyPatch(typeof(BehBoatTransportUnloadUnits), nameof(BehBoatTransportUnloadUnits.execute))]
internal static class HorizontalTransportUnload
{
    private static bool Prefix(Actor pActor, ref BehResult __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || !HorizontalBoatTransport.Tracked(pActor)) return true;
        Boat boat = pActor.getSimpleComponent<Boat>();
        __result = BehResult.Stop;
        if (boat == null || boat.taxi_target == null) return false;
        WorldTile shore = HorizontalBoatTransport.Shore(pActor.current_tile, boat.taxi_target);
        // 无合法岸线时保留船与乘客，由原版后续任务重试，不能在水中强制清空乘客。
        if (shore == null) return false;
        boat.unloadPassengers(shore);
        if (boat.taxi_request != null)
        {
            TaxiManager.finish(boat.taxi_request);
            boat.taxi_request = null;
            boat.cancelWork(pActor);
        }
        __result = BehResult.Continue;
        return false;
    }
}

[HarmonyPatch(typeof(Boat), nameof(Boat.unloadPassengers))]
internal static class HorizontalTransportTripEnd
{
    private static void Postfix(Boat __instance) => HorizontalBoatTransport.Forget(__instance.actor);
}
