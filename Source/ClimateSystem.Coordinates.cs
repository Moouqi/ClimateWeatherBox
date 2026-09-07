using System;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem
{
    private const int CoordinateRebuildTilesPerFrame = 2048;
    private float _latitudeMinDegrees = -90f;
    private float _latitudeMaxDegrees = 90f;
    private float _longitudeMinDegrees = -180f;
    private float _longitudeMaxDegrees = 180f;
    private bool _coordinateRangePending;
    private float _coordinateRangeApplyAt;
    private bool _coordinateSliderDragging;
    private int _coordinateRebuildStage;
    private int _coordinateRebuildCursor;

    private void LoadCoordinateRangeSettings()
    {
        if (PlayerPrefs.GetInt("ClimateWeather.CoordinateRangeSaved", 0) == 0)
        {
            SetTemplateCoordinateValues(_template);
            return;
        }

        _latitudeMinDegrees = Mathf.Clamp(
            PlayerPrefs.GetFloat("ClimateWeather.LatitudeMin", -90f), -90f, 85f);
        _latitudeMaxDegrees = Mathf.Clamp(
            PlayerPrefs.GetFloat("ClimateWeather.LatitudeMax", 90f), _latitudeMinDegrees + 5f, 90f);
        _longitudeMinDegrees = Mathf.Clamp(
            PlayerPrefs.GetFloat("ClimateWeather.LongitudeMin", -180f), -180f, 170f);
        _longitudeMaxDegrees = Mathf.Clamp(
            PlayerPrefs.GetFloat("ClimateWeather.LongitudeMax", 180f), _longitudeMinDegrees + 10f, 180f);
    }

    private void SetTemplateCoordinateValues(ClimateTemplate template)
    {
        _latitudeMinDegrees = template == ClimateTemplate.NorthernHemisphere ? 0f : -90f;
        _latitudeMaxDegrees = template == ClimateTemplate.SouthernHemisphere ? 0f : 90f;
        _longitudeMinDegrees = -180f;
        _longitudeMaxDegrees = 180f;
    }

    private void ApplyTemplateCoordinatePreset(ClimateTemplate template)
    {
        SetTemplateCoordinateValues(template);
        ScheduleCoordinateRangeApply(0f);
    }

    private float LatitudeDegreesAt(float tileY)
    {
        float normalized = MapBox.height <= 1 ? 0.5f : Mathf.Clamp01(tileY / (MapBox.height - 1f));
        return Mathf.Lerp(_latitudeMinDegrees, _latitudeMaxDegrees, normalized);
    }

    private float GetSignedLatitude(float tileY) => LatitudeDegreesAt(tileY) / 90f;

    private float LongitudeDegreesAt(float tileX)
    {
        float normalized = MapBox.width <= 1 ? 0.5f : Mathf.Clamp01(tileX / (MapBox.width - 1f));
        return Mathf.Lerp(_longitudeMinDegrees, _longitudeMaxDegrees, normalized);
    }

    private float LongitudeRadiansAt(float tileX) => LongitudeDegreesAt(tileX) * Mathf.Deg2Rad;

    private float LatitudeDegreesToMapY(float latitudeDegrees)
    {
        return Mathf.Clamp01(Mathf.InverseLerp(_latitudeMinDegrees, _latitudeMaxDegrees, latitudeDegrees));
    }

    private bool TryLongitudeDegreesToMapX(float longitudeDegrees, out float normalizedX)
    {
        float candidate = NormalizeLongitudeDegrees(longitudeDegrees);
        bool inside = candidate >= _longitudeMinDegrees && candidate <= _longitudeMaxDegrees;
        normalizedX = Mathf.Clamp01(Mathf.InverseLerp(
            _longitudeMinDegrees, _longitudeMaxDegrees, candidate));
        return inside;
    }

    private static float NormalizeLongitudeDegrees(float value)
    {
        return Mathf.Repeat(value + 180f, 360f) - 180f;
    }

    private static string FormatCoordinate(float degrees, bool latitude)
    {
        string suffix = latitude
            ? degrees < -0.05f ? "S" : degrees > 0.05f ? "N" : ""
            : degrees < -0.05f ? "W" : degrees > 0.05f ? "E" : "";
        return $"{Mathf.Abs(degrees):F1}°{suffix}";
    }

    private void SetLatitudeMinimum(float value)
    {
        value = Mathf.Clamp(value, -90f, _latitudeMaxDegrees - 5f);
        if (Mathf.Abs(value - _latitudeMinDegrees) < 0.1f) return;
        _latitudeMinDegrees = value;
        ScheduleCoordinateRangeApply();
    }

    private void SetLatitudeMaximum(float value)
    {
        value = Mathf.Clamp(value, _latitudeMinDegrees + 5f, 90f);
        if (Mathf.Abs(value - _latitudeMaxDegrees) < 0.1f) return;
        _latitudeMaxDegrees = value;
        ScheduleCoordinateRangeApply();
    }

    private void SetLongitudeMinimum(float value)
    {
        value = Mathf.Clamp(value, -180f, _longitudeMaxDegrees - 10f);
        if (Mathf.Abs(value - _longitudeMinDegrees) < 0.1f) return;
        _longitudeMinDegrees = value;
        ScheduleCoordinateRangeApply();
    }

    private void SetLongitudeMaximum(float value)
    {
        value = Mathf.Clamp(value, _longitudeMinDegrees + 10f, 180f);
        if (Mathf.Abs(value - _longitudeMaxDegrees) < 0.1f) return;
        _longitudeMaxDegrees = value;
        ScheduleCoordinateRangeApply();
    }

    private void ScheduleCoordinateRangeApply(float delay = 0.35f)
    {
        // 用户在上一轮重算完成前再次拖动时，立即丢弃旧范围的后台任务。
        _coordinateRebuildStage = 0;
        _coordinateRebuildCursor = 0;
        InitializeContinuousAtmosphere();
        _coordinateRangePending = true;
        _coordinateRangeApplyAt = Time.unscaledTime + delay;
    }

    private void TrackCoordinateSliderInteraction()
    {
        bool dragging = GUIUtility.hotControl != 0 && Event.current.type != EventType.MouseUp;
        if (_coordinateRangePending && dragging)
        {
            _coordinateSliderDragging = true;
            _coordinateRangeApplyAt = Time.unscaledTime + 0.35f;
            return;
        }
        if (!_coordinateSliderDragging || dragging) return;
        _coordinateSliderDragging = false;
        _coordinateRangeApplyAt = Time.unscaledTime + 0.08f;
    }

    private void ApplyPendingCoordinateRange()
    {
        if (!_coordinateRangePending || _coordinateSliderDragging ||
            Time.unscaledTime < _coordinateRangeApplyAt) return;
        _coordinateRangePending = false;
        PlayerPrefs.SetInt("ClimateWeather.CoordinateRangeSaved", 1);
        PlayerPrefs.SetFloat("ClimateWeather.LatitudeMin", _latitudeMinDegrees);
        PlayerPrefs.SetFloat("ClimateWeather.LatitudeMax", _latitudeMaxDegrees);
        PlayerPrefs.SetFloat("ClimateWeather.LongitudeMin", _longitudeMinDegrees);
        PlayerPrefs.SetFloat("ClimateWeather.LongitudeMax", _longitudeMaxDegrees);
        PlayerPrefs.Save();

        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null || tiles.Length == 0 || tiles.Length != _cells.Length)
        {
            _worldSeed = int.MinValue;
            return;
        }

        _solarCursor = 0;
        _nextNightRefresh = 0f;
        _biomeTransitionStates.Clear();
        _pendingBiomeTransitions.Clear();
        _pendingBareBiomeRepairs.Clear();
        _queuedBareBiomeRepairs.Clear();
        _bareBiomeCursor = 0;
        _queuedBiomeCandidates.Clear();
        _acceptedBiomeExpansions.Clear();
        _coordinateRebuildStage = 1;
        _coordinateRebuildCursor = 0;
    }

    /// <summary>
    /// 坐标范围会影响所有格点的太阳、温湿度、气压和风场。将完整重算拆成
    /// 两个逐帧阶段，避免大地图在释放滑杆时产生单帧 CPU 峰值。
    /// </summary>
    private bool StepCoordinateRangeRebuild()
    {
        if (_coordinateRebuildStage == 0) return false;
        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null || tiles.Length != _cells.Length)
        {
            _coordinateRebuildStage = 0;
            _worldSeed = int.MinValue;
            return false;
        }

        int end = Math.Min(tiles.Length, _coordinateRebuildCursor + CoordinateRebuildTilesPerFrame);
        if (_coordinateRebuildStage == 1)
        {
            for (int i = _coordinateRebuildCursor; i < end; i++)
            {
                CalculateCell(tiles[i], _cells[i], true, true);
                UpdatePressure(tiles[i], _cells[i], true);
            }
        }
        else
        {
            for (int i = _coordinateRebuildCursor; i < end; i++)
                CalculateWind(tiles[i], _cells[i], true);
        }
        _coordinateRebuildCursor = end;
        if (_coordinateRebuildCursor < tiles.Length) return true;

        if (_coordinateRebuildStage == 1)
        {
            _coordinateRebuildStage = 2;
            _coordinateRebuildCursor = 0;
            return true;
        }

        _coordinateRebuildStage = 0;
        _coordinateRebuildCursor = 0;
        BuildClimateTraversalOrder();
        _layerNeedsFullRefresh = true;
        RefreshLayer(true);
        RefreshClimateAverages(true);
        return false;
    }
}
