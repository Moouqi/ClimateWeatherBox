using System;

namespace ClimateWeather;

// Pure scoring model: deterministic, independent of frame rate and Unity state.
internal static class BiomeSuitability
{
    internal static readonly string[] Names = { "biome_grass", "biome_maple", "biome_birch",
        "biome_jungle", "biome_swamp", "biome_desert", "biome_savanna", "biome_permafrost" };
    internal static float Ramp(float a, float b, float value)
    {
        float t = Math.Max(0f, Math.Min(1f, (value - a) / (b - a)));
        return t * t * (3f - 2f * t);
    }
    private static float Range(float v, float min, float goodMin, float goodMax, float max)
        => Ramp(min, goodMin, v) * (1f - Ramp(goodMax, max, v));

    internal static float Score(string biome, float c, float water, float latitude, bool alpine)
    {
        float l = Math.Abs(latitude);
        switch (biome)
        {
            case "biome_jungle": return 1.05f * Range(c, 12, 22, 33, 42) *
                Ramp(.50f, .76f, water) * (1f - Ramp(25, 38, l));
            case "biome_savanna": return .95f * Range(c, 10, 23, 36, 46) *
                Range(water, .19f, .32f, .48f, .66f) * (1f - Ramp(33, 51, l));
            case "biome_maple": return Range(c, 1, 10, 22, 32) * Ramp(.32f, .59f, water) *
                Range(l, 10, 23, 48, 65);
            case "biome_birch": return Range(c, -12, -1, 11, 23) * Ramp(.30f, .56f, water) *
                Range(l, 25, 40, 63, 79);
            case "biome_swamp": return 1.10f * Range(c, 0, 12, 30, 40) *
                Ramp(.68f, .91f, water) * (1f - Ramp(48, 70, l));
            case "biome_desert": return (1f - Ramp(.15f, .40f, water)) *
                (.65f + .35f * Ramp(-8, 20, c));
            case "biome_permafrost": return (1f - Ramp(-12, 3, c)) *
                (alpine ? 1f : Ramp(40, 65, l));
            case "biome_grass": return .62f * Range(c, -15, 5, 27, 43) *
                Range(water, .12f, .30f, .68f, 1.05f);
            default: return 0f;
        }
    }

    internal static string Choose(float c, float h, float latitude, bool alpine,
        string current, float patch)
    {
        string best = "biome_grass";
        float bestScore = 0f, currentScore = 0f;
        for (int i = 0; i < Names.Length; i++)
        {
            float score = Score(Names[i], c, h, latitude, alpine);
            // Smooth spatial patch preference never makes an excluded biome eligible.
            score *= 1f + patch * (((i * .61803399f) % 1f) * 2f - 1f) * .10f;
            if (Names[i] == current) currentScore = score;
            if (score > bestScore) { bestScore = score; best = Names[i]; }
        }
        // A genuinely unsuitable incumbent cannot be preserved by hysteresis.
        if (currentScore >= .16f && bestScore < currentScore + .12f) return current;
        return best;
    }
}
