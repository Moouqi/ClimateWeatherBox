using HarmonyLib;
using UnityEngine;
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace ClimateWeather;

internal static class BoatDeathHistory
{
    internal sealed class History
    {
        internal object Data, World;
        internal readonly Step[] Steps = new Step[8];
        internal int Count;
        internal bool HasHit;
        internal AttackType Hit;
        internal float Damage, HealthBefore;
        internal int HitFrame;
        internal string HitOrigin;
    }
    internal struct Step
    {
        internal Vector2 From, To;
        internal bool Wrap, Liquid;
        internal int Frame;
    }
    private static readonly ConditionalWeakTable<Actor, History> Histories = new();
    internal static History Get(Actor actor)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || actor?.asset?.is_boat != true) return null;
        History h = Histories.GetOrCreateValue(actor);
        // 换世界或对象池复用后不能把上一艘船的记录归到新船名下。
        if (!ReferenceEquals(h.Data, actor.data) || !ReferenceEquals(h.World, World.world?.tiles_list))
        {
            h.Data = actor.data; h.World = World.world?.tiles_list; h.Count = 0; h.HasHit = false;
        }
        return h;
    }
    internal static void Report(Actor actor, AttackType cause)
    {
        History h = Get(actor);
        if (h == null) return;
        var s = new StringBuilder();
        s.Append($"[ClimateWeather BoatDeath] die boat={actor.data.id} type={actor.asset.id} cause={cause} " +
            $"position={actor.current_position} tile=({actor.current_tile?.x},{actor.current_tile?.y}) " +
            $"liquid={actor.current_tile?.Type?.liquid} wrapRoute={HorizontalGroundMovement.Has(actor)} force={actor.under_forces}");
        if (h.HasHit) s.Append($" lastHitRequest={h.Hit} requestedDamage={h.Damage} healthBefore={h.HealthBefore} hitFrame={h.HitFrame} hitOrigin={h.HitOrigin}");
        for (int i = Math.Max(0, h.Count - h.Steps.Length); i < h.Count; i++)
        {
            Step step = h.Steps[i % h.Steps.Length];
            s.Append($" | step frame={step.Frame} {step.From}->{step.To} targetLiquid={step.Liquid} wrap={step.Wrap}");
        }
        // 仅死亡时获取有限调用链，区分原版及其他补丁入口；不在移动热路径取堆栈。
        var trace = new System.Diagnostics.StackTrace(false);
        for (int i = 1; i < Math.Min(trace.FrameCount, 9); i++)
        {
            var method = trace.GetFrame(i)?.GetMethod();
            s.Append($" | caller={method?.DeclaringType?.FullName}.{method?.Name}");
        }
        Debug.Log(s.ToString());
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.moveTo))]
internal static class BoatStepHistory
{
    private static void Prefix(Actor __instance, WorldTile pTileTarget)
    {
        BoatDeathHistory.History h = BoatDeathHistory.Get(__instance);
        if (h == null || pTileTarget == null) return;
        h.Steps[h.Count++ % h.Steps.Length] = new BoatDeathHistory.Step {
            From = __instance.current_position, To = pTileTarget.posV3,
            Liquid = pTileTarget.Type.liquid, Wrap = HorizontalGroundMovement.Has(__instance), Frame = Time.frameCount
        };
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.getHit))]
internal static class BoatHitHistory
{
    private static void Prefix(Actor __instance, float pDamage, AttackType pAttackType)
    {
        BoatDeathHistory.History h = BoatDeathHistory.Get(__instance);
        if (h == null) return;
        // 这是伤害请求，不把可能被护盾或其他补丁取消的请求当成实际伤害。
        h.HasHit = true; h.Hit = pAttackType; h.Damage = pDamage;
        h.HealthBefore = __instance.getHealth(); h.HitFrame = Time.frameCount;
        h.HitOrigin = null;
        if (pAttackType == AttackType.Explosion && pDamage >= h.HealthBefore && h.HealthBefore > 0)
        {
            // 在伤害发起处抓调用链；死亡批处理的堆栈无法还原伤害来源。
            var s = new StringBuilder();
            var trace = new System.Diagnostics.StackTrace(false);
            for (int i = 1; i < Math.Min(trace.FrameCount, 10); i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                s.Append($"{method?.DeclaringType?.FullName}.{method?.Name};");
            }
            Building home = __instance.getHomeBuilding();
            s.Append($"home={home != null};count={home?.component_docks?.countBoatTypes(__instance.asset.boat_type)};");
            h.HitOrigin = s.ToString();
        }
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.die))]
internal static class BoatFinalDeathDiagnostics
{
    private static void Prefix(Actor __instance, AttackType pType)
    {
        if (__instance.isAlive()) BoatDeathHistory.Report(__instance, pType);
    }
}

// 只记录，不免疫触地、攻击或重力伤害；用日志区分真实死亡与接缝表现。
[HarmonyPatch(typeof(Actor), nameof(Actor.checkDieOnGroundBoat))]
internal static class BoatGroundDeathDiagnostics
{
    private static void Prefix(Actor __instance)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || __instance.asset?.is_boat != true ||
            !__instance.isAlive() || __instance.current_tile?.Type == null || __instance.current_tile.Type.liquid ||
            __instance.isInMagnet()) return;
        Debug.Log($"[ClimateWeather BoatDeath] ground-check boat={__instance.data.id} type={__instance.asset.id} " +
            $"position={__instance.current_position} tile=({__instance.current_tile.x},{__instance.current_tile.y}) " +
            $"wrapRoute={HorizontalGroundMovement.Has(__instance)} force={__instance.under_forces}");
    }
}

[HarmonyPatch(typeof(Actor), nameof(Actor.checkDeathOutsideMap))]
internal static class BoatBorderDeathDiagnostics
{
    private static void Prefix(Actor __instance)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true || __instance.asset?.is_boat != true ||
            !__instance.isAlive() || __instance.inMapBorder()) return;
        Debug.Log($"[ClimateWeather BoatDeath] border-check boat={__instance.data.id} position={__instance.current_position}");
    }
}

[HarmonyPatch(typeof(Boat), nameof(Boat.destroyBecauseOverfilled))]
internal static class BoatCapacityDeathDiagnostics
{
    private static void Prefix(Boat __instance)
    {
        Actor actor = __instance.actor;
        if (ClimateSystem.Active?.HorizontalWrap != true || actor == null || !actor.isAlive() ||
            !__instance.isHomeDockOverfilled()) return;
        Building home = actor.getHomeBuilding();
        Docks dock = home?.component_docks;
        Debug.Log($"[ClimateWeather BoatDeath] capacity-destroy boat={actor.data.id} type={actor.asset.id} " +
            $"homePresent={home != null} homeTile=({home?.current_tile?.x},{home?.current_tile?.y}) " +
            $"homeUsable={home?.isUsable()} homeAbandoned={home?.isAbandoned()} " +
            $"sameTypeCount={(dock == null ? -1 : dock.countBoatTypes(actor.asset.boat_type))} " +
            $"hasOceanTiles={dock?.hasOceanTiles()}");
    }
}
