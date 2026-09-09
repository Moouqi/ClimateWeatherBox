using System;
using System.Collections.Generic;

namespace ClimateWeather;

// No Unity or game dependency: reusable by sampling, effects and future picking.
internal static class HorizontalTopology
{
    internal static bool WithinWrappedRadius(float cx, float cy, float x, float y, int width, float radius)
    {
        float dx = Delta(cx, x, width), dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    // 范围效果共用同一套归一化及去重规则，避免小地图重复伤害。
    internal static bool TryAddWrappedCell(int x, int y, int width, int height,
        HashSet<int> seen, out int wrappedX)
    {
        wrappedX = x;
        return NormalizeCell(ref wrappedX, y, width, height, true) &&
            seen.Add(y * width + wrappedX);
    }

    // 两份相隔一个世界宽度的噪声平滑混合，使接缝处数值和一阶导数连续。
    internal static float PeriodicBlend(float x, int width, out float wrapped)
    {
        wrapped = Wrap(x, width);
        float t = wrapped / width;
        return t * t * (3f - 2f * t);
    }

    internal static bool NormalizeCell(ref int x,int y,int width,int height,bool wrap)
    {
        if (width<=0 || y<0 || y>=height) return false;
        if (wrap) x=Wrap(x,width);
        return x>=0 && x<width;
    }

    // Minimum interval covering occupied columns on a cylinder, inclusive.
    internal static int CircularColumnSpan(List<int> columns,int width)
    {
        if (columns.Count==0) return 0;
        columns.Sort();
        int largestGap=columns[0]+width-columns[columns.Count-1];
        for (int i=1;i<columns.Count;i++) largestGap=Math.Max(largestGap,columns[i]-columns[i-1]);
        return width-largestGap+1;
    }
    internal static int Wrap(int x, int width)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int value = x % width;
        return value < 0 ? value + width : value;
    }

    internal static float Wrap(float x, int width)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        float value = x % width;
        if (value < 0) value += width;
        // 极小负数加宽度可能舍入成 width，原版会把它判为地图外并致死。
        return value >= width ? 0f : value;
    }

    internal static float Delta(float from, float to, int width)
    {
        float delta = Wrap(to - from, width);
        return delta > width * .5f ? delta - width : delta;
    }

    internal struct ViewSlice
    {
        // Draw [SourceStart, SourceEnd) at [DisplayStart, DisplayEnd).
        internal double SourceStart, SourceEnd, DisplayStart, DisplayEnd;
        internal double Offset => DisplayStart - SourceStart;
    }

    internal static bool SplitView(double left, double right, int width,
        List<ViewSlice> output, int maximumSlices = 8)
    {
        output.Clear();
        if (width <= 0 || maximumSlices < 1 || double.IsNaN(left) || double.IsNaN(right) ||
            double.IsInfinity(left) || double.IsInfinity(right) || right <= left) return false;
        // Bound extreme zoom-out cost before issuing any drawing work.
        double first = Math.Floor(left / width), last = Math.Ceiling(right / width) - 1;
        if (last - first + 1 > maximumSlices || Math.Abs(first) > 1e9 || Math.Abs(last) > 1e9) return false;
        for (double copy = first; copy <= last; copy++)
        {
            double offset = copy * width;
            double start = Math.Max(left, offset), end = Math.Min(right, offset + width);
            if (end <= start) continue;
            output.Add(new ViewSlice { SourceStart = start - offset, SourceEnd = end - offset,
                DisplayStart = start, DisplayEnd = end });
        }
        return true;
    }

    internal static bool TryPick(double x, double y, int width, int height, out int tileX, out int tileY)
    {
        tileX = tileY = -1;
        if (width <= 0 || height <= 0 || double.IsNaN(x) || double.IsInfinity(x) ||
            double.IsNaN(y) || y < 0 || y >= height) return false;
        double wrapped = x % width;
        if (wrapped < 0) wrapped += width;
        tileX = (int)Math.Floor(wrapped);
        tileY = (int)Math.Floor(y);
        return true;
    }
}
