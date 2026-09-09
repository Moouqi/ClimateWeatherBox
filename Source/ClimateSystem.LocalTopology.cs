using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private float ClimateNoise(float x, float y, float scale, float offsetX, float offsetY)
    {
        if (!HorizontalWrap || MapBox.width <= 0)
            return Mathf.PerlinNoise(x * scale + offsetX, y * scale + offsetY);
        float blend = HorizontalTopology.PeriodicBlend(x, MapBox.width, out float wrapped);
        float ny = y * scale + offsetY;
        return Mathf.Lerp(Mathf.PerlinNoise(wrapped * scale + offsetX, ny),
            Mathf.PerlinNoise((wrapped - MapBox.width) * scale + offsetX, ny), blend);
    }

    // Climate-only neighbors. Never mutate WorldTile.neighbours shared by AI.
    private int ClimateNeighbourCount(WorldTile tile) => (tile?.neighbours?.Length ?? 0)+
        (HorizontalWrap && tile != null && MapBox.width>1 && (tile.x==0 || tile.x==MapBox.width-1) ? 1 : 0);

    private WorldTile ClimateNeighbour(WorldTile tile,int index)
    {
        int count=tile.neighbours?.Length ?? 0;
        if (index<count) return tile.neighbours[index];
        int x=tile.x==0 ? MapBox.width-1 : 0;
        WorldTile other=World.world.GetTileSimple(x,tile.y);
        for (int i=0;i<count;i++) if (tile.neighbours[i]==other) return null;
        return other;
    }

    private WorldTile CloudTerrainSample(int x,int y)
    {
        x=HorizontalWrap ? HorizontalTopology.Wrap(x,MapBox.width) : Mathf.Clamp(x,0,MapBox.width-1);
        return World.world.GetTileSimple(x,Mathf.Clamp(y,0,MapBox.height-1));
    }
}
