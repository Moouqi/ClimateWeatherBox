using System;
using System.Collections.Generic;

namespace ClimateWeather;

// 经接缝通道拆成两条不回绕航段，避免全局 A* 先朝大陆内部展开。
// 全段验证完成才提交给原版任务，防止把中途到达误认作装卸/贸易完成。
internal sealed class BoatChannelSearch
{
    private readonly int width, height, start, goal, exit;
    private HorizontalGroundPathfinder.Session search;
    private readonly List<int> first = new();
    private int phase;
    internal int Expanded { get; private set; }
    internal BoatChannelSearch(int w, int h, int from, int to, int entry = -1, int opposite = -1)
    {
        width = w; height = h; start = from; goal = to; exit = opposite;
        phase = entry >= 0 ? 1 : 0;
        search = new(w, h, from, entry >= 0 ? entry : to, phase == 0);
    }
    internal HorizontalGroundPathfinder.Result Advance(Func<int, bool> water, int budget, List<int> path)
    {
        int before = search.Expanded;
        var result = search.Advance(water, budget, path);
        Expanded += search.Expanded - before;
        if (phase != 0 && (result == HorizontalGroundPathfinder.Result.Unreachable ||
            result == HorizontalGroundPathfinder.Result.BudgetExceeded && search.Expanded >= 8192))
        {
            // 通道只是启发式候选，不可用或明显绕路时退回完整搜索，不能误报海域不通。
            phase = 0; first.Clear(); path.Clear();
            search = new(width, height, start, goal);
            return HorizontalGroundPathfinder.Result.BudgetExceeded;
        }
        if (result != HorizontalGroundPathfinder.Result.Found) return result;
        if (phase == 1)
        {
            first.AddRange(path); path.Clear(); phase = 2;
            search = new(width, height, exit, goal, false);
            return HorizontalGroundPathfinder.Result.BudgetExceeded;
        }
        if (phase == 2)
        {
            foreach (int p in first)
                if (!water(p))
                {
                    phase = 0; first.Clear(); path.Clear(); search = new(width, height, start, goal);
                    return HorizontalGroundPathfinder.Result.BudgetExceeded;
                }
            path.InsertRange(0, first);
        }
        return result;
    }
}
