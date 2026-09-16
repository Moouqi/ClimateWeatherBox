using System;
using System.Collections.Generic;

namespace ClimateWeather;

// 独立于游戏对象，便于用暴力最短路验证；调用方负责按单位能力判断可通行性。
internal static class HorizontalGroundPathfinder
{
    internal enum Result { Found, Unreachable, BudgetExceeded, Invalid }
    private readonly struct Entry
    {
        internal readonly int Pixel, Cost, Score, Sequence;
        internal Entry(int pixel, int cost, int score, int sequence)
        { Pixel = pixel; Cost = cost; Score = score; Sequence = sequence; }
    }
    private sealed class Order : IComparer<Entry>
    {
        public int Compare(Entry a, Entry b)
        {
            int value = a.Score.CompareTo(b.Score);
            if (value != 0) return value;
            // 同估价优先更接近终点的节点，避免开阔水面铺满矩形后耗尽预算。
            value = b.Cost.CompareTo(a.Cost);
            return value != 0 ? value : a.Sequence.CompareTo(b.Sequence);
        }
    }
    private static readonly int[] Dx = { -1, 1, 0, 0 };
    private static readonly int[] Dy = { 0, 0, -1, 1 };

    // 长航路保留开放表和父节点；每次只展开一小段，不把未完成误报为不可达。
    internal sealed class Session
    {
        private readonly int width, height, start, goal;
        private readonly bool wrap;
        private readonly Dictionary<int, int> costs = new();
        private readonly Dictionary<int, int> parents = new();
        private readonly SortedSet<Entry> open = new(new Order());
        private int sequence;
        internal int Expanded { get; private set; }
        internal Session(int w, int h, int from, int to, bool periodic = true)
        {
            width = w; height = h; start = from; goal = to; wrap = periodic;
            costs[start] = 0; open.Add(new Entry(start, 0, Estimate(start), sequence++));
        }
        private int Estimate(int p)
        {
            int dx = Math.Abs(p % width - goal % width);
            return (wrap ? Math.Min(dx, width - dx) : dx) + Math.Abs(p / width - goal / width);
        }
        internal Result Advance(Func<int, bool> water, int budget, List<int> path)
        {
            path.Clear();
            if (budget <= 0) return Result.BudgetExceeded;
            if (!water(start) || !water(goal)) return Result.Unreachable;
            int work = 0;
            while (open.Count > 0 && work < budget)
            {
                Entry current = open.Min; open.Remove(current); work++;
                if (costs[current.Pixel] != current.Cost) continue;
                if (current.Pixel == goal)
                {
                    int p = goal; path.Add(p);
                    while (p != start) { p = parents[p]; path.Add(p); }
                    path.Reverse();
                    // 搜索期间地形变化时拒绝旧路线，不能沿失效父链穿过陆地。
                    foreach (int cell in path) if (!water(cell)) { path.Clear(); return Result.Unreachable; }
                    return Result.Found;
                }
                Expanded++;
                for (int i = 0; i < 4; i++)
                {
                    int x = current.Pixel % width + Dx[i], y = current.Pixel / width + Dy[i];
                    if (!HorizontalTopology.NormalizeCell(ref x, y, width, height, wrap)) continue;
                    int next = y * width + x, cost = current.Cost + 1;
                    if (costs.TryGetValue(next, out int old) && old <= cost) continue;
                    if (!water(next)) continue;
                    costs[next] = cost; parents[next] = current.Pixel;
                    open.Add(new Entry(next, cost, cost + Estimate(next), sequence++));
                }
            }
            return open.Count == 0 ? Result.Unreachable : Result.BudgetExceeded;
        }
    }

    internal static Result Find(int width, int height, int start, int goal, bool wrap,
        Func<int, bool> walkable, int budget, List<int> path, out int expanded)
    {
        path.Clear(); expanded = 0;
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue ||
            start < 0 || goal < 0 || start >= (long)width * height || goal >= (long)width * height ||
            walkable == null || budget <= 0) return Result.Invalid;
        if (start == goal) { path.Add(start); return Result.Found; }
        if (!walkable(goal)) return Result.Unreachable;
        // 字典只保存搜索区域，不按整张地图分配数组；预算约束单次搜索的 CPU 与内存。
        var costs = new Dictionary<int, int> { [start] = 0 };
        var parents = new Dictionary<int, int>();
        var open = new SortedSet<Entry>(new Order());
        int sequence = 0;
        open.Add(new Entry(start, 0, Heuristic(start), sequence++));
        while (open.Count > 0)
        {
            Entry current = open.Min;
            open.Remove(current);
            if (costs[current.Pixel] != current.Cost) continue;
            if (current.Pixel == goal)
            {
                int pixel = goal;
                path.Add(pixel);
                while (pixel != start) { pixel = parents[pixel]; path.Add(pixel); }
                path.Reverse();
                return Result.Found;
            }
            if (expanded >= budget) return Result.BudgetExceeded;
            expanded++;
            for (int i = 0; i < 4; i++)
            {
                int x = current.Pixel % width + Dx[i], y = current.Pixel / width + Dy[i];
                if (!HorizontalTopology.NormalizeCell(ref x, y, width, height, wrap)) continue;
                int next = y * width + x;
                if (next == current.Pixel) continue;
                int cost = current.Cost + 1;
                if (costs.TryGetValue(next, out int previous) && previous <= cost) continue;
                if (!walkable(next)) continue;
                costs[next] = cost;
                parents[next] = current.Pixel;
                open.Add(new Entry(next, cost, cost + Heuristic(next), sequence++));
            }
        }
        return Result.Unreachable;

        int Heuristic(int pixel)
        {
            int dx = Math.Abs(pixel % width - goal % width);
            if (wrap) dx = Math.Min(dx, width - dx);
            return dx + Math.Abs(pixel / width - goal / width);
        }
    }
}
