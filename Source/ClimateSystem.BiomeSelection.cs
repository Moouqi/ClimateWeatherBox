using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private static void UpdateBiomeClimateMemory(ClimateCell cell, float elapsed)
    {
        if (!cell.BiomeClimateInitialized)
        {
            cell.BiomeTemperature = cell.Temperature;
            cell.BiomeMoisture = cell.Humidity;
            cell.BiomeClimateInitialized = true;
            return;
        }
        float response = 1f - Mathf.Exp(-Mathf.Max(0f, elapsed) / 180f);
        cell.BiomeTemperature = Mathf.Lerp(cell.BiomeTemperature, cell.Temperature, response);
        cell.BiomeMoisture = Mathf.Lerp(cell.BiomeMoisture, cell.Humidity, response);
    }

    private void GetBiomeClimate(WorldTile tile, ref float t, ref float h)
    {
        if (tile != null && TryGetClimate(tile, out ClimateCell cell) && cell.BiomeClimateInitialized)
        {
            t = cell.BiomeTemperature;
            h = cell.BiomeMoisture;
        }
    }

    private string SelectBiome(WorldTile tile, float t, float h)
    {
        GetBiomeClimate(tile, ref t, ref h);
        float latitude = tile == null ? 0f : Mathf.Abs(GetSignedLatitude(tile.pos.y)) * 90f;
        float patch = tile == null ? 0f :
            (Mathf.PerlinNoise(tile.pos.x / 24f + (_worldSeed & 1023) * .17f,
                tile.pos.y / 24f + (_worldSeed & 2047) * .11f) - .5f) * 2f;
        return BiomeSuitability.Choose(ClimateCelsius(t), h, latitude, IsSummit(tile),
            tile?.Type?.biome_asset?.id ?? string.Empty, patch);
    }

    private bool IsBiomeWithinTolerance(WorldTile tile, string biomeId, float t, float h)
    {
        GetBiomeClimate(tile, ref t, ref h);
        return BiomeSuitability.Score(biomeId, ClimateCelsius(t), h,
            tile == null ? 0f : Mathf.Abs(GetSignedLatitude(tile.pos.y)) * 90f, IsSummit(tile)) >= .16f;
    }
}
