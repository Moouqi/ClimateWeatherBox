using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ai.behaviours;

namespace ClimateWeather;

[HarmonyPatch(typeof(BehGoToTileTarget), nameof(BehGoToTileTarget.execute))]
internal static class BoatIncrementalRouting
{
    private sealed class Job
    {
        internal object Data, World;
        internal WorldTile Start, Target;
        internal int Width, Height, LastFrame = -1;
        internal float LastSeen;
        internal BoatChannelSearch Search;
        internal HorizontalGroundPathfinder.Result Result = HorizontalGroundPathfinder.Result.BudgetExceeded;
        internal readonly List<int> Path = new();
    }
    private static readonly Dictionary<Actor, Job> Jobs = new();
    private static readonly List<Actor> Stale = new();
    private static readonly Queue<KeyValuePair<Actor, Job>> Rotation = new();
    private static int frame = -1, slices;
    private static bool Valid(Actor actor, Job j) => actor != null && actor.isAlive() &&
        ReferenceEquals(actor.data, j.Data) && ReferenceEquals(World.world?.tiles_list, j.World) &&
        MapBox.width == j.Width && MapBox.height == j.Height && actor.current_tile == j.Start &&
        actor.beh_tile_target == j.Target && !actor.under_forces && !actor.is_moving &&
        Time.realtimeSinceStartup - j.LastSeen < 30f;

    private static bool Prefix(BehGoToTileTarget __instance, Actor pActor, ref BehResult __result)
    {
        if (ClimateSystem.Active?.HorizontalWrap != true) { Jobs.Clear(); Rotation.Clear(); return true; }
        if (frame != Time.frameCount)
        {
            frame = Time.frameCount; slices = 0; Stale.Clear();
            foreach (var pair in Jobs) if (!Valid(pair.Key, pair.Value)) Stale.Add(pair.Key);
            foreach (Actor actor in Stale) Jobs.Remove(actor);
            // 独立于行为调用顺序轮转：先被游戏更新的船不能每帧抢走所有时间片。
            int visits = Rotation.Count;
            while (visits-- > 0 && slices < 4)
            {
                var item = Rotation.Dequeue();
                if (!Jobs.TryGetValue(item.Key, out Job queued) || !ReferenceEquals(queued, item.Value)) continue;
                Rotation.Enqueue(item);
                if (queued.Result != HorizontalGroundPathfinder.Result.BudgetExceeded) continue;
                slices++;
                queued.Result = queued.Search.Advance(p => HorizontalBoatMovement.Passable(
                    World.world.GetTileSimple(p % queued.Width, p / queued.Width)), 512, queued.Path);
            }
        }
        WorldTile target = pActor?.beh_tile_target;
        if (!HorizontalBoatMovement.Eligible(pActor) || pActor.is_moving || target == null ||
            __instance.walk_on_blocks || __instance.limit_pathfinding_regions != 0 ||
            !DebugConfig.isOn(DebugOption.SystemUnitPathfinding) ||
            !HorizontalBoatMovement.Passable(pActor.current_tile) || !HorizontalBoatMovement.Passable(target))
        { if (pActor != null) Jobs.Remove(pActor); return true; }
        if (!Jobs.TryGetValue(pActor, out Job job))
        {
            if (HorizontalWorkTargets.Distance(pActor.current_tile, target) < 256 ||
                !HorizontalOceanConnectivity.Connected(pActor.current_tile, target)) return true;
            // 扩大保留进度的容量但不增加每帧工作量，长短任务共享轮转队列。
            __result = BehResult.RepeatStep;
            if (Jobs.Count >= 32) return false;
            job = new Job {Data=pActor.data, World=World.world.tiles_list, Start=pActor.current_tile,
                Target=target, Width=MapBox.width, Height=MapBox.height, LastSeen=Time.realtimeSinceStartup};
            int entry = -1, opposite = -1;
            if (Math.Abs(job.Start.x - target.x) > job.Width / 2)
            {
                var seams = HorizontalOceanConnectivity.PatrolSeams;
                int best = int.MaxValue, count = Math.Min(32, seams.Count);
                for (int i = 0; i < count; i++)
                {
                    int y = seams[i * seams.Count / count];
                    int x = job.Start.x < target.x ? 0 : job.Width - 1;
                    WorldTile a = World.world.GetTileSimple(x, y);
                    WorldTile b = World.world.GetTileSimple(job.Width - 1 - x, y);
                    if (!HorizontalBoatMovement.Passable(a) || !HorizontalBoatMovement.Passable(b) ||
                        !HorizontalOceanConnectivity.Connected(job.Start, a) ||
                        !HorizontalOceanConnectivity.Connected(b, target)) continue;
                    int score = Math.Abs(job.Start.y - y) + Math.Abs(target.y - y);
                    if (score >= best) continue;
                    best = score; entry = y * job.Width + x; opposite = y * job.Width + job.Width - 1 - x;
                }
            }
            job.Search = new BoatChannelSearch(job.Width, job.Height,
                job.Start.y * job.Width + job.Start.x, target.y * job.Width + target.x, entry, opposite);
            // 等待搜索时清掉旧路线，不能让旧路线在下一帧启动并移动搜索起点。
            pActor.clearOldPath();
            Jobs.Add(pActor, job);
            Rotation.Enqueue(new KeyValuePair<Actor, Job>(pActor, job));
        }
        if (!Valid(pActor, job)) { Jobs.Remove(pActor); return true; }
        job.LastSeen = Time.realtimeSinceStartup;
        __result = BehResult.RepeatStep;
        var result = job.Result;
        // 每帧预算限制工作量，不限制航线可达范围。正在执行的行为持续保留搜索，
        // 只有目标改变、单位失效或行为不再被调用时才回收；不能在大洋绕路中途重置。
        if (result == HorizontalGroundPathfinder.Result.BudgetExceeded) return false;
        Jobs.Remove(pActor);
        if (result == HorizontalGroundPathfinder.Result.Found)
        {
            HorizontalGroundMovement.ApplyRoute(pActor, target, job.Path);
            __result = BehResult.Continue;
        }
        else
        {
            BoatTransportDiagnostics.RouteFailure(pActor, target, "Incremental" + result, job.Search.Expanded);
            __result = BehResult.Stop;
        }
        return false;
    }
}
