namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    // First stage preserves the existing global-map setting. Regional ranges
    // remain open until the explicit wrap UI and seam visualization are ready.
    internal bool HorizontalWrap => _longitudeMaxDegrees - _longitudeMinDegrees >= 359f;
}
