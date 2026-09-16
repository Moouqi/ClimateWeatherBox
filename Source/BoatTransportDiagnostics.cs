using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ai.behaviours;
using life.taxi;

namespace ClimateWeather;

// 按阶段限频，而非逐船逐帧刷屏；输出原版决策，避免诊断改变运输任务。
[HarmonyPatch]
internal static class BoatTransportDiagnostics
{
    private static readonly Dictionary<string, float> Next = new();
    internal static void RouteFailure(Actor actor, WorldTile target, string reason, int expanded = 0)
    {
        if (actor?.asset?.is_boat != true) return;
        string key = "route:" + actor.asset.id + ":" + reason;
        if (Next.TryGetValue(key, out float next) && Time.realtimeSinceStartup < next) return;
        Next[key] = Time.realtimeSinceStartup + 10f;
        Debug.Log($"[ClimateWeather BoatRoute] boat={actor.data.id} type={actor.asset.id} reason={reason} " +
            $"from={Position(actor.current_tile)} target={Position(target)} expanded={expanded}");
    }
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BehBoatFindRequest), "execute");
        yield return AccessTools.Method(typeof(BehBoatTransportFindTilePickUp), "execute");
        yield return AccessTools.Method(typeof(BehBoatTransportDoLoading), "execute");
        yield return AccessTools.Method(typeof(BehBoatTransportFindTileUnload), "execute");
        yield return AccessTools.Method(typeof(BehBoatTransportUnloadUnits), "execute");
        yield return AccessTools.Method(typeof(BehGoToTileTarget), "execute");
        yield return AccessTools.Method(typeof(BehTaxiFindShipTile), "execute");
        yield return AccessTools.Method(typeof(BehTaxiEmbark), "execute");
        yield return AccessTools.Method(typeof(BehBoatFindTargetForTrade), "execute");
        yield return AccessTools.Method(typeof(BehBoatFindWaterTile), "execute");
        yield return AccessTools.Method(typeof(BehBoatFindTileInDock), "execute");
    }
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Actor pActor, BehResult __result, MethodBase __originalMethod)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || pActor?.asset == null) return;
        string stage = __originalMethod.DeclaringType.Name;
        if (!pActor.asset.is_boat)
        {
            // 普通单位的移动不能触发遍历请求表，仅记录接送任务中的乘客。
            if (stage == nameof(BehGoToTileTarget) && (!pActor.hasTask() || !pActor.ai.task.flag_boat_related)) return;
            string passengerKey = "passenger:" + stage + ":" + __result;
            if (Next.TryGetValue(passengerKey, out float passengerNext) && Time.realtimeSinceStartup < passengerNext) return;
            Next[passengerKey] = Time.realtimeSinceStartup + 10f;
            TaxiRequest request = TaxiManager.getRequestForActor(pActor);
            Boat assigned = request?.getBoat();
            Debug.Log($"[ClimateWeather BoatTransport] passenger={pActor.data.id} stage={stage} result={__result} " +
                $"from={Position(pActor.current_tile)} moveTarget={Position(pActor.beh_tile_target)} " +
                $"boatAt={Position(assigned?.actor?.current_tile)} request={request?.state.ToString() ?? "none"} inside={pActor.is_inside_boat}");
            return;
        }
        Boat boat = pActor.getSimpleComponent<Boat>();
        if (boat == null) return;
        string key = pActor.asset.id + ":" + stage + ":" + __result;
        if (Next.TryGetValue(key, out float next) && Time.realtimeSinceStartup < next) return;
        Next[key] = Time.realtimeSinceStartup + 10f;
        WorldTile from = pActor.current_tile, target = pActor.beh_tile_target;
        bool connected = from != null && target != null && HorizontalOceanConnectivity.Connected(from, target);
        Debug.Log($"[ClimateWeather BoatTransport] stage={stage} result={__result} boat={pActor.data.id} type={pActor.asset.id} " +
            $"from={Position(from)} moveTarget={Position(target)} destination={Position(boat.taxi_target)} " +
            $"passengers={boat.countPassengers()} request={boat.taxi_request?.state.ToString() ?? "none"} " +
            $"waiting={boat.taxi_request?.countActors() ?? 0} totalRequests={TaxiManager.list.Count} " +
            $"connectedWater={connected} wrapRoute={HorizontalGroundMovement.Has(pActor)}");
    }
    private static string Position(WorldTile t) => t == null ? "none" : $"({t.x},{t.y})";
}
