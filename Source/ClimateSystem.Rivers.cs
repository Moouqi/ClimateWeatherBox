using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    /// <summary>
    /// 以所有现存海洋/湖泊建立距离场并确定每条河的起终点。随后先计算八邻域、
    /// 海拔等级单调不升的 A* 基础路径，再按等高平台采样和重建，最终转换成单格宽
    /// 四邻域连续河道。
    /// </summary>
    private void PlanRiverNetwork(WorldTile[] tiles)
    {
        if (tiles == null || tiles.Length == 0 || _cellIndexByPixel.Length == 0) return;
        int width = MapBox.width;
        int height = MapBox.height;
        int total = width * height;
        int[] waterDistance = new int[total];
        for (int i = 0; i < total; i++) waterDistance[i] = int.MaxValue;
        Queue<int> frontier = new Queue<int>();

        int destinationBodies = SeedQualifiedRiverDestinations(
            tiles, waterDistance, frontier, width, height);
        if (frontier.Count == 0) return;

        int[] dx = { -1, 1, 0, 0 };
        int[] dy = { 0, 0, -1, 1 };
        while (frontier.Count > 0)
        {
            int pixel = frontier.Dequeue();
            int x = pixel % width;
            int y = pixel / width;
            int nextDistance = waterDistance[pixel] + 1;
            for (int direction = 0; direction < 4; direction++)
            {
                int nx = x + dx[direction];
                int ny = y + dy[direction];
                if (!HorizontalTopology.NormalizeCell(ref nx,ny,width,height,HorizontalWrap)) continue;
                int nextPixel = ny * width + nx;
                if (waterDistance[nextPixel] <= nextDistance) continue;
                int nextIndex = _cellIndexByPixel[nextPixel];
                if (nextIndex < 0 || nextIndex >= tiles.Length) continue;
                WorldTile nextTile = tiles[nextIndex];
                // TileZone 覆盖整张地图；只有 zone.city 非空才是已被城市占领的分区。
                // 已有聚落作为不可穿越障碍参与距离场，河流不会拆除建筑或城区。
                if (nextTile == null || nextTile.building != null || nextTile.zone?.city != null) continue;
                waterDistance[nextPixel] = nextDistance;
                frontier.Enqueue(nextPixel);
            }
        }

        List<int> summitPixels = new List<int>();
        for (int pixel = 0; pixel < total; pixel++)
        {
            int index = _cellIndexByPixel[pixel];
            if (index >= 0 && index < tiles.Length && IsSummit(tiles[index]) &&
                waterDistance[pixel] != int.MaxValue) summitPixels.Add(pixel);
        }
        summitPixels.Sort((a, b) => RiverSourceHash(a).CompareTo(RiverSourceHash(b)));

        int sourceLimit = Mathf.Clamp(total / 32768, 3, 24);
        int sourceSpacing = Mathf.Clamp(Math.Min(width, height) / 10, 10, 48);
        int maxRiverLength = Mathf.Clamp(Math.Max(width, height), 64, 512);
        List<int> acceptedSources = new List<int>();
        for (int candidateIndex = 0; candidateIndex < summitPixels.Count &&
             acceptedSources.Count < sourceLimit; candidateIndex++)
        {
            int sourcePixel = summitPixels[candidateIndex];
            int sourceDistance = waterDistance[sourcePixel];
            // 相邻已有浅滩表示该山巅已经拥有河口/旧河道，加载存档时不会重复扩张。
            if (sourceDistance <= 1 || sourceDistance > maxRiverLength) continue;
            int sx = sourcePixel % width;
            int sy = sourcePixel / width;
            bool tooClose = false;
            for (int i = 0; i < acceptedSources.Count; i++)
            {
                int other = acceptedSources[i];
                int ox = other % width;
                int oy = other / width;
                float ddx = HorizontalWrap ? HorizontalTopology.Delta(sx,ox,width) : sx-ox;
                int ddy = sy - oy;
                if (ddx * ddx + ddy * ddy < sourceSpacing * sourceSpacing)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose) continue;
            if (TryPlanRiver(sourcePixel, waterDistance, tiles, maxRiverLength))
                acceptedSources.Add(sourcePixel);
        }
        Debug.Log($"[ClimateWeather] 河网规划完成：山巅候选 {summitPixels.Count}，" +
                  $"有效海/湖 {destinationBodies}，成功河源 {acceptedSources.Count}，" +
                  $"待生成浅滩 {_pendingRiverPixels.Count} 格。");
    }

    private int SeedQualifiedRiverDestinations(WorldTile[] tiles, int[] waterDistance,
        Queue<int> frontier, int width, int height)
    {
        int total = width * height;
        bool[] visited = new bool[total];
        int minimumBodySize = Mathf.Clamp(total / 16384, 8, 32);
        int bodyCount = 0;
        int[] dx = { -1, 1, 0, 0 };
        int[] dy = { 0, 0, -1, 1 };
        Queue<int> componentFrontier = new Queue<int>();
        List<int> component = new List<int>();
        HashSet<int> occupiedColumns = new HashSet<int>();

        for (int startPixel = 0; startPixel < total; startPixel++)
        {
            if (visited[startPixel]) continue;
            int startIndex = _cellIndexByPixel[startPixel];
            if (startIndex < 0 || startIndex >= tiles.Length ||
                tiles[startIndex]?.main_type?.ocean != true)
            {
                visited[startPixel] = true;
                continue;
            }
            component.Clear();
            occupiedColumns.Clear();
            componentFrontier.Enqueue(startPixel);
            visited[startPixel] = true;
            bool hasCloseOrDeepWater = false;
            int minX = width;
            int maxX = 0;
            int minY = height;
            int maxY = 0;
            while (componentFrontier.Count > 0)
            {
                int pixel = componentFrontier.Dequeue();
                component.Add(pixel);
                int x = pixel % width;
                int y = pixel / width;
                occupiedColumns.Add(x);
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                int tileIndex = _cellIndexByPixel[pixel];
                string waterId = tileIndex >= 0 && tileIndex < tiles.Length
                    ? tiles[tileIndex]?.main_type?.id
                    : null;
                if (waterId == "close_ocean" || waterId == "deep_ocean")
                    hasCloseOrDeepWater = true;
                for (int direction = 0; direction < 4; direction++)
                {
                    int nx = x + dx[direction];
                    int ny = y + dy[direction];
                    if (!HorizontalTopology.NormalizeCell(ref nx,ny,width,height,HorizontalWrap)) continue;
                    int nextPixel = ny * width + nx;
                    if (visited[nextPixel]) continue;
                    int nextIndex = _cellIndexByPixel[nextPixel];
                    if (nextIndex < 0 || nextIndex >= tiles.Length ||
                        tiles[nextIndex]?.main_type?.ocean != true) continue;
                    visited[nextPixel] = true;
                    componentFrontier.Enqueue(nextPixel);
                }
            }
            // 单格水坑和旧版残留浅滩不是湖泊；只有具备实际面积的连通水体
            // 才能成为河流终点。外围海洋和正常湖泊都会远大于此阈值。
            int bodyWidth = maxX - minX + 1;
            if (HorizontalWrap && minX==0 && maxX==width-1)
                bodyWidth=HorizontalTopology.CircularColumnSpan(new List<int>(occupiedColumns),width);
            int bodyHeight = maxY - minY + 1;
            float compactness = component.Count / (float)Math.Max(1, bodyWidth * bodyHeight);
            bool broadShallowLake = bodyWidth >= 5 && bodyHeight >= 5 && compactness >= 0.35f;
            if (component.Count < minimumBodySize || (!hasCloseOrDeepWater && !broadShallowLake))
                continue;
            bodyCount++;
            for (int i = 0; i < component.Count; i++)
            {
                int pixel = component[i];
                waterDistance[pixel] = 0;
                frontier.Enqueue(pixel);
            }
        }
        return bodyCount;
    }

    private bool TryPlanRiver(int sourcePixel, int[] waterDistance, WorldTile[] tiles,
        int maxLength)
    {
        int endpoint = FindRiverEndpoint(sourcePixel, waterDistance, tiles, maxLength);
        if (endpoint < 0) return false;

        int expansionBudget = Mathf.Clamp(maxLength * 96, 8192, 98304);
        List<int> basePath = FindRiverAStarPath(sourcePixel, endpoint, tiles,
            maxLength * 2, expansionBudget, -1, 0.10f);
        if (basePath == null || basePath.Count < 2) return false;

        List<int> platformPath = RefineRiverAcrossElevationPlatforms(basePath, tiles, sourcePixel);
        List<int> fourConnected = ConvertRiverToFourNeighbourPath(platformPath, tiles, endpoint);
        if (fourConnected == null || fourConnected.Count < 2) return false;
        // Fail closed if a refinement ever introduced a jump or uphill step.
        for (int i=1;i<fourConnected.Count;i++)
        {
            int a=fourConnected[i-1],b=fourConnected[i];
            float dx=HorizontalWrap ? Math.Abs(HorizontalTopology.Delta(a%MapBox.width,b%MapBox.width,MapBox.width))
                : Math.Abs(a%MapBox.width-b%MapBox.width);
            if (dx+Math.Abs(a/MapBox.width-b/MapBox.width)!=1 || RiverElevationLevel(b)>RiverElevationLevel(a)) return false;
        }

        // 从河口向水源逆序写入：受分批预算限制时，已经显示的河段仍始终连着水体。
        for (int i = fourConnected.Count - 1; i >= 0; i--)
            QueueRiverCenterPixel(fourConnected[i], sourcePixel, tiles);
        return true;
    }

    /// <summary>沿水体距离场下降，先固定这条河对应的实际海岸/湖岸终点。</summary>
    private int FindRiverEndpoint(int sourcePixel, int[] waterDistance, WorldTile[] tiles,
        int maxLength)
    {
        int width = MapBox.width;
        int height = MapBox.height;
        int current = sourcePixel;
        int[] dx = { -1, 1, 0, 0 };
        int[] dy = { 0, 0, -1, 1 };
        int limit = Math.Min(maxLength + 4, waterDistance[sourcePixel] + 4);
        for (int step = 0; step < limit; step++)
        {
            int distance = waterDistance[current];
            if (distance == 0) return current;
            if (distance == int.MaxValue) return -1;
            int x = current % width;
            int y = current / width;
            int best = -1;
            float bestScore = float.MaxValue;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + dx[d];
                int ny = y + dy[d];
                if (!HorizontalTopology.NormalizeCell(ref nx,ny,width,height,HorizontalWrap)) continue;
                int pixel = ny * width + nx;
                if (waterDistance[pixel] >= distance || !IsRiverPassablePixel(pixel, pixel, tiles))
                    continue;
                float score = waterDistance[pixel] * 16f + RiverElevationLevel(pixel) * 0.25f +
                              ((RiverSourceHash(pixel ^ sourcePixel) & 255) / 255f) * 0.05f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = pixel;
                }
            }
            if (best < 0) return -1;
            current = best;
        }
        return waterDistance[current] == 0 ? current : -1;
    }

    /// <summary>
    /// 八邻域 A*。基础路径要求海拔等级单调不升，平台路径则锁定同一海拔等级。
    /// 边代价为欧式步长乘当前海拔等级，启发项为到终点的欧式距离。
    /// </summary>
    private List<int> FindRiverAStarPath(int start, int end, WorldTile[] tiles,
        int maxPathLength, int maxExpanded, int requiredLevel, float noiseStrength)
    {
        int width = MapBox.width;
        int height = MapBox.height;
        Dictionary<int, float> gScore = new Dictionary<int, float>();
        Dictionary<int, int> cameFrom = new Dictionary<int, int>();
        HashSet<int> closed = new HashSet<int>();
        RiverMinHeap open = new RiverMinHeap();
        gScore[start] = 0f;
        open.Push(new RiverSearchNode { Pixel = start, G = 0f, F = RiverEuclidean(start, end) });
        int expanded = 0;

        while (open.Count > 0 && expanded < maxExpanded)
        {
            RiverSearchNode node = open.Pop();
            if (!gScore.TryGetValue(node.Pixel, out float knownG) || node.G > knownG + 0.0001f)
                continue;
            if (node.Pixel == end) return ReconstructRiverPath(cameFrom, start, end, maxPathLength);
            if (!closed.Add(node.Pixel)) continue;
            expanded++;
            int x = node.Pixel % width;
            int y = node.Pixel / width;
            int currentLevel = RiverElevationLevel(node.Pixel);
            for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
            {
                if (ox == 0 && oy == 0) continue;
                int nx = x + ox;
                int ny = y + oy;
                if (!HorizontalTopology.NormalizeCell(ref nx,ny,width,height,HorizontalWrap)) continue;
                int next = ny * width + nx;
                if (closed.Contains(next) || !IsRiverPassablePixel(next, end, tiles)) continue;
                int nextLevel = RiverElevationLevel(next);
                if (requiredLevel >= 0 ? nextLevel != requiredLevel : nextLevel > currentLevel)
                    continue;
                bool diagonal = ox != 0 && oy != 0;
                if (diagonal && !HasRiverOrthogonalBridge(node.Pixel, next, end, tiles, requiredLevel))
                    continue;

                float stepDistance = diagonal ? 1.41421356f : 1f;
                float baseCost = stepDistance * Mathf.Max(1, currentLevel);
                float noise = RiverPathNoise(next, start) * noiseStrength;
                float tentative = knownG + baseCost + noise;
                if (gScore.TryGetValue(next, out float oldG) && tentative >= oldG) continue;
                cameFrom[next] = node.Pixel;
                gScore[next] = tentative;
                open.Push(new RiverSearchNode
                {
                    Pixel = next,
                    G = tentative,
                    F = tentative + RiverEuclidean(next, end)
                });
            }
        }
        return null;
    }

    private List<int> RefineRiverAcrossElevationPlatforms(List<int> basePath,
        WorldTile[] tiles, int sourcePixel)
    {
        List<int> result = new List<int>(basePath.Count * 2);
        int segmentStart = 0;
        while (segmentStart < basePath.Count)
        {
            int level = RiverElevationLevel(basePath[segmentStart]);
            int segmentEnd = segmentStart;
            while (segmentEnd + 1 < basePath.Count &&
                   RiverElevationLevel(basePath[segmentEnd + 1]) == level) segmentEnd++;
            List<int> segment = basePath.GetRange(segmentStart, segmentEnd - segmentStart + 1);
            List<int> refined = RefineRiverPlatform(segment, level, tiles, sourcePixel);
            AppendRiverPath(result, refined);
            segmentStart = segmentEnd + 1;
        }
        return RemoveRiverLoops(result);
    }

    private List<int> RefineRiverPlatform(List<int> baseSegment, int level,
        WorldTile[] tiles, int sourcePixel)
    {
        if (baseSegment.Count < 4) return baseSegment;
        int start = baseSegment[0];
        int end = baseSegment[baseSegment.Count - 1];
        float distance = RiverEuclidean(start, end);
        int sampleCount = Mathf.Clamp(Mathf.FloorToInt(distance / 12f), 0, 6);
        if (sampleCount == 0) return baseSegment;

        List<int> controls = new List<int>(sampleCount + 2) { start };
        for (int sample = 1; sample <= sampleCount; sample++)
        {
            float t = sample / (float)(sampleCount + 1);
            int anchorIndex = Mathf.Clamp(Mathf.RoundToInt(t * (baseSegment.Count - 1)),
                1, baseSegment.Count - 2);
            int sampled = FindRiverPlatformSample(baseSegment[anchorIndex], start, end,
                level, sample, sourcePixel, tiles);
            if (sampled != controls[controls.Count - 1] && sampled != end) controls.Add(sampled);
        }
        controls.Add(end);

        List<int> rebuilt = new List<int>();
        for (int i = 1; i < controls.Count; i++)
        {
            int localLength = Math.Max(24,
                Mathf.CeilToInt(RiverEuclidean(controls[i - 1], controls[i]) * 4f));
            List<int> link = FindRiverAStarPath(controls[i - 1], controls[i], tiles,
                localLength, Mathf.Clamp(baseSegment.Count * 96, 2048, 32768), level, 0.16f);
            if (link == null) return baseSegment;
            AppendRiverPath(rebuilt, link);
        }
        return rebuilt.Count > 0 ? rebuilt : baseSegment;
    }

    private int FindRiverPlatformSample(int anchor, int start, int end, int level,
        int sampleOrdinal, int sourcePixel, WorldTile[] tiles)
    {
        int width = MapBox.width;
        int ax = anchor % width;
        int ay = anchor / width;
        float dx = end % width - start % width;
        if (HorizontalWrap) dx=HorizontalTopology.Delta(start%width,end%width,width);
        float dy = end / width - start / width;
        float length = Mathf.Max(1f, Mathf.Sqrt(dx * dx + dy * dy));
        float perpendicularX = -dy / length;
        float perpendicularY = dx / length;
        float signedNoise = RiverPathNoise(anchor, sourcePixel ^ (sampleOrdinal * 7919)) * 2f - 1f;
        float amplitude = Mathf.Clamp(length * 0.16f, 2f, 8f);
        int targetX = Mathf.RoundToInt(ax + perpendicularX * signedNoise * amplitude);
        int targetY = Mathf.RoundToInt(ay + perpendicularY * signedNoise * amplitude);
        int radius = Mathf.Clamp(Mathf.CeilToInt(amplitude), 2, 9);
        int best = anchor;
        float bestDistance = float.MaxValue;
        for (int oy = -radius; oy <= radius; oy++)
        for (int ox = -radius; ox <= radius; ox++)
        {
            int x = targetX + ox;
            int y = targetY + oy;
            if (!HorizontalTopology.NormalizeCell(ref x,y,MapBox.width,MapBox.height,HorizontalWrap)) continue;
            int pixel = y * width + x;
            if (RiverElevationLevel(pixel) != level || !IsRiverPassablePixel(pixel, end, tiles))
                continue;
            float d = ox * ox + oy * oy + RiverPathNoise(pixel, sourcePixel) * 0.25f;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = pixel;
            }
        }
        return best;
    }

    private List<int> ConvertRiverToFourNeighbourPath(List<int> path, WorldTile[] tiles, int endpoint)
    {
        if (path == null || path.Count == 0) return null;
        List<int> result = new List<int>(path.Count * 2) { path[0] };
        for (int i = 1; i < path.Count; i++)
        {
            int previous = result[result.Count - 1];
            int next = path[i];
            int px = previous % MapBox.width;
            int py = previous / MapBox.width;
            int nx = next % MapBox.width;
            int ny = next / MapBox.width;
            float stepX=HorizontalWrap ? Math.Abs(HorizontalTopology.Delta(px,nx,MapBox.width)) : Math.Abs(nx-px);
            if (stepX == 1 && Math.Abs(ny - py) == 1)
            {
                int bridgeA = py * MapBox.width + nx;
                int bridgeB = ny * MapBox.width + px;
                int bridge = ChooseRiverOrthogonalBridge(previous, next, bridgeA, bridgeB,
                    endpoint, tiles);
                if (bridge < 0) return null;
                if (bridge != result[result.Count - 1]) result.Add(bridge);
            }
            if (next != result[result.Count - 1]) result.Add(next);
        }
        return RemoveRiverLoops(result);
    }

    private int ChooseRiverOrthogonalBridge(int from, int to, int a, int b,
        int endpoint, WorldTile[] tiles)
    {
        bool validA = IsRiverBridgeStep(from, a, to, endpoint, tiles);
        bool validB = IsRiverBridgeStep(from, b, to, endpoint, tiles);
        if (!validA) return validB ? b : -1;
        if (!validB) return a;
        return RiverPathNoise(a, from ^ to) <= RiverPathNoise(b, from ^ to) ? a : b;
    }

    private bool HasRiverOrthogonalBridge(int from, int to, int endpoint,
        WorldTile[] tiles, int requiredLevel)
    {
        int fx = from % MapBox.width;
        int fy = from / MapBox.width;
        int tx = to % MapBox.width;
        int ty = to / MapBox.width;
        int a = fy * MapBox.width + tx;
        int b = ty * MapBox.width + fx;
        return IsRiverBridgeStep(from, a, to, endpoint, tiles, requiredLevel) ||
               IsRiverBridgeStep(from, b, to, endpoint, tiles, requiredLevel);
    }

    private bool IsRiverBridgeStep(int from, int bridge, int to, int endpoint,
        WorldTile[] tiles, int requiredLevel = -1)
    {
        if (!IsRiverPassablePixel(bridge, endpoint, tiles)) return false;
        int fromLevel = RiverElevationLevel(from);
        int bridgeLevel = RiverElevationLevel(bridge);
        int toLevel = RiverElevationLevel(to);
        return requiredLevel >= 0
            ? bridgeLevel == requiredLevel && toLevel == requiredLevel
            : bridgeLevel <= fromLevel && toLevel <= bridgeLevel;
    }

    private bool IsRiverPassablePixel(int pixel, int endpoint, WorldTile[] tiles)
    {
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return false;
        int index = _cellIndexByPixel[pixel];
        if (index < 0 || index >= tiles.Length) return false;
        WorldTile tile = tiles[index];
        if (tile == null || tile.building != null || tile.zone?.city != null) return false;
        if (tile.main_type?.ocean == true)
            return pixel == endpoint || _committedRiverCenterPixels.Contains(pixel);
        if (tile.main_type?.liquid == true || tile.Type?.lava == true)
            return _committedRiverCenterPixels.Contains(pixel);
        return true;
    }

    private int RiverElevationLevel(int pixel)
    {
        return Mathf.Clamp(Mathf.FloorToInt(ContourElevationAtPixel(pixel) * 12f) + 1, 1, 12);
    }

    private float RiverEuclidean(int a, int b)
    {
        int width = MapBox.width;
        float dx = a % width - b % width;
        if (HorizontalWrap) dx=HorizontalTopology.Delta(a%width,b%width,width);
        float dy = a / width - b / width;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private float RiverPathNoise(int pixel, int salt)
    {
        int x = pixel % MapBox.width;
        int y = pixel / MapBox.width;
        float perlin = ClimateNoise(x, y, 0.083f, _worldSeed * 0.007f,
            -_worldSeed * 0.011f);
        float hash = (RiverSourceHash(pixel ^ salt) & 1023) / 1023f;
        return perlin * 0.75f + hash * 0.25f;
    }

    private static List<int> ReconstructRiverPath(Dictionary<int, int> cameFrom,
        int start, int end, int maxPathLength)
    {
        List<int> reverse = new List<int> { end };
        int current = end;
        while (current != start && reverse.Count <= maxPathLength)
        {
            if (!cameFrom.TryGetValue(current, out current)) return null;
            reverse.Add(current);
        }
        if (current != start) return null;
        reverse.Reverse();
        return reverse;
    }

    private static void AppendRiverPath(List<int> destination, List<int> source)
    {
        if (source == null) return;
        for (int i = 0; i < source.Count; i++)
            if (destination.Count == 0 || destination[destination.Count - 1] != source[i])
                destination.Add(source[i]);
    }

    private static List<int> RemoveRiverLoops(List<int> path)
    {
        List<int> result = new List<int>(path.Count);
        Dictionary<int, int> position = new Dictionary<int, int>();
        for (int i = 0; i < path.Count; i++)
        {
            int pixel = path[i];
            if (position.TryGetValue(pixel, out int existing))
            {
                for (int remove = result.Count - 1; remove > existing; remove--)
                {
                    position.Remove(result[remove]);
                    result.RemoveAt(remove);
                }
                continue;
            }
            position[pixel] = result.Count;
            result.Add(pixel);
        }
        return result;
    }

    private void QueueRiverCenterPixel(int pixel, int sourcePixel, WorldTile[] tiles)
    {
        if (pixel == sourcePixel) return;
        int index = pixel >= 0 && pixel < _cellIndexByPixel.Length
            ? _cellIndexByPixel[pixel]
            : -1;
        if (index < 0 || index >= tiles.Length) return;
        WorldTile tile = tiles[index];
        if (tile == null || tile.main_type?.ocean == true || tile.main_type?.liquid == true ||
            tile.Type?.lava == true || tile.building != null || tile.zone?.city != null) return;
        if (_plannedRiverPixels.Add(pixel)) _pendingRiverPixels.Enqueue(pixel);
    }

    private static int RiverSourceHash(int pixel)
    {
        unchecked
        {
            uint hash = (uint)pixel * 747796405u + 2891336453u;
            hash = ((hash >> ((int)(hash >> 28) + 4)) ^ hash) * 277803737u;
            return (int)((hash >> 22) ^ hash);
        }
    }

    private bool IsShallowWaterPixel(int pixel, WorldTile[] tiles)
    {
        int index = pixel >= 0 && pixel < _cellIndexByPixel.Length
            ? _cellIndexByPixel[pixel]
            : -1;
        return index >= 0 && index < tiles.Length &&
               tiles[index]?.main_type?.id == "shallow_waters";
    }

    /// <summary>
    /// 地形可能被玩家、原版或其他模组改写。新一轮规划前只保留实际仍为浅滩的河道，
    /// 避免已消失的坐标成为新河流的“虚假汇流点”。
    /// </summary>
    private void PruneInvalidRiverTracking(WorldTile[] tiles)
    {
        _committedRiverCenterPixels.RemoveWhere(pixel => !IsShallowWaterPixel(pixel, tiles));
        if (_pendingRiverPixels.Count == 0)
            _plannedRiverPixels.RemoveWhere(pixel => !_committedRiverCenterPixels.Contains(pixel));
    }

    /// <summary>
    /// EnsureWorld 可能在 SmoothLoader 仍生成地图时完成数组初始化。
    /// 等加载队列完全结束后再留出一小段稳定时间，然后才依据最终地形规划河网。
    /// </summary>
    private void TryStartAutomaticRiverGeneration()
    {
        if (!_automaticRiverPlanPending) return;
        if (SmoothLoader.isLoading())
        {
            _automaticRiverPlanReadyAt = -1f;
            return;
        }
        if (_automaticRiverPlanReadyAt < 0f)
        {
            _automaticRiverPlanReadyAt = Time.unscaledTime + AutomaticRiverPlanDelay;
            return;
        }
        if (Time.unscaledTime < _automaticRiverPlanReadyAt) return;

        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null || tiles.Length == 0 || tiles.Length != _cells.Length) return;
        _automaticRiverPlanPending = false;
        _automaticRiverPlanReadyAt = -1f;
        _riverAppliedTiles = 0;
        _riverSkippedTiles = 0;
        PruneInvalidRiverTracking(tiles);
        BuildTerrainElevationMap(tiles);
        PlanRiverNetwork(tiles);
    }

    private void StepRivers()
    {
        if (_pendingRiverPixels.Count == 0 || World.world?.tiles_list == null) return;
        TileType shallow = AssetManager.tiles.get("shallow_waters");
        if (shallow == null) return;
        int changed = 0;
        int inspected = 0;
        while (changed < RiverTilesPerTick && inspected < RiverTilesPerTick * 2 &&
               _pendingRiverPixels.Count > 0)
        {
            int pixel = _pendingRiverPixels.Dequeue();
            inspected++;
            int x = pixel % MapBox.width;
            int y = pixel / MapBox.width;
            WorldTile tile = MapBox.instance.GetTileSimple(x, y);
            if (tile?.main_type?.id == "shallow_waters")
            {
                _committedRiverCenterPixels.Add(pixel);
                _riverMoistureFieldDirty = true;
                _riverMoistureRebuildAt = Time.unscaledTime + RiverMoistureRebuildDelay;
                changed++;
                _riverAppliedTiles++;
                continue;
            }
            if (tile == null || tile.main_type?.ocean == true || tile.main_type?.liquid == true ||
                tile.Type?.lava == true || tile.building != null || tile.zone?.city != null)
            {
                _plannedRiverPixels.Remove(pixel);
                _committedRiverCenterPixels.Remove(pixel);
                _riverSkippedTiles++;
                continue;
            }
            MapAction.terraformTile(tile, shallow, null);
            if (tile.main_type?.id == "shallow_waters")
            {
                _committedRiverCenterPixels.Add(pixel);
                _riverMoistureFieldDirty = true;
                _riverMoistureRebuildAt = Time.unscaledTime + RiverMoistureRebuildDelay;
                changed++;
                _riverAppliedTiles++;
            }
            else
            {
                _plannedRiverPixels.Remove(pixel);
                _committedRiverCenterPixels.Remove(pixel);
                _riverSkippedTiles++;
                Debug.LogWarning($"[ClimateWeather] 河道地块写入失败：({x}, {y})，" +
                                 $"最终主层 {tile.main_type?.id ?? "null"}。");
            }
        }
        if (_pendingRiverPixels.Count == 0)
        {
            Debug.Log($"[ClimateWeather] 河道写入完成：成功 {_riverAppliedTiles} 格，" +
                      $"跳过/失败 {_riverSkippedTiles} 格。");
            _riverButtonStatus = _riverSkippedTiles == 0 ? "生成完成" : "完成(有跳过)";
            _riverButtonStatusUntil = Time.unscaledTime + 4f;
        }
    }

    private void TriggerRiverGeneration()
    {
        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null || tiles.Length == 0 || tiles.Length != _cells.Length)
        {
            _riverButtonStatus = "地图未就绪";
            _riverButtonStatusUntil = Time.unscaledTime + 4f;
            return;
        }
        if (_pendingRiverPixels.Count > 0) return;
        // 手动生成代表玩家已主动启动本轮规划，不再额外执行延迟的自动规划。
        _automaticRiverPlanPending = false;
        _automaticRiverPlanReadyAt = -1f;
        _riverAppliedTiles = 0;
        _riverSkippedTiles = 0;
        PruneInvalidRiverTracking(tiles);
        BuildTerrainElevationMap(tiles);
        int before = _pendingRiverPixels.Count;
        PlanRiverNetwork(tiles);
        int added = _pendingRiverPixels.Count - before;
        if (added > 0)
        {
            _riverButtonStatus = $"已规划 {added} 格";
            _riverButtonStatusUntil = Time.unscaledTime + 4f;
        }
        else
        {
            _riverButtonStatus = "无新河道";
            _riverButtonStatusUntil = Time.unscaledTime + 4f;
        }
    }

    private float GetRiverMoisture(WorldTile tile)
    {
        if (tile == null || tile.main_type?.ocean == true) return 0f;
        int pixel = tile.pos.y * MapBox.width + tile.pos.x;
        return pixel >= 0 && pixel < _riverMoistureField.Length
            ? _riverMoistureField[pixel]
            : 0f;
    }

    private static readonly System.Func<int,int,bool> RiverMoistureSource = IsRiverMoistureSource;
    private static bool IsRiverMoistureSource(int x,int y)
    {
        return World.world.GetTileSimple(x,y)?.main_type?.id == "shallow_waters";
    }

    private void StepRiverMoistureField()
    {
        int width=MapBox.width,height=MapBox.height;
        if (width<=0 || height<=0) return;
        if (_riverMoistureBuilder == null || _riverMoistureBuilder.Width!=width ||
            _riverMoistureBuilder.Height!=height || _riverMoistureBuilder.Wrap!=HorizontalWrap)
        {
            _riverMoistureBuilder=new RiverMoistureBuilder(width,height,RiverMoistureRadius,HorizontalWrap);
            _riverMoistureFieldDirty=true;
        }
        var builder=_riverMoistureBuilder;
        if (builder.Complete)
        {
            if (!_riverMoistureFieldDirty || Time.unscaledTime<_riverMoistureRebuildAt) return;
            // Changes arriving during this build set dirty again and queue another
            // build; publication must never erase that pending invalidation.
            _riverMoistureFieldDirty=false;
            builder.Begin();
        }
        long start=System.Diagnostics.Stopwatch.GetTimestamp();
        for (int work=0;work<32768 && !builder.Complete;work+=1024)
        {
            builder.Step(1024,RiverMoistureSource);
            if ((System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency>=1.0) break;
        }
        if (!builder.Complete) return;
        float[] old=_riverMoistureField;
        _riverMoistureField=builder.Result;
        builder.RecycleOutput(old);
        MarkThermalRiverMoistureDirty();
    }
}
