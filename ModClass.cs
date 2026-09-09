using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ClimateWeather;

public sealed partial class ClimateSystem : MonoBehaviour
{
    public static ClimateSystem Active { get; private set; }
    private const float SeasonSeconds = 180f;
    // 扩大单批范围并降低调用频率：减少全图纹理上传和调度次数，同时提高总体扫描吞吐。
    private const float TickSeconds = 5f;
    private const int TilesPerTick = 4096;
    // 日照/温度只做数值运算，使用较高频的分批刷新；地形与群系仍保持低频刷新。
    private const float SolarTickSeconds = 0.5f;
    private const int SolarTilesPerTick = 8192;
    private const int WeatherChecks = 20;
    private const int BiomeChanges = 128;
    private const int BareBiomeScansPerTick = 8192;
    private const int BareBiomeRepairsPerTick = 512;
    private const float BiomeTransitionDelay = 90f;
    private const int TerrainChangeCentersPerFrame = 256;
    private const float RiverMoistureRebuildDelay = 0.35f;
    private const float FrozenSolarAbsorption = 0.38f;
    private const float FrozenWarmingResponse = 0.55f;
    private const float LandRainfallMoistureMultiplier = 1.80f;
    private const float ExtremeDroughtHumidity = 0.14f;
    // 与沙化阈值保留 6% 滞后，防止湿度在临界值附近时反复改写地形。
    private const float DroughtRecoveryHumidity = 0.20f;
    private const float VegetationExtremeExposureSeconds = 75f;
    // 河网在世界加载器完全结束后规划一次，实际地形写入按气候周期分批执行。
    // 避免规划后的原版生成步骤改写目标地块，也避免大型地图一次写入过多而卡顿。
    private const int RiverTilesPerTick = 128;
    private const int RiverMoistureRadius = 12;
    private const float AutomaticRiverPlanDelay = 0.5f;

    private ClimateCell[] _cells = Array.Empty<ClimateCell>();
    private int _worldSeed = int.MinValue;
    private int _cursor;
    private float _tick;
    private float _solarTick;
    private int _solarCursor;
    private int _bareBiomeCursor;
    private float _seasonClock;
    private float _averageTemperature;
    private float _averageLandHumidity;
    private float _nextAverageRefresh;
    private int _averageCursor, _averageFrame = -1, _averageTemperatureCount, _averageLandCount;
    private double _averageTemperatureSum, _averageHumiditySum;
    private bool _showPanel = true;
    private ClimateLayer _visibleLayer;
    private ClimateTemplate _template = ClimateTemplate.Global;
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;
    // 1.0 = 现实地球约 33°C 的自然温室效应；滑杆范围 0–2倍。
    private float _greenhouseGasLevel = 1f;
    // 1.0 = 类地大气太阳透射；更厚的大气削弱到达地表的可见光和短波热量。
    private float _atmosphereThickness = 1f;
    private string _currentAgeId = string.Empty;
    private AgeClimateProfile _ageClimateProfile = AgeClimateProfile.Normal;
    private float[] _terrainRawElevation = Array.Empty<float>();
    private float[] _terrainElevation = Array.Empty<float>();
    private int[] _cellIndexByPixel = Array.Empty<int>();
    private int[] _climateTraversalOrder = Array.Empty<int>();
    private int _rainEvents;
    private int _stormEvents;
    private int _tornadoEvents;
    private int _typhoonEvents;
    private int _hurricaneEvents;
    private int _tropicalCycloneEvents;
    private readonly HashSet<int> _climateFrozenTiles = new HashSet<int>();
    private readonly Dictionary<int, FrozenTerrainSnapshot> _forcedFrozenTerrain =
        new Dictionary<int, FrozenTerrainSnapshot>();
    // 仅记录由气候系统沙化的土壤；自然沙地不在湿度回升时被误恢复。
    private readonly Dictionary<int, DroughtTerrainSnapshot> _droughtSandTerrain =
        new Dictionary<int, DroughtTerrainSnapshot>();
    // 弱键跟踪本模组生成的降雨云；云被销毁后状态可由 GC 自动回收。
    private readonly ConditionalWeakTable<Cloud, RainCloudState> _rainCloudStates =
        new ConditionalWeakTable<Cloud, RainCloudState>();
    private readonly ConditionalWeakTable<Building, VegetationTemperatureState> _vegetationTemperatureStates =
        new ConditionalWeakTable<Building, VegetationTemperatureState>();
    private readonly List<Cloud> _activePrecipitationClouds = new List<Cloud>();
    private readonly ConditionalWeakTable<TornadoEffect, TropicalCycloneState> _tropicalCycloneStates =
        new ConditionalWeakTable<TornadoEffect, TropicalCycloneState>();
    private readonly List<TornadoEffect> _activeTropicalCyclones = new List<TornadoEffect>();
    private readonly Dictionary<int, BiomeTransitionState> _biomeTransitionStates =
        new Dictionary<int, BiomeTransitionState>();
    // 判定与地形写入分离：所有扫描到的地块都能完成适生计时，达到条件后进入
    // 去重队列；每个气候周期只消费有限数量，避免预算耗尽导致后续地块失去判定机会。
    private readonly Queue<QueuedBiomeTransition> _pendingBiomeTransitions =
        new Queue<QueuedBiomeTransition>();
    // dirty tile 与写入失败项优先于独立全图裸地游标重试。
    private readonly Queue<int> _pendingBareBiomeRepairs = new Queue<int>();
    private readonly HashSet<int> _queuedBareBiomeRepairs = new HashSet<int>();
    private readonly Dictionary<int, string> _queuedBiomeCandidates =
        new Dictionary<int, string>();
    // 原版自然扩张或玩家放置且通过气候耐受检查的群系，可在加宽适生区内稳定存在。
    // 不把这些地块当作初始草原/沙漠锁死；一旦越出耐受区便恢复核心气候选择。
    private readonly Dictionary<int, string> _acceptedBiomeExpansions =
        new Dictionary<int, string>();
    private bool _applyingClimateBiome;
    // WorldTile 的地形 setter 可能在同一帧被批量/嵌套调用；按像素去重后统一重算，
    // 避免在 terraformTop -> terraformTile -> setTileTypes 链路中读取中间状态。
    private readonly HashSet<int> _pendingTerrainPixels = new HashSet<int>();
    private readonly List<int> _terrainChangeCenters = new List<int>(TerrainChangeCentersPerFrame);
    private readonly HashSet<int> _terrainClimateIndices = new HashSet<int>();
    private readonly HashSet<int> _terrainWindIndices = new HashSet<int>();
    private readonly HashSet<int> _terrainElevationAffected = new HashSet<int>();
    private readonly Dictionary<int, LavaCoolingState> _lavaCoolingStates =
        new Dictionary<int, LavaCoolingState>();
    private readonly Queue<int> _pendingRiverPixels = new Queue<int>();
    // 已预留/写入的坐标用于去重；只有真正写入浅滩的坐标才是可汇入的河道中心线。
    private readonly HashSet<int> _plannedRiverPixels = new HashSet<int>();
    private readonly HashSet<int> _committedRiverCenterPixels = new HashSet<int>();
    private bool _automaticRiverPlanPending;
    private float _automaticRiverPlanReadyAt = -1f;
    private int _riverAppliedTiles;
    private int _riverSkippedTiles;
    private string _riverButtonStatus = "生成河流";
    private float _riverButtonStatusUntil;
    private float[] _riverMoistureField = Array.Empty<float>();
    private RiverMoistureBuilder _riverMoistureBuilder;
    private bool _riverMoistureFieldDirty = true;
    private float _riverMoistureRebuildAt;

    public Season CurrentSeason => (Season)(((int)(_seasonClock / SeasonSeconds)) & 3);
    internal bool IsApplyingClimateBiome => _applyingClimateBiome;

    // 昼夜判定查表：行 sin/cos 依纬度映射缓存，列 hour-angle 余弦与赤纬每帧重建。
    // 悬停提示、灯光蒙版、窗户光过滤共用，单次查询只剩查表与乘加。
    private float[] _nightRowSin = Array.Empty<float>();
    private float[] _nightRowCos = Array.Empty<float>();
    private float[] _nightColCosHour = Array.Empty<float>();
    private float _nightSinDec, _nightCosDec;
    private int _nightCacheFrame = -1;
    private float _nightRowLatMin = float.NaN, _nightRowLatMax = float.NaN;
    internal bool HasAnyNight { get; private set; }

    internal bool IsNightAt(WorldTile tile)
    {
        if (tile == null || MapBox.width <= 1) return false;
        if (World.world?.era_manager?.getCurrentAge()?.overlay_darkness == true) return true;
        if (Time.frameCount != _nightCacheFrame) RefreshNightLookup();
        if (tile.pos.y >= _nightRowSin.Length || tile.pos.x >= _nightColCosHour.Length) return false;
        float sinAltitude = _nightRowSin[tile.pos.y] * _nightSinDec +
                            _nightRowCos[tile.pos.y] * _nightCosDec * _nightColCosHour[tile.pos.x];
        return sinAltitude < -0.02f;
    }

    private void RefreshNightLookup()
    {
        _nightCacheFrame = Time.frameCount;
        int width = Math.Max(1, MapBox.width);
        int height = Math.Max(1, MapBox.height);
        float declination = CurrentSolarDeclinationRadians();
        _nightSinDec = Mathf.Sin(declination);
        _nightCosDec = Mathf.Cos(declination);
        float subsolarLongitude = CurrentSubsolarLongitudeRadians();
        if (_nightColCosHour.Length != width) _nightColCosHour = new float[width];
        for (int x = 0; x < width; x++)
            _nightColCosHour[x] = Mathf.Cos(LongitudeRadiansAt(x) - subsolarLongitude);
        if (_nightRowSin.Length != height ||
            _nightRowLatMin != _latitudeMinDegrees || _nightRowLatMax != _latitudeMaxDegrees)
        {
            _nightRowSin = new float[height];
            _nightRowCos = new float[height];
            for (int y = 0; y < height; y++)
            {
                float latitude = GetSignedLatitude(y) * Mathf.PI * 0.5f;
                _nightRowSin[y] = Mathf.Sin(latitude);
                _nightRowCos[y] = Mathf.Cos(latitude);
            }
            _nightRowLatMin = _latitudeMinDegrees;
            _nightRowLatMax = _latitudeMaxDegrees;
        }
        // 5×5 粗采样判定全图是否仍有夜晚，供 shouldShowLights 门控；
        // 细窄夜带可能漏检，只影响窗户光在极端模板下的显示时机。
        bool any = false;
        for (int ry = 0; ry < 5 && !any; ry++)
        {
            int y = Math.Min(height - 1, ry * (height - 1) / 4);
            for (int rx = 0; rx < 5; rx++)
            {
                int x = Math.Min(width - 1, rx * (width - 1) / 4);
                float sinAltitude = _nightRowSin[y] * _nightSinDec +
                                    _nightRowCos[y] * _nightCosDec * _nightColCosHour[x];
                if (sinAltitude < -0.02f) { any = true; break; }
            }
        }
        HasAnyNight = any;
    }

    private void Awake()
    {
        Active = this;
        EnsureFrozenSeaTopTypes();
        RestoreVanillaOceanFreezeAssets();
        int saved = PlayerPrefs.GetInt("ClimateWeather.Template", (int)ClimateTemplate.Global);
        _template = Enum.IsDefined(typeof(ClimateTemplate), saved)
            ? (ClimateTemplate)saved
            : ClimateTemplate.Global;
        LoadCoordinateRangeSettings();
        int savedUnit = PlayerPrefs.GetInt("ClimateWeather.TemperatureUnit", (int)TemperatureUnit.Celsius);
        _temperatureUnit = Enum.IsDefined(typeof(TemperatureUnit), savedUnit)
            ? (TemperatureUnit)savedUnit
            : TemperatureUnit.Celsius;
        _greenhouseGasLevel = Mathf.Clamp(PlayerPrefs.GetFloat("ClimateWeather.GreenhouseGas", 1f), 0f, 2f);
        _atmosphereThickness = Mathf.Clamp(
            PlayerPrefs.GetFloat("ClimateWeather.AtmosphereThickness", 1f), 0f, 2f);
    }

    private void OnDestroy()
    {
        ResetGpuThermal();
        _gpuAtmosphere?.Dispose();
        Fields?.Dispose();
        Fields = null;
        if (Active == this) Active = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _showPanel = !_showPanel;
        if (Input.GetKeyDown(KeyCode.F3)) ToggleLayer(ClimateLayer.Clouds);
        if (Input.GetKeyDown(KeyCode.F4)) ToggleLayer(ClimateLayer.Elevation);
        if (Input.GetKeyDown(KeyCode.F5)) ToggleLayer(ClimateLayer.Wind);
        if (Input.GetKeyDown(KeyCode.F6)) ToggleLayer(ClimateLayer.Temperature);
        if (Input.GetKeyDown(KeyCode.F7)) ToggleLayer(
            _visibleLayer == ClimateLayer.Humidity ? ClimateLayer.AirHumidity :
            _visibleLayer == ClimateLayer.AirHumidity ? ClimateLayer.Rainfall :
            _visibleLayer == ClimateLayer.Rainfall ? ClimateLayer.Rainfall : ClimateLayer.Humidity);
        if (World.world == null || World.world.tiles_list == null || World.world.tiles_list.Length == 0) return;
        ApplyPendingCoordinateRange();
        if (!EnsureWorld()) return;
        if (StepCoordinateRangeRebuild()) return;
        RefreshAgeClimateProfileIfNeeded();
        Bench.bench("mod.TerrainChanges", "cpu");
        ProcessTerrainChanges();
        Bench.benchEnd("mod.TerrainChanges", "cpu", false, 0);
        TryStartAutomaticRiverGeneration();
        StepRiverMoistureField();
        Bench.bench("mod.DisplayFields", "cpu");
        RefreshDisplayFields();
        Bench.benchEnd("mod.DisplayFields", "cpu", false, 0);
        if (World.world.isPaused()) return;
        _seasonClock += Time.deltaTime;
        Bench.bench("mod.Atmosphere", "cpu");
        AdvanceContinuousAtmosphere();
        Bench.benchEnd("mod.Atmosphere", "cpu", false, 0);
        UpdateTropicalCyclones();
        _solarTick += Time.deltaTime;
        if (_solarTick >= SolarTickSeconds)
        {
            _solarTick = 0f;
            Bench.bench("mod.DynamicTemp", "cpu");
            UpdateDynamicTemperatures();
            Bench.benchEnd("mod.DynamicTemp", "cpu", false, 0);
        }
        _tick += Time.deltaTime;
        if (_tick < TickSeconds) return;
        _tick = 0f;
        Bench.bench("mod.StepClimate", "cpu");
        StepClimate();
        Bench.benchEnd("mod.StepClimate", "cpu", false, 0);
        Bench.bench("mod.StepRivers", "cpu");
        StepRivers();
        Bench.benchEnd("mod.StepRivers", "cpu", false, 0);
        Bench.bench("mod.Weather", "cpu");
        GenerateWeather();
        Bench.benchEnd("mod.Weather", "cpu", false, 0);
    }

    private bool EnsureWorld()
    {
        WorldTile[] tiles = World.world.tiles_list;
        int seed = MapBox.current_world_seed_id;
        if (_worldSeed == seed && _cells.Length == tiles.Length) return true;

        // tiles_list 会在地图生成早期先分配数组，随后才逐格填充。此时读取 pos/Type
        // 会导致初始化中断。必须等待所有地块及其地表类型准备完成。
        if (MapBox.width <= 0 || MapBox.height <= 0 || tiles.Length != MapBox.width * MapBox.height)
            return false;
        for (int i = 0; i < tiles.Length; i++)
            if (tiles[i] == null || tiles[i].Type == null) return false;

        EnsureFrozenSeaTopTypes();
        RestoreVanillaOceanFreezeAssets();
        CreateFrozenSeaTilemaps();
        RepairInvalidOceanSnow(tiles);

        _gpuAtmosphere?.Dispose(); _gpuAtmosphere = null;
        ResetGpuThermal();
        _air = Array.Empty<AirState>();
        _cursor = 0;
        _solarCursor = 0;
        _bareBiomeCursor = 0;
        _seasonClock = 0f;
        _nextAverageRefresh = 0f;
        _averageCursor = 0;
        _averageFrame = -1;
        _currentAgeId = string.Empty;
        _ageClimateProfile = AgeClimateProfile.Normal;
        _climateFrozenTiles.Clear();
        _forcedFrozenTerrain.Clear();
        _droughtSandTerrain.Clear();
        _biomeTransitionStates.Clear();
        _pendingBiomeTransitions.Clear();
        _pendingBareBiomeRepairs.Clear();
        _queuedBareBiomeRepairs.Clear();
        _queuedBiomeCandidates.Clear();
        _acceptedBiomeExpansions.Clear();
        _pendingTerrainPixels.Clear();
        _terrainChangeCenters.Clear();
        _terrainClimateIndices.Clear();
        _terrainWindIndices.Clear();
        _terrainElevationAffected.Clear();
        ClearTrackedRainClouds();
        ClearTrackedTropicalCyclones();
        _lavaCoolingStates.Clear();
        _pendingRiverPixels.Clear();
        _plannedRiverPixels.Clear();
        _committedRiverCenterPixels.Clear();
        _automaticRiverPlanPending = true;
        _automaticRiverPlanReadyAt = -1f;
        _riverAppliedTiles = 0;
        _riverSkippedTiles = 0;
        _riverMoistureField = Array.Empty<float>();
        _riverMoistureBuilder = null;
        _riverMoistureFieldDirty = true;
        _riverMoistureRebuildAt = 0f;
        RefreshAgeClimateProfileIfNeeded(true);
        RecoverFrozenOceanTiles(tiles);
        ClimateCell[] initialized = new ClimateCell[tiles.Length];
        for (int i = 0; i < initialized.Length; i++)
        {
            initialized[i] = new ClimateCell();
            CalculateCell(tiles[i], initialized[i], true, true);
        }
        // 只在完整初始化成功后提交状态，异常或生成中的半成品不会污染下一帧。
        _cells = initialized;
        InitializeAtmosphericCloudField(tiles);
        _worldSeed = seed;
        _cellIndexByPixel = new int[MapBox.width * MapBox.height];
        for (int i = 0; i < _cellIndexByPixel.Length; i++) _cellIndexByPixel[i] = -1;
        for (int i = 0; i < tiles.Length; i++)
        {
            int pixel = tiles[i].pos.y * MapBox.width + tiles[i].pos.x;
            if (pixel >= 0 && pixel < _cellIndexByPixel.Length) _cellIndexByPixel[pixel] = i;
        }
        BuildTerrainElevationMap(tiles);
        BuildClimateTraversalOrder();
        for (int i = 0; i < tiles.Length; i++) UpdatePressure(tiles[i], initialized[i], true);
        for (int i = 0; i < tiles.Length; i++) CalculateWind(tiles[i], initialized[i], true);
        InitializeContinuousAtmosphere();
        InitializeFields();
        RefreshClimateAverages(true);
        return true;
    }

    private void RefreshAgeClimateProfileIfNeeded(bool force = false)
    {
        WorldAgeAsset age = World.world?.era_manager?.getCurrentAge();
        string ageId = age?.id ?? "age_unknown";
        if (!force && ageId == _currentAgeId) return;

        AgeClimateProfile profile = AgeClimateProfile.Normal;
        if (age != null)
        {
            if (age.overlay_sun)
            {
                profile.TemperatureOffsetC += 11f;
                profile.SunlightMultiplier *= 1.15f;
                profile.HumidityMultiplier *= 0.72f;
                profile.RainMultiplier *= 0.55f;
                profile.StormMultiplier *= 0.80f;
                profile.HurricaneMultiplier *= 1.35f;
            }
            if (age.overlay_rain || age.particles_rain)
            {
                profile.TemperatureOffsetC -= 1.5f;
                profile.SunlightMultiplier *= 0.86f;
                profile.HumidityOffset += 0.18f;
                profile.RainMultiplier *= 2.25f;
                profile.StormMultiplier *= 1.45f;
                profile.HurricaneMultiplier *= 0.90f;
            }
            if (age.global_freeze_world)
            {
                profile.TemperatureOffsetC -= 20f;
                profile.SunlightMultiplier *= 0.72f;
                profile.HumidityMultiplier *= 0.88f;
                profile.RainMultiplier *= 1.30f;
                profile.StormMultiplier *= 0.70f;
                profile.HurricaneMultiplier *= 0.35f;
                profile.ForceSnow = true;
            }
            if (age.overlay_darkness)
            {
                profile.TemperatureOffsetC -= 4f;
                profile.SunlightMultiplier *= 0.62f;
                profile.HumidityMultiplier *= 1.06f;
            }
            if (age.overlay_moon)
            {
                profile.TemperatureOffsetC -= 2f;
                profile.HumidityOffset += 0.04f;
            }
            if (age.overlay_ash || age.particles_ash)
            {
                profile.TemperatureOffsetC -= 6f;
                profile.SunlightMultiplier *= 0.52f;
                profile.HumidityMultiplier *= 0.74f;
                profile.RainMultiplier *= 0.40f;
                profile.StormMultiplier *= 0.65f;
                profile.HurricaneMultiplier *= 0.55f;
            }
            if (age.overlay_chaos || age.flag_chaos)
            {
                profile.TemperatureOffsetC += 6f;
                profile.HumidityMultiplier *= 0.82f;
                profile.StormMultiplier *= 1.80f;
                profile.HurricaneMultiplier *= 1.55f;
            }
            if (age.overlay_magic || age.particles_magic)
            {
                profile.TemperatureOffsetC += 1.5f;
                profile.HumidityOffset += 0.05f;
                profile.RainMultiplier *= 1.10f;
            }
        }

        _currentAgeId = ageId;
        _ageClimateProfile = profile;
        _solarCursor = 0;
        _biomeTransitionStates.Clear();
        _pendingBiomeTransitions.Clear();
        _pendingBareBiomeRepairs.Clear();
        _queuedBareBiomeRepairs.Clear();
        _bareBiomeCursor = 0;
        _queuedBiomeCandidates.Clear();
        _acceptedBiomeExpansions.Clear();
        Debug.Log($"[ClimateWeather] 纪元气候适配：{ageId}，温度修正 " +
                  $"{profile.TemperatureOffsetC:+0.0;-0.0;0.0}°C，降雨倍率 {profile.RainMultiplier:F2}。");
    }

    private void StepClimate()
    {
        WorldTile[] tiles = World.world.tiles_list;
        if (_climateTraversalOrder.Length != tiles.Length) BuildClimateTraversalOrder();
        if (_climateTraversalOrder.Length == 0) return;
        int count = Math.Min(TilesPerTick, _climateTraversalOrder.Length);
        // 拆分测量在循环内只累计秒表读数，循环外一次性写入 Bench，
        // 避免每个格子都触发字典查询扭曲测量结果。
        long windTicks = 0, lavaTicks = 0, vegetationTicks = 0,
             droughtTicks = 0, biomeTicks = 0, polarTicks = 0;
        for (int n = 0; n < count; n++)
        {
            int orderPosition = (_cursor + n) % _climateTraversalOrder.Length;
            int index = _climateTraversalOrder[orderPosition];
            if (index < 0 || index >= tiles.Length) continue;
            CalculateCell(tiles[index], _cells[index], false, false);
            long segment = System.Diagnostics.Stopwatch.GetTimestamp();
            CalculateWind(tiles[index], _cells[index], false);
            windTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
            segment = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateLavaCooling(tiles[index], _cells[index]);
            lavaTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
            segment = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateVegetationTemperatureStress(tiles[index], _cells[index]);
            vegetationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
            segment = System.Diagnostics.Stopwatch.GetTimestamp();
            if (UpdateExtremeDroughtTerrain(tiles[index], _cells[index]))
            {
                droughtTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
                InvalidateQueuedBiome(index);
                continue;
            }
            droughtTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
            segment = System.Diagnostics.Stopwatch.GetTimestamp();
            EvaluateClimateBiome(index, tiles[index], _cells[index]);
            biomeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
            segment = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyPolarFreezing(tiles[index], _cells[index]);
            polarTicks += System.Diagnostics.Stopwatch.GetTimestamp() - segment;
        }
        _cursor = (_cursor + count) % _climateTraversalOrder.Length;
        Bench.bench("mod.Climate.Repair", "cpu");
        RepairBareBiomeTiles(tiles);
        Bench.benchEnd("mod.Climate.Repair", "cpu", false, 0);
        Bench.bench("mod.Climate.BiomeApply", "cpu");
        ApplyQueuedBiomeTransitions(tiles, BiomeChanges);
        Bench.benchEnd("mod.Climate.BiomeApply", "cpu", false, 0);
        if (!Bench.bench_enabled) return;
        double toSeconds = 1.0 / System.Diagnostics.Stopwatch.Frequency;
        Bench.benchSave("mod.Climate.Wind", windTicks * toSeconds, 0, "cpu");
        Bench.benchSave("mod.Climate.Lava", lavaTicks * toSeconds, 0, "cpu");
        Bench.benchSave("mod.Climate.Vegetation", vegetationTicks * toSeconds, 0, "cpu");
        Bench.benchSave("mod.Climate.Drought", droughtTicks * toSeconds, 0, "cpu");
        Bench.benchSave("mod.Climate.Biome", biomeTicks * toSeconds, 0, "cpu");
        Bench.benchSave("mod.Climate.Polar", polarTicks * toSeconds, 0, "cpu");
    }

    /// <summary>
    /// 由 WorldTile 地形 setter 的 Harmony 后缀调用。只登记最终像素，不在 setter 内
    /// 直接计算，确保 setTileTypes 同时更改 top/main 时读取到完整的新地形。
    /// </summary>
    internal void NotifyTerrainChanged(WorldTile tile, bool riverTopologyChanged = false)
    {
        if (tile == null || _cells.Length == 0 || _cellIndexByPixel.Length == 0) return;
        int x = tile.pos.x;
        int y = tile.pos.y;
        if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) return;
        int pixel = y * MapBox.width + x;
        if (pixel >= 0 && pixel < _cellIndexByPixel.Length && _cellIndexByPixel[pixel] >= 0)
        {
            _pendingTerrainPixels.Add(pixel);
            if (riverTopologyChanged)
            {
                _riverMoistureFieldDirty = true;
                _riverMoistureRebuildAt = Time.unscaledTime + RiverMoistureRebuildDelay;
            }
        }
    }


    private void ProcessTerrainChanges()
    {
        if (_pendingTerrainPixels.Count == 0 || World.world?.tiles_list == null) return;

        List<int> centers = _terrainChangeCenters;
        centers.Clear();
        foreach (int pixel in _pendingTerrainPixels)
        {
            centers.Add(pixel);
            if (centers.Count >= TerrainChangeCentersPerFrame) break;
        }
        for (int i = 0; i < centers.Count; i++) _pendingTerrainPixels.Remove(centers[i]);

        // 中心及一格邻域需要更新温湿度：海陆转换会改变邻格的沿岸水汽，
        // 群系/植被变化会改变本格保湿，山峰与雪峰会改变本格温度。
        HashSet<int> climateIndices = _terrainClimateIndices;
        climateIndices.Clear();
        // 风读取相邻温度和地形高度；扩大到两格，确保温度邻域刷新后外圈风也同步。
        HashSet<int> windIndices = _terrainWindIndices;
        windIndices.Clear();
        for (int i = 0; i < centers.Count; i++)
        {
            int centerPixel = centers[i];
            int centerX = centerPixel % MapBox.width;
            int centerY = centerPixel / MapBox.width;
            AddClimateIndicesAround(centerX, centerY, 1, climateIndices);
            AddClimateIndicesAround(centerX, centerY, 2, windIndices);
        }

        WorldTile[] tiles = World.world.tiles_list;
        UpdateTerrainElevationMap(centers, tiles);
        foreach (int index in climateIndices)
        {
            if (index < 0 || index >= tiles.Length || index >= _cells.Length) continue;
            WorldTile tile = tiles[index];
            if (tile?.Type == null) continue;
            // 地形变化立即重算湿度与地表属性，但保留当前温度；温度随后由统一的
            // 时间积分器按新地表热惯性靠近目标，避免冻融/群系变化造成瞬时跳温。
            CalculateCell(tile, _cells[index], false, true);
            if (tile.data != null) _biomeTransitionStates.Remove(tile.data.tile_id);
            // dirty tile 可能刚被火焰、灾害或地形工具移除了群系顶层；不等待
            // 数分钟后的全图遍历，送入独立的高优先级修复队列。
            if (IsClimateBiomeSeedableBareSoil(tile)) QueueBareBiomeRepair(index);
            else EvaluateClimateBiome(index, tile, _cells[index]);
        }
        // 必须在全部温度先更新后再计算风，否则同一批次会因遍历顺序产生方向差异。
        foreach (int index in windIndices)
        {
            if (index < 0 || index >= tiles.Length || index >= _cells.Length) continue;
            WorldTile tile = tiles[index];
            if (tile?.Type != null)
            {
                CalculateWind(tile, _cells[index], true);
                MarkThermalClassificationDirty(index);
            }
        }
    }

    private void AddClimateIndicesAround(int centerX, int centerY, int radius, HashSet<int> destination)
    {
        for (int y = Math.Max(0, centerY - radius); y <= Math.Min(MapBox.height - 1, centerY + radius); y++)
        for (int x = Math.Max(0, centerX - radius); x <= Math.Min(MapBox.width - 1, centerX + radius); x++)
        {
            int pixel = y * MapBox.width + x;
            if (pixel < 0 || pixel >= _cellIndexByPixel.Length) continue;
            int index = _cellIndexByPixel[pixel];
            if (index >= 0 && index < _cells.Length) destination.Add(index);
        }
    }

    private void BuildClimateTraversalOrder()
    {
        int total = Math.Max(0, MapBox.width * MapBox.height);
        if (total == 0 || _cellIndexByPixel.Length != total)
        {
            _climateTraversalOrder = Array.Empty<int>();
            return;
        }
        // 按离赤道的距离排序，使气候地形刷新由赤道向两极推进。
        // 在约6°宽的纬度带内按小片区交错推进，避免整排完成造成直线前沿。
        const float featherDegrees = 6f;
        List<KeyValuePair<float, int>> ranked = new List<KeyValuePair<float, int>>(total);
        for (int y = 0; y < MapBox.height; y++)
        for (int x = 0; x < MapBox.width; x++)
        {
            int pixel = y * MapBox.width + x;
            int index = _cellIndexByPixel[pixel];
            if (index < 0) continue;
            float equatorDistance = Mathf.Abs(LatitudeDegreesAt(y));
            float jitter = TraversalJitter01(x / 4, y / 4) * featherDegrees +
                           TraversalJitter01(x, y) * 0.35f;
            ranked.Add(new KeyValuePair<float, int>(equatorDistance + jitter, index));
        }
        ranked.Sort((a, b) =>
        {
            int keyOrder = a.Key.CompareTo(b.Key);
            return keyOrder != 0 ? keyOrder : a.Value.CompareTo(b.Value);
        });
        _climateTraversalOrder = new int[ranked.Count];
        for (int i = 0; i < ranked.Count; i++) _climateTraversalOrder[i] = ranked[i].Value;
        _cursor = 0;
    }

    private float TraversalJitter01(int x, int y)
    {
        unchecked
        {
            uint hash = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)_worldSeed;
            hash ^= hash >> 13;
            hash *= 1274126177u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFu) / 65535f;
        }
    }

    private float TerrainElevationAt(int x, int y, float fallback)
    {
        // 风场的山体梯度必须采样接缝另一侧，不能把越界当作同高平地。
        if (!HorizontalTopology.NormalizeCell(ref x, y, MapBox.width, MapBox.height, HorizontalWrap)) return fallback;
        WorldTile tile = MapBox.instance.GetTileSimple(x, y);
        return tile == null ? fallback : GetTerrainElevation(tile);
    }

    private static float GetTerrainElevation(WorldTile tile)
    {
        TileType main = tile?.main_type;
        TileTypeBase surface = tile?.Type;
        if (main == null || surface == null) return 0f;
        if (surface.summit || main.summit) return 1f;
        if (main.mountains || main.edge_mountains) return 0.78f;
        if (surface.edge_mountains) return 0.64f;
        if (surface.edge_hills || surface.rocks) return 0.34f;
        return 0f;
    }

    private void BuildTerrainElevationMap(WorldTile[] tiles)
    {
        int total = Math.Max(0, MapBox.width * MapBox.height);
        _terrainRawElevation = new float[total];
        _terrainElevation = new float[total];
        if (tiles == null || _cellIndexByPixel.Length != total) return;
        for (int pixel = 0; pixel < total; pixel++)
        {
            int index = _cellIndexByPixel[pixel];
            _terrainRawElevation[pixel] = index >= 0 && index < tiles.Length
                ? GetContourElevation(tiles[index])
                : 0f;
        }
        for (int pixel = 0; pixel < total; pixel++)
            _terrainElevation[pixel] = SmoothContourElevation(pixel);
    }

    private void UpdateTerrainElevationMap(List<int> changedPixels, WorldTile[] tiles)
    {
        int total = Math.Max(0, MapBox.width * MapBox.height);
        if (_terrainRawElevation.Length != total || _terrainElevation.Length != total)
        {
            BuildTerrainElevationMap(tiles);
            return;
        }
        for (int i = 0; i < changedPixels.Count; i++)
        {
            int pixel = changedPixels[i];
            if (pixel < 0 || pixel >= total) continue;
            int index = _cellIndexByPixel[pixel];
            if (index >= 0 && index < tiles.Length)
                _terrainRawElevation[pixel] = GetContourElevation(tiles[index]);
        }
        HashSet<int> affected = _terrainElevationAffected;
        affected.Clear();
        for (int i = 0; i < changedPixels.Count; i++)
        {
            int pixel = changedPixels[i];
            int cx = pixel % MapBox.width;
            int cy = pixel / MapBox.width;
            for (int y = Math.Max(0, cy - 1); y <= Math.Min(MapBox.height - 1, cy + 1); y++)
            for (int x = Math.Max(0, cx - 1); x <= Math.Min(MapBox.width - 1, cx + 1); x++)
                affected.Add(y * MapBox.width + x);
        }
        foreach (int pixel in affected) _terrainElevation[pixel] = SmoothContourElevation(pixel);
    }

    private float SmoothContourElevation(int pixel)
    {
        if (pixel < 0 || pixel >= _terrainRawElevation.Length) return 0f;
        int centerX = pixel % MapBox.width;
        int centerY = pixel / MapBox.width;
        float sum = 0f;
        float weightSum = 0f;
        for (int oy = -1; oy <= 1; oy++)
        for (int ox = -1; ox <= 1; ox++)
        {
            int x = centerX + ox;
            int y = centerY + oy;
            if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) continue;
            float weight = (ox == 0 ? 2f : 1f) * (oy == 0 ? 2f : 1f);
            sum += _terrainRawElevation[y * MapBox.width + x] * weight;
            weightSum += weight;
        }
        return weightSum > 0f ? sum / weightSum : _terrainRawElevation[pixel];
    }

    private static float GetContourElevation(WorldTile tile)
    {
        TileType main = tile?.main_type;
        TileTypeBase surface = tile?.Type;
        if (main == null || surface == null) return 0f;
        string id = main.id ?? string.Empty;
        if (id == "deep_ocean") return 0.02f;
        if (id == "close_ocean") return 0.09f;
        if (id == "shallow_waters") return 0.16f;
        if (id == "pit_deep_ocean") return 0.04f;
        if (id == "pit_close_ocean") return 0.11f;
        if (id == "pit_shallow_waters") return 0.19f;
        if (surface.summit || main.summit || id == "summit") return 1f;
        if (main.mountains || id == "mountains") return 0.82f;
        if (surface.edge_mountains || main.edge_mountains) return 0.73f;
        if (id == "hills" || surface.edge_hills) return 0.58f;
        if (surface.rocks) return 0.52f;
        if (id == "soil_high") return 0.43f;
        if (id == "soil_low") return 0.34f;
        if (main.sand || surface.sand || id == "sand") return 0.24f;
        if (surface.lava) return 0.47f;
        return main.ocean ? 0.12f : 0.34f;
    }

    private float ContourElevationAtPixel(int pixel, WorldTile fallbackTile = null)
    {
        return pixel >= 0 && pixel < _terrainElevation.Length
            ? _terrainElevation[pixel]
            : GetContourElevation(fallbackTile);
    }

    private static bool IsSummit(WorldTile tile)
    {
        return tile?.Type?.summit == true || tile?.main_type?.summit == true;
    }

    private static bool IsSnowPeak(WorldTile tile)
    {
        string id = tile?.top_type?.id ?? tile?.Type?.id ?? string.Empty;
        return IsSummit(tile) && (tile?.data?.frozen == true ||
               id.IndexOf("snow_summit", StringComparison.OrdinalIgnoreCase) >= 0 ||
               id.IndexOf("frozen", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsFrozenClimateSurface(WorldTile tile)
    {
        if (tile?.data?.frozen == true || IsFrozenSeaTop(tile?.top_type)) return true;
        string id = tile?.top_type?.id ?? tile?.Type?.id ?? string.Empty;
        return id.StartsWith("frozen_", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith("snow_", StringComparison.OrdinalIgnoreCase);
    }

    private float TemperatureAt(int x, int y, float fallback)
    {
        if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) return fallback;
        int pixel = y * MapBox.width + x;
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return fallback;
        int index = _cellIndexByPixel[pixel];
        return index >= 0 && index < _cells.Length ? _cells[index].Temperature : fallback;
    }

    private void CalculateCell(WorldTile tile, ClimateCell cell, bool initializeTemperature,
        bool immediateClimate)
    {
        if (AtmosphereReady && !initializeTemperature) return;
        // signedLatitude: 0 = 赤道，+1 = 北极，-1 = 南极。
        float signedLatitude = GetSignedLatitude(tile.pos.y);
        float latitude = Mathf.Abs(signedLatitude);
        float yearPhase = (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f;
        float noise = Mathf.PerlinNoise(tile.pos.x * 0.025f + _worldSeed * 0.001f, tile.pos.y * 0.025f);
        bool isOcean = tile.Type != null && tile.Type.layer_type == TileLayerType.Ocean;
        CalculateSolarTemperature(tile, noise, isOcean, cell.Humidity,
            out float sunlight, out float targetTemperature);
        int oceanNeighbours = 0;
        if (!isOcean)
            for (int i = 0; i < ClimateNeighbourCount(tile); i++)
                if (ClimateNeighbour(tile,i)?.Type?.layer_type == TileLayerType.Ocean)
                    oceanNeighbours++;
        float coastMoisture = Mathf.Clamp01(oceanNeighbours * 0.16f);
        float riverMoisture = GetRiverMoisture(tile);
        // 湿度表示相对湿度：同等水汽含量下，地表升温会提高饱和上限，
        // 因而陆地和海洋的当前相对湿度都会下降；暖海蒸发另外补充水汽。
        float surfaceRetention = GetSurfaceMoistureRetention(tile);
        float moistureTemperature = initializeTemperature ? targetTemperature : cell.Temperature;
        float mildTemperatureMoisture = 1f - Mathf.Abs(moistureTemperature - 0.55f) * 1.35f;
        float temperatureDrying = Mathf.SmoothStep(0f, 1f,
            Mathf.InverseLerp(0.45f, 1f, moistureTemperature));
        float relativeHumidityLoss = temperatureDrying * (isOcean ? 0.20f : 0.32f);
        float oceanEvaporation = isOcean ? 0.38f + moistureTemperature * 0.32f : 0f;
        // 全球大气环流湿度带：赤道辐合带湿润，副热带高压带干旱，
        // 中纬西风带较湿，两极寒冷干燥。关于赤道南北镜像分布。
        float equatorialWet = Mathf.Clamp01(1f - latitude / 0.18f) * 0.28f;
        float subtropicalDry = Mathf.Clamp01(1f - Mathf.Abs(latitude - 0.31f) / 0.15f) * 0.48f;
        float midLatitudeWet = Mathf.Clamp01(1f - Mathf.Abs(latitude - 0.58f) / 0.20f) * 0.12f;
        float polarDry = Mathf.Clamp01((latitude - 0.78f) / 0.22f) * 0.38f;
        // 当地夏半年暖海岸季风增强；南北半球自然反相。
        float localSummer = Mathf.Max(0f, Mathf.Sin(yearPhase) * (signedLatitude >= 0f ? 1f : -1f));
        float monsoonMoisture = coastMoisture * localSummer * Mathf.Clamp01(1f - latitude) * 0.16f;
        float availableMoisture =
            0.08f + noise * 0.30f +
            surfaceRetention * 0.34f +
            coastMoisture * (0.28f + moistureTemperature * 0.24f) +
            riverMoisture +
            Mathf.Clamp01(mildTemperatureMoisture) * 0.13f +
            oceanEvaporation + equatorialWet + midLatitudeWet + monsoonMoisture;
        // 副热带下沉气流、极地高压和高温应该按比例削弱现有水汽，
        // 而不是作为固定值相加后把整个纬度带硬截断到 0%。
        float circulationRetention = (1f - subtropicalDry) * (1f - polarDry);
        float thermalRetention = 1f - relativeHumidityLoss;
        float humidity = Mathf.Clamp01(
            availableMoisture * circulationRetention * thermalRetention *
            _ageClimateProfile.HumidityMultiplier + _ageClimateProfile.HumidityOffset);
        // 即使内陆沙漠也有背景水汽；沿海与河道附近的下限随水源提高。
        float humidityFloor = isOcean
            ? 0.24f
            : 0.035f + coastMoisture * 0.10f + riverMoisture * 0.30f;
        humidity = Mathf.Max(humidityFloor, humidity);
        cell.Sunlight = Mathf.Lerp(cell.Sunlight, sunlight, initializeTemperature ? 1f : 0.42f);
        if (initializeTemperature)
        {
            cell.Temperature = targetTemperature;
            cell.LastTemperatureTime = _seasonClock;
        }
        cell.Humidity = Mathf.Lerp(cell.Humidity, humidity, immediateClimate ? 1f : 0.22f);
    }

    private void UpdateLavaCooling(WorldTile tile, ClimateCell cell)
    {
        if (tile?.data == null) return;
        int tileId = tile.data.tile_id;
        if (tile.Type?.lava != true)
        {
            _lavaCoolingStates.Remove(tileId);
            return;
        }
        // 保持原版世界法则语义：永久岩浆和盖娅盟约开启时不由气候系统凝固。
        if (WorldLawLibrary.world_law_forever_lava.isEnabled() ||
            WorldLawLibrary.world_law_gaias_covenant.isEnabled())
        {
            _lavaCoolingStates.Remove(tileId);
            return;
        }

        string lavaTypeId = tile.Type.id;
        if (!_lavaCoolingStates.TryGetValue(tileId, out LavaCoolingState state) ||
            state.LavaTypeId != lavaTypeId)
        {
            _lavaCoolingStates[tileId] = new LavaCoolingState
            {
                LavaTypeId = lavaTypeId,
                CoolingProgress = 0f,
                LastClimateTime = _seasonClock
            };
            return;
        }

        float elapsed = Mathf.Clamp(_seasonClock - state.LastClimateTime, 0f, 60f);
        state.LastClimateTime = _seasonClock;
        if (elapsed <= 0f) return;

        int waterNeighbours = 0;
        for (int i = 0; i < ClimateNeighbourCount(tile); i++)
            if (ClimateNeighbour(tile,i)?.Type?.ocean == true) waterNeighbours++;

        // 冷空气、夜晚、高湿和邻水都会加速散热；炎热干燥的白昼岩浆冷却最慢。
        float coolingRate = 0.42f + (1f - cell.Temperature) * 1.15f +
                            cell.Humidity * 1.10f + (1f - cell.Sunlight) * 0.28f +
                            Mathf.Min(1.4f, waterNeighbours * 0.35f);
        state.CoolingProgress += elapsed * coolingRate;

        // 高等级岩浆储热更多。依次复用原版 lava3→lava2→lava1→lava0→hills
        // 资产和 MapAction 链路，因此发光材质、伤害与最终丘陵统计均保持原版行为。
        float stageRequirement = 32f + Mathf.Max(0, tile.Type.lava_level) * 10f;
        if (state.CoolingProgress < stageRequirement) return;
        state.CoolingProgress = 0f;
        LavaHelper.coolDownLava(tile);
        if (tile.Type?.lava == true)
        {
            state.LavaTypeId = tile.Type.id;
            state.LastClimateTime = _seasonClock;
        }
        else
        {
            _lavaCoolingStates.Remove(tileId);
        }
    }

    private void CalculateSolarTemperature(WorldTile tile, float noise, bool isOcean, float humidity,
        out float sunlight, out float temperature)
    {
        float signedLatitude = GetSignedLatitude(tile.pos.y);
        float latitude = Mathf.Abs(signedLatitude);
        float latitudeRadians = signedLatitude * Mathf.PI * 0.5f;
        float longitudeRadians = LongitudeRadiansAt(tile.pos.x);
        float hourAngle = longitudeRadians - CurrentSubsolarLongitudeRadians();
        float declination = CurrentSolarDeclinationRadians();
        float solarAltitude = Mathf.Sin(latitudeRadians) * Mathf.Sin(declination) +
                              Mathf.Cos(latitudeRadians) * Mathf.Cos(declination) * Mathf.Cos(hourAngle);
        float directSun = Mathf.Pow(Mathf.Clamp01(solarAltitude), 0.62f);
        float twilight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.10f, 0.10f, solarAltitude));
        float lightTransmission = SurfaceLightTransmission();
        float heatTransmission = SurfaceSolarHeatTransmission();
        sunlight = Mathf.Clamp01((directSun * 0.88f + twilight * 0.08f) * lightTransmission +
                                 (noise - 0.5f) * 0.06f);
        sunlight = Mathf.Clamp01(sunlight * _ageClimateProfile.SunlightMultiplier);

        // 先建立随纬度、季节缓慢变化的日平均温度，再叠加有限的零均值昼夜扰动。
        // 不再把瞬时短波辐射直接映射成最高 48–58°C 的额外升温。
        float elevation = GetTerrainElevation(tile);
        bool snowPeak = IsSnowPeak(tile);
        bool frozenSurface = IsFrozenClimateSurface(tile);
        float absorbedSolarFactor = snowPeak ? 0.72f : 1f;
        if (frozenSurface) absorbedSolarFactor *= FrozenSolarAbsorption;

        float yearPhase = (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f;
        float radiativeBaseC = -45f + 39f * (1f - Mathf.Pow(latitude, 1.30f));
        float greenhouseC = _greenhouseGasLevel * 33f;
        float seasonalC = Mathf.Sin(yearPhase) * Mathf.Sign(signedLatitude) *
                          Mathf.Pow(latitude, 0.80f) * 16f;
        float altitudeCoolingC = elevation * 12f + (snowPeak ? 4f : 0f);
        // 大气厚度只控制短波透射；温室气体仍独立负责长波保温。
        float atmosphericSolarMeanC = (heatTransmission - 1f) * 18f;
        float meanC = radiativeBaseC + greenhouseC + seasonalC +
                      atmosphericSolarMeanC + (noise - 0.5f) * 6f - altitudeCoolingC +
                      _ageClimateProfile.TemperatureOffsetC;

        float amplitudeC = GetDiurnalAmplitudeC(tile, isOcean, humidity);
        float latitudeAttenuation = Mathf.Sqrt(Mathf.Clamp01(Mathf.Cos(latitudeRadians)));
        float diurnalWave = Mathf.Cos(hourAngle) * latitudeAttenuation;
        // 湿润空气和云量代理同时压低白天升温并减弱夜间辐射降温。
        float humidity01 = Mathf.Clamp01(humidity);
        float diurnalC = amplitudeC * diurnalWave;
        if (diurnalC >= 0f)
            diurnalC *= heatTransmission * absorbedSolarFactor * Mathf.Lerp(1f, 0.82f, humidity01);
        else
            diurnalC *= Mathf.Lerp(1f, 0.72f, humidity01);

        float targetC = meanC + diurnalC;
        temperature = Mathf.Clamp01((targetC + 50f) / 100f);
        // 气候显示范围无法表示真实岩浆温度，但必须把岩浆作为强局地热源参与湿度和风场。
        if (tile?.Type?.lava == true)
            temperature = Mathf.Max(temperature, 0.78f + Mathf.Clamp(tile.Type.lava_level, 0, 3) * 0.065f);
    }

    private float GetDiurnalAmplitudeC(WorldTile tile, bool isOcean, float humidity)
    {
        if (isOcean)
        {
            string id = tile?.main_type?.id ?? string.Empty;
            float oceanAmplitude = id == "shallow_waters" ? 2.5f : id == "close_ocean" ? 1.5f : 0.8f;
            return oceanAmplitude * Mathf.Lerp(1f, 0.70f, Mathf.Clamp01(humidity));
        }

        string biome = tile?.Type?.biome_asset?.id ?? string.Empty;
        float amplitude = biome == "biome_desert" ? 12f :
                          biome == "biome_savanna" ? 10f :
                          biome == "biome_grass" ? 8f :
                          biome == "biome_permafrost" ? 7f :
                          biome == "biome_birch" ? 7f :
                          biome == "biome_maple" ? 6f :
                          biome == "biome_jungle" || biome == "biome_swamp" ? 5f : 8f;
        if (IsCoastal(tile)) amplitude *= 0.78f;
        return amplitude * Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(humidity));
    }

    private static float GetThermalTimeSeconds(WorldTile tile, bool isOcean, float humidity, bool warming)
    {
        if (tile?.Type?.lava == true) return 2.5f;
        float thermalTime;
        if (isOcean)
        {
            string id = tile?.main_type?.id ?? string.Empty;
            thermalTime = id == "shallow_waters" ? 85f : id == "close_ocean" ? 125f : 175f;
        }
        else
        {
            string biome = tile?.Type?.biome_asset?.id ?? string.Empty;
            bool sand = tile?.main_type?.sand == true || tile?.Type?.sand == true;
            thermalTime = sand ? 18f :
                          biome == "biome_swamp" || biome == "biome_jungle" ? 45f :
                          biome == "biome_maple" || biome == "biome_birch" ? 38f : 30f;
            thermalTime *= Mathf.Lerp(1f, 1.35f, Mathf.Clamp01(humidity));
        }
        if (IsFrozenClimateSurface(tile))
            thermalTime *= warming ? 1f / FrozenWarmingResponse : 1.20f;
        return thermalTime;
    }

    private void UpdateDynamicTemperatures()
    {
        if (UpdateDynamicTemperaturesGpu()) return;
        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null || tiles.Length != _cells.Length || tiles.Length == 0) return;
        int count = Math.Min(SolarTilesPerTick, tiles.Length);
        for (int n = 0; n < count; n++)
        {
            int index = (_solarCursor + n) % tiles.Length;
            WorldTile tile = tiles[index];
            if (tile?.Type == null) continue;
            float noise = Mathf.PerlinNoise(tile.pos.x * 0.025f + _worldSeed * 0.001f, tile.pos.y * 0.025f);
            bool ocean = tile.Type.layer_type == TileLayerType.Ocean;
            ClimateCell cell = _cells[index];
            CalculateSolarTemperature(tile, noise, ocean, cell.Humidity,
                out float sunlight, out float targetTemperature);
            cell.Sunlight = Mathf.Lerp(cell.Sunlight, sunlight, 0.42f);
            float elapsed = cell.LastTemperatureTime < 0f
                ? SolarTickSeconds
                : Mathf.Clamp(_seasonClock - cell.LastTemperatureTime, 0f, 60f);
            cell.LastTemperatureTime = _seasonClock;
            float thermalTime = GetThermalTimeSeconds(tile, ocean, cell.Humidity,
                targetTemperature > cell.Temperature);
            float thermalResponse = 1f - Mathf.Exp(-elapsed / Mathf.Max(1f, thermalTime));
            if (AtmosphereReady && tile.Type.lava != true)
            {
                // Clouds shade the surface by day and moderate cooling at night.
                targetTemperature += cell.CloudCover * (0.025f - sunlight * 0.07f);
                targetTemperature = Mathf.Lerp(targetTemperature,
                    (cell.AirTemperatureC + 50f) / 100f, 0.15f);
            }
            cell.Temperature = Mathf.Lerp(cell.Temperature, targetTemperature, thermalResponse);
            SyncAtmosphere(tile, cell, elapsed);
            UpdateBiomeClimateMemory(cell, elapsed);
        }
        _solarCursor = (_solarCursor + count) % tiles.Length;
        // 显示字段与模拟节奏解耦：RefreshDisplayFields 按固定节流周期采样当前状态，
        // 模拟批次不再直接触发纹理上传。
    }

    /// <summary>
    /// 太阳温度批次之间持续蒸散陆地水分。速率随超过 32°C 的程度、日照和地表
    /// 保水能力变化；冰冻、熔岩和海洋不使用此规则。
    /// </summary>
    private static void ApplyHighTemperatureLandDrying(WorldTile tile, ClimateCell cell,
        float elapsed, bool ocean)
    {
        if (tile?.Type == null || cell == null || elapsed <= 0f || ocean ||
            tile.Type.lava || IsFrozenClimateSurface(tile)) return;
        float celsius = ClimateCelsius(cell.Temperature);
        if (celsius <= 32f) return;

        float heat = Mathf.InverseLerp(32f, 50f, celsius);
        float retention = Mathf.Clamp01(GetSurfaceMoistureRetention(tile));
        float exposure = Mathf.Lerp(0.70f, 1.20f, Mathf.Clamp01(cell.Sunlight));
        float resistance = Mathf.Lerp(1.30f, 0.55f, retention);
        // 50°C、低保水地表约每分钟损失 1.4%–1.8% 湿度；属于缓慢累积变化。
        float lossPerSecond = Mathf.Lerp(0.00002f, 0.00022f, heat) * exposure * resistance;
        cell.Humidity = Mathf.Max(0.02f, cell.Humidity - lossPerSecond * elapsed);
    }

    /// <summary>
    /// 湿度低于 14% 时把普通高/低土壤主层沙化。湿度恢复到 20% 后回退到本模组
    /// 改写前的主层和顶层；山地、水体、城区及自然沙地均不会被错误覆盖或恢复。
    /// </summary>
    private bool UpdateExtremeDroughtTerrain(WorldTile tile, ClimateCell cell)
    {
        if (tile?.data == null || tile.main_type == null || tile.Type == null || cell == null)
            return false;
        int tileId = tile.data.tile_id;
        if (_droughtSandTerrain.TryGetValue(tileId, out DroughtTerrainSnapshot snapshot))
        {
            // 玩家、纪元或其他模组已把该地块改成别的主层时，放弃陈旧快照。
            if (tile.main_type != TileLibrary.sand)
            {
                _droughtSandTerrain.Remove(tileId);
                return false;
            }
            if (cell.Humidity < DroughtRecoveryHumidity) return false;
            MapAction.terraformTile(tile, snapshot.Main, snapshot.Top, TerraformLibrary.nothing);
            _droughtSandTerrain.Remove(tileId);
            _biomeTransitionStates.Remove(tileId);
            _acceptedBiomeExpansions.Remove(tileId);
            return true;
        }

        if (cell.Humidity >= ExtremeDroughtHumidity || tile.data.frozen ||
            tile.main_type.ocean || tile.main_type.liquid || tile.Type.lava ||
            tile.building != null || tile.zone?.city != null) return false;
        // 沙化只改变可生长群系的普通土壤，不削平丘陵、山峰和特殊地块。
        if (tile.main_type != TileLibrary.soil_low && tile.main_type != TileLibrary.soil_high)
            return false;
        string biomeId = tile.Type.biome_asset?.id ?? string.Empty;
        if (!ManagedBiomes.Contains(biomeId)) return false;

        _droughtSandTerrain[tileId] = new DroughtTerrainSnapshot
        {
            Main = tile.main_type,
            Top = tile.top_type
        };
        MapAction.terraformTile(tile, TileLibrary.sand, null, TerraformLibrary.nothing);
        _biomeTransitionStates.Remove(tileId);
        _acceptedBiomeExpansions.Remove(tileId);
        return true;
    }

    /// <summary>
    /// 树木和小型植被只有持续处于其群系无法耐受的温度时才枯死。树木沿用原版
    /// startMakingRuins 进入废墟状态；没有废墟贴图的小草会按原版逻辑枯萎移除。
    /// </summary>
    private void UpdateVegetationTemperatureStress(WorldTile tile, ClimateCell cell)
    {
        Building vegetation = tile?.building;
        if (vegetation?.asset == null || cell == null || vegetation.current_tile != tile ||
            !vegetation.isAlive() || vegetation.isRuin() || vegetation.isOnRemove()) return;
        BuildingType kind = vegetation.asset.building_type;
        if (kind != BuildingType.Building_Tree && kind != BuildingType.Building_Plant) return;

        string biomeId = tile.Type?.biome_asset?.id ?? string.Empty;
        // 魔法、腐化等特殊群系保留其原版抗性，只处理本气候系统管理的自然群系。
        if (!ManagedBiomes.Contains(biomeId)) return;
        GetVegetationTemperatureTolerance(biomeId, out float coldLimitC, out float hotLimitC);
        float celsius = ClimateCelsius(cell.Temperature);
        bool extreme = celsius <= coldLimitC || celsius >= hotLimitC;

        VegetationTemperatureState state = _vegetationTemperatureStates.GetOrCreateValue(vegetation);
        float elapsed = state.LastClimateTime < 0f
            ? 0f
            : Mathf.Clamp(_seasonClock - state.LastClimateTime, 0f, 120f);
        state.LastClimateTime = _seasonClock;
        if (extreme)
            state.ExposureSeconds += elapsed;
        else
            state.ExposureSeconds = Mathf.Max(0f, state.ExposureSeconds - elapsed * 2f);
        if (state.ExposureSeconds < VegetationExtremeExposureSeconds) return;

        _vegetationTemperatureStates.Remove(vegetation);
        vegetation.startMakingRuins();
    }

    private static void GetVegetationTemperatureTolerance(string biomeId,
        out float coldLimitC, out float hotLimitC)
    {
        switch (biomeId)
        {
            case "biome_permafrost":
                coldLimitC = -38f;
                hotLimitC = 25f;
                return;
            case "biome_desert":
                coldLimitC = -8f;
                hotLimitC = 55f;
                return;
            case "biome_savanna":
                coldLimitC = 0f;
                hotLimitC = 48f;
                return;
            case "biome_jungle":
            case "biome_swamp":
                coldLimitC = 5f;
                hotLimitC = 43f;
                return;
            case "biome_maple":
                coldLimitC = -18f;
                hotLimitC = 40f;
                return;
            case "biome_birch":
                coldLimitC = -30f;
                hotLimitC = 35f;
                return;
            default: // 草原
                coldLimitC = -12f;
                hotLimitC = 42f;
                return;
        }
    }

    private void ApplyPolarFreezing(WorldTile tile, ClimateCell cell)
    {
        if (tile?.Type == null || tile.data == null || tile.main_type == null) return;
        int tileId = tile.data.tile_id;
        if (!TryGetFreezeThreshold(tile, out float freezeThreshold)) return;
        float thawThreshold = freezeThreshold + 0.10f;

        if (tile.main_type.ocean)
        {
            bool frozenSea = _forcedFrozenTerrain.ContainsKey(tileId) || IsFrozenSeaTop(tile.top_type);
            if (!frozenSea && cell.Temperature <= freezeThreshold)
                FreezeOcean(tile);
            else if (frozenSea && cell.Temperature >= thawThreshold)
                RestoreFrozenOcean(tile);
            return;
        }

        // 陆地只调用原版 frozen/unfreeze 机制。
        bool tracked = _climateFrozenTiles.Contains(tileId);
        if (!tracked && tile.data.frozen && cell.Temperature <= freezeThreshold)
        {
            _climateFrozenTiles.Add(tileId);
            tracked = true;
        }
        if (!tracked && !tile.data.frozen && cell.Temperature <= freezeThreshold)
        {
            tile.freeze(100);
            if (tile.data.frozen) _climateFrozenTiles.Add(tileId);
            return;
        }
        if (tracked && cell.Temperature >= thawThreshold)
        {
            if (tile.data.frozen) tile.unfreeze(100);
            if (!tile.data.frozen) _climateFrozenTiles.Remove(tileId);
        }
    }

    private static bool TryGetFreezeThreshold(WorldTile tile, out float threshold)
    {
        threshold = 0f;
        TileType main = tile?.main_type;
        TileTypeBase surface = tile?.Type;
        if (main == null || surface == null) return false;
        bool ocean = main.ocean;
        bool sand = !ocean && (main.sand || surface.sand);
        bool land = !ocean && !main.liquid && !main.considered_empty_tile;
        if (!ocean && !sand && !land) return false;
        threshold = main.id == "shallow_waters" ? 0.48f :
                    main.id == "close_ocean" ? 0.44f :
                    ocean ? 0.40f : sand ? 0.45f : 0.42f;
        return true;
    }

    internal bool ShouldBlockClimateUnfreeze(WorldTile tile)
    {
        if (tile?.data == null || tile.main_type?.ocean == true ||
            !_climateFrozenTiles.Contains(tile.data.tile_id)) return false;
        if (!TryGetClimate(tile, out ClimateCell cell) || !TryGetFreezeThreshold(tile, out float threshold)) return false;
        return cell.Temperature < threshold + 0.10f;
    }

    private void FreezeOcean(WorldTile tile)
    {
        if (tile?.data == null || tile.main_type?.ocean != true) return;
        TopTileType frozenTop = GetFrozenSeaTop(tile.main_type.id);
        if (frozenTop == null) return;
        int tileId = tile.data.tile_id;
        if (!_forcedFrozenTerrain.ContainsKey(tileId))
            _forcedFrozenTerrain[tileId] = new FrozenTerrainSnapshot { Main = tile.main_type, Top = tile.top_type };
        tile.setTopTileType(frozenTop, true);
        tile.data.frozen = false;
        tile.health = 10;
        MapBox.instance.setTileDirty(tile);
        tile.updateStats();
        _climateFrozenTiles.Add(tileId);
    }

    private void RestoreFrozenOcean(WorldTile tile)
    {
        if (tile?.data == null) return;
        int tileId = tile.data.tile_id;
        if (!_forcedFrozenTerrain.TryGetValue(tileId, out FrozenTerrainSnapshot snapshot)) return;
        tile.data.frozen = false;
        tile.setTileTypes(snapshot.Main, snapshot.Top, true);
        tile.health = 10;
        MapBox.instance.setTileDirty(tile);
        tile.updateStats();
        _forcedFrozenTerrain.Remove(tileId);
        _climateFrozenTiles.Remove(tileId);
    }

    private static void RepairInvalidOceanSnow(WorldTile[] tiles)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            WorldTile tile = tiles[i];
            if (tile?.main_type?.ocean != true || tile.data == null) continue;
            string topId = tile.top_type?.id ?? string.Empty;
            bool invalidSnow = topId == "frozen_low" || topId == "frozen_high" ||
                               topId == "snow_sand" || topId == "snow_hills" || topId == "snow_summit" ||
                               topId == "permafrost_low" || topId == "permafrost_high";
            bool legacyIce = topId == "ice";
            bool legacyFrozen = tile.data.frozen && !IsFrozenSeaTop(tile.top_type);
            if (!invalidSnow && !legacyIce && !legacyFrozen) continue;
            tile.data.frozen = false;
            if (invalidSnow || legacyIce || legacyFrozen) tile.setTileTypes(tile.main_type, null, true);
            MapBox.instance.setTileDirty(tile);
            tile.updateStats();
        }
    }

    private static void EnsureFrozenSeaTopTypes()
    {
        EnsureFrozenSeaTop("frozen_shallow_sea", new Color32(183, 225, 242, 255));
        EnsureFrozenSeaTop("frozen_close_sea", new Color32(155, 203, 231, 255));
        EnsureFrozenSeaTop("frozen_deep_sea", new Color32(126, 175, 213, 255));
    }

    private static void EnsureFrozenSeaTop(string id, Color32 color)
    {
        TopTileType ice = AssetManager.top_tiles.get("ice");
        if (ice == null) return;
        TopTileType top = AssetManager.top_tiles.get(id) ?? AssetManager.top_tiles.clone(id, "ice");
        if (top == null) return;
        // AssetLibrary.clone 只复制地块配置，不会为新 id 建立可渲染的
        // TileSprites/变体图集。三种海冰显式共用原版 ice 的完整贴图集。
        top.sprites = ice.sprites;
        top.color = color;
        top.layer_type = TileLayerType.Ocean;
        top.ground = false;
        top.liquid = true;
        top.ocean = true;
        top.can_build_on = false;
        top.can_be_frozen = false;
        top.biome_id = null;
        top.biome_asset = null;
    }

    private static void CreateFrozenSeaTilemaps()
    {
        if (World.world?.tilemap == null) return;
        TopTileType ice = AssetManager.top_tiles.get("ice");
        if (ice?.sprites == null) return;
        string[] ids = { "frozen_shallow_sea", "frozen_close_sea", "frozen_deep_sea" };
        for (int i = 0; i < ids.Length; i++)
        {
            TopTileType top = AssetManager.top_tiles.get(ids[i]);
            if (top == null) continue;
            top.sprites = ice.sprites;
            // 新 TopTileType 在原版图集初始化完成后动态注册，必须为当前
            // WorldTilemap 显式创建变体缓存，否则 getVariation 会读取空数组。
            World.world.tilemap.createTileMapFor(top);
        }
    }

    private static void RestoreVanillaOceanFreezeAssets()
    {
        TileType shallow = AssetManager.tiles.get("shallow_waters");
        TileType close = AssetManager.tiles.get("close_ocean");
        TileType deep = AssetManager.tiles.get("deep_ocean");
        if (shallow != null) { shallow.can_be_frozen = true; shallow.freeze_to_id = "ice"; shallow.fast_freeze = true; }
        if (close != null) { close.can_be_frozen = false; close.freeze_to_id = string.Empty; close.fast_freeze = false; }
        if (deep != null) { deep.can_be_frozen = false; deep.freeze_to_id = string.Empty; deep.fast_freeze = false; }
    }

    private void RecoverFrozenOceanTiles(WorldTile[] tiles)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            WorldTile tile = tiles[i];
            if (tile?.data == null || tile.main_type?.ocean != true || !IsFrozenSeaTop(tile.top_type)) continue;
            int tileId = tile.data.tile_id;
            tile.data.frozen = false;
            _forcedFrozenTerrain[tileId] = new FrozenTerrainSnapshot { Main = tile.main_type, Top = null };
            _climateFrozenTiles.Add(tileId);
        }
    }

    private static TopTileType GetFrozenSeaTop(string oceanId)
    {
        string id = oceanId == "shallow_waters" ? "frozen_shallow_sea" :
                    oceanId == "close_ocean" ? "frozen_close_sea" :
                    oceanId == "deep_ocean" ? "frozen_deep_sea" : null;
        return id == null ? null : AssetManager.top_tiles.get(id);
    }

    private static bool IsFrozenSeaTop(TopTileType top)
    {
        string id = top?.id;
        return id == "frozen_shallow_sea" || id == "frozen_close_sea" || id == "frozen_deep_sea";
    }

    private void SelectTemplate(ClimateTemplate template)
    {
        _template = template;
        PlayerPrefs.SetInt("ClimateWeather.Template", (int)template);
        ApplyTemplateCoordinatePreset(template);
    }

    private void ToggleTemperatureUnit()
    {
        _temperatureUnit = _temperatureUnit == TemperatureUnit.Celsius
            ? TemperatureUnit.Fahrenheit
            : TemperatureUnit.Celsius;
        PlayerPrefs.SetInt("ClimateWeather.TemperatureUnit", (int)_temperatureUnit);
        PlayerPrefs.Save();
    }

    private void SetGreenhouseGasLevel(float value)
    {
        value = Mathf.Clamp(value, 0f, 2f);
        if (Mathf.Abs(value - _greenhouseGasLevel) < 0.001f) return;
        _greenhouseGasLevel = value;
        PlayerPrefs.SetFloat("ClimateWeather.GreenhouseGas", _greenhouseGasLevel);
        PlayerPrefs.Save();
    }

    private void SetAtmosphereThickness(float value)
    {
        value = Mathf.Clamp(value, 0f, 2f);
        if (Mathf.Abs(value - _atmosphereThickness) < 0.001f) return;
        _atmosphereThickness = value;
        PlayerPrefs.SetFloat("ClimateWeather.AtmosphereThickness", _atmosphereThickness);
        PlayerPrefs.Save();
    }

    private float SurfaceLightTransmission()
    {
        return _atmosphereThickness <= 1f
            ? Mathf.Lerp(1.20f, 1f, _atmosphereThickness)
            : Mathf.Lerp(1f, 0.55f, _atmosphereThickness - 1f);
    }

    private float SurfaceSolarHeatTransmission()
    {
        return _atmosphereThickness <= 1f
            ? Mathf.Lerp(1.35f, 1f, _atmosphereThickness)
            : Mathf.Lerp(1f, 0.45f, _atmosphereThickness - 1f);
    }

    private static float ClimateCelsius(float normalizedTemperature)
    {
        // 气候模型 0–1 映射到适合世界尺度展示的 -50°C 至 50°C。
        return Mathf.Lerp(-50f, 50f, Mathf.Clamp01(normalizedTemperature));
    }

    private float ToCelsius(float normalizedTemperature) => ClimateCelsius(normalizedTemperature);

    private string FormatTemperature(float normalizedTemperature)
    {
        return FormatCelsius(ToCelsius(normalizedTemperature));
    }

    private string FormatTemperature(WorldTile tile, float normalizedTemperature)
    {
        return FormatCelsius(SurfaceCelsius(tile, normalizedTemperature));
    }

    private string FormatCelsius(float celsius)
    {
        return _temperatureUnit == TemperatureUnit.Celsius
            ? $"{celsius:F1} °C"
            : $"{celsius * 9f / 5f + 32f:F1} °F";
    }

    /// <summary>
    /// 气候运算继续使用 -50–50°C 的环境温标，避免破坏冻结、群系和天气阈值；
    /// 熔岩则使用独立的物理表面温度。最高等级约 1400°C，并随原版四级冷却
    /// 链逐级降到凝固前约 650°C。
    /// </summary>
    private float SurfaceCelsius(WorldTile tile, float normalizedTemperature)
    {
        float ambient = ToCelsius(normalizedTemperature);
        if (tile?.Type?.lava != true) return ambient;

        int level = Mathf.Clamp(tile.Type.lava_level, 0, 3);
        float stageStartC = 800f + level * 200f;
        float stageEndC = level > 0 ? stageStartC - 200f : 650f;
        if (tile.data == null ||
            !_lavaCoolingStates.TryGetValue(tile.data.tile_id, out LavaCoolingState state))
            return stageStartC;
        float stageRequirement = 32f + level * 10f;
        float progress = Mathf.Clamp01(state.CoolingProgress / Mathf.Max(1f, stageRequirement));
        return Mathf.Lerp(stageStartC, stageEndC, progress);
    }

    private static float GetSurfaceMoistureRetention(WorldTile tile)
    {
        if (tile?.Type == null) return 0.15f;
        if (tile.Type.layer_type == TileLayerType.Ocean) return 1f;
        if (tile.Type.lava) return 0.02f;
        string biome = tile.Type.biome_asset?.id ?? string.Empty;
        switch (biome)
        {
            case "biome_swamp": return 1f;
            case "biome_jungle": return 0.90f;
            case "biome_maple": return 0.78f;
            case "biome_birch": return 0.70f;
            case "biome_grass": return 0.55f;
            case "biome_permafrost": return 0.46f;
            case "biome_savanna": return 0.30f;
            case "biome_desert": return 0.08f;
            default:
                // 特殊群系仍参与气候，但不由本模组替换；有植被的地表比裸岩更保湿。
                return tile.Type.is_biome ? 0.48f : 0.18f;
        }
    }

    private void GenerateWeather()
    {
        WorldTile[] tiles = World.world.tiles_list;
        PruneTrackedRainClouds();
        int maxActiveClouds = Mathf.Clamp(tiles.Length / 1024, 24, 72);
        for (int i = 0; i < WeatherChecks; i++)
        {
            int index = UnityEngine.Random.Range(0, tiles.Length);
            WorldTile tile = tiles[index];
            ClimateCell c = _cells[index];
            if (tile == null) continue;
            bool ocean = tile.main_type?.ocean == true;
            float heat01 = Mathf.Clamp01(Mathf.InverseLerp(0.42f, 0.95f, c.Temperature));
            float moisture01 = Mathf.Clamp01(Mathf.InverseLerp(0.46f, 0.95f, c.Humidity));
            float atmosphericCloud = Mathf.Clamp01(c.CloudCover);
            float convection = c.Humidity * Mathf.Lerp(0.45f, 1.85f, heat01);
            float seasonalRainFactor = SeasonalRainFactor(tile);
            float localSummer = LocalSummerStrength(tile);
            float landEvapotranspiration = ocean
                ? 0f
                : LandEvapotranspirationPotential(tile, c, heat01, localSummer);
            // 暖空气能承载更多水汽，暖海还会持续蒸发：即使相对湿度
            // 因升温下降，对流和降雨云生成数量仍会随温度上升；当地夏季和
            // 随太阳直射带移动的热带雨季会进一步提高概率。
            // 海洋是主要水汽源：全年提高云生成频率，当地夏季再额外增强。
            float oceanFrequency = ocean ? Mathf.Lerp(1.35f, 2.35f, localSummer) : 1f;
            float rainChance = Mathf.Clamp01(
                (0.012f + moisture01 * Mathf.Lerp(0.022f, 0.082f, heat01) +
                 (ocean ? 0.022f + heat01 * 0.028f :
                  0.010f + landEvapotranspiration * 0.060f)) *
                seasonalRainFactor * oceanFrequency *
                Mathf.Lerp(0.72f, 1.58f, atmosphericCloud) *
                _ageClimateProfile.RainMultiplier);
            float cloudHumidityThreshold = ocean ? 0.46f :
                Mathf.Lerp(0.52f, 0.38f, landEvapotranspiration);
            cloudHumidityThreshold -= atmosphericCloud * 0.08f;
            if (c.RainRate > 0.00001f && atmosphericCloud > 0.12f &&
                UnityEngine.Random.value < rainChance)
            {
                int cloudCount = ocean ? 2 : 1;
                if (UnityEngine.Random.value < heat01 * 0.65f) cloudCount++;
                if (!ocean && UnityEngine.Random.value < landEvapotranspiration * 0.45f) cloudCount++;
                if (ocean && UnityEngine.Random.value < heat01 * 0.35f) cloudCount++;
                if (ocean && UnityEngine.Random.value < 0.25f + localSummer * 0.65f) cloudCount++;
                if (ocean && UnityEngine.Random.value < localSummer * 0.40f) cloudCount++;
                if (UnityEngine.Random.value < atmosphericCloud * 0.55f) cloudCount++;
                float wetSeasonExcess = Mathf.Clamp01((seasonalRainFactor - 1f) / 0.75f);
                if (UnityEngine.Random.value < wetSeasonExcess * 0.60f) cloudCount++;
                cloudCount = Math.Min(cloudCount,
                    Math.Max(0, maxActiveClouds - _activePrecipitationClouds.Count));
                for (int cloudIndex = 0; cloudIndex < cloudCount; cloudIndex++)
                {
                    WorldTile spawnTile = NearbyWeatherTile(tile, cloudIndex == 0 ? 0 : 3);
                    string cloudType = PrecipitationCloudType(spawnTile);
                    Cloud cloud = EffectsLibrary.spawn("fx_cloud", spawnTile, cloudType) as Cloud;
                    if (cloud != null)
                    {
                        cloud.setLifespan(10f + c.Humidity * 22f + heat01 * 8f);
                        RegisterRainCloud(cloud);
                        _rainEvents++;
                    }
                }
                // 海洋蒸发和陆地蒸散都要从源地扣除水汽；雨云落地后再由
                // ApplyRainfallMoisture 回补目的地，形成可追踪的水循环。
                float sourceMoistureCost = ocean ? 0.012f :
                    Mathf.Lerp(0.018f, 0.030f, landEvapotranspiration);
            }
            if (c.Humidity > 0.68f && c.Temperature > 0.48f &&
                UnityEngine.Random.value < 0.012f * convection * _ageClimateProfile.StormMultiplier)
            {
                MapBox.spawnLightningSmall(tile, 0.2f + convection * 0.18f, null);
                _stormEvents++;
            }
        }
        CheckSevereWeatherCandidates(tiles);
    }

    private float LandEvapotranspirationPotential(WorldTile tile, ClimateCell cell,
        float heat01, float localSummer)
    {
        if (tile?.Type == null || tile.main_type?.ocean == true || tile.Type.lava ||
            IsFrozenClimateSurface(tile) || IsSummit(tile)) return 0f;

        float storedMoisture = Mathf.Clamp01(Mathf.InverseLerp(0.32f, 0.88f, cell.Humidity));
        float vegetation = Mathf.Clamp01(GetSurfaceMoistureRetention(tile));
        float availableEnergy = Mathf.Clamp01(cell.Sunlight * 0.72f + heat01 * 0.48f);
        float growingSeason = Mathf.Lerp(0.72f, 1.20f, localSummer);
        // 裸地也存在少量直接蒸发，枫林、白桦林、雨林和沼泽则以蒸腾为主。
        return Mathf.Clamp01(storedMoisture * availableEnergy * growingSeason *
                             Mathf.Lerp(0.18f, 1.12f, vegetation));
    }

    private static bool CanSpawnClimateTornado(int tileCount)
    {
        if (!WorldLawLibrary.world_law_disasters_nature.isEnabled() || World.world?.stack_effects == null)
            return false;
        int maximum = Mathf.Clamp(tileCount / 16384, 1, 8);
        BaseEffectController controller = World.world.stack_effects.get("fx_tornado");
        return controller != null && controller.getList().Count < maximum;
    }

    private float LocalSummerStrength(WorldTile tile)
    {
        if (tile == null) return 0.5f;
        float signedLatitude = GetSignedLatitude(tile.pos.y);
        float absoluteLatitude = Mathf.Abs(signedLatitude);
        if (absoluteLatitude < 0.08f) return 0.5f;
        float yearPhase = (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f;
        float hemisphereSign = signedLatitude >= 0f ? 1f : -1f;
        return Mathf.Clamp01((Mathf.Sin(yearPhase) * hemisphereSign + 1f) * 0.5f);
    }

    private float SeasonalRainFactor(WorldTile tile)
    {
        if (tile == null) return 1f;
        float signedLatitude = GetSignedLatitude(tile.pos.y);
        float absoluteLatitude = Mathf.Abs(signedLatitude);
        float yearPhase = (_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f;
        float hemisphereSign = signedLatitude >= 0f ? 1f : -1f;
        // 春秋约为 1，夏季提高到 1.35，冬季降至 0.65；赤道不强行套用半球夏冬。
        float localSeasonSignal = absoluteLatitude < 0.08f
            ? 0f
            : Mathf.Sin(yearPhase) * hemisphereSign;
        float summerFactor = Mathf.Lerp(0.65f, 1.35f, (localSeasonSignal + 1f) * 0.5f);

        // 热带辐合带随太阳直射纬度移动。距离直射纬度约 29° 内形成雨季加成，
        // 赤道附近因此在春分和秋分前后形成两次明显雨季。
        float subsolarLatitude = CurrentSolarDeclinationRadians() / (Mathf.PI * 0.5f);
        float convergence = Mathf.Clamp01(1f - Mathf.Abs(signedLatitude - subsolarLatitude) / 0.32f);
        float tropicalWeight = Mathf.Clamp01(1f - absoluteLatitude / 0.48f);
        float rainSeasonFactor = Mathf.Lerp(0.85f, 1.45f, convergence * tropicalWeight);
        return Mathf.Clamp(summerFactor * rainSeasonFactor, 0.55f, 1.75f);
    }

    private WorldTile NearbyWeatherTile(WorldTile center, int radius)
    {
        if (center == null || radius <= 0) return center;
        int x = center.pos.x + UnityEngine.Random.Range(-radius, radius + 1);
        x=HorizontalWrap ? HorizontalTopology.Wrap(x,MapBox.width) : Mathf.Clamp(x,0,MapBox.width-1);
        int y = Mathf.Clamp(center.pos.y + UnityEngine.Random.Range(-radius, radius + 1), 0, MapBox.height - 1);
        return MapBox.instance.GetTileSimple(x, y) ?? center;
    }

    private void RegisterRainCloud(Cloud cloud)
    {
        if (cloud == null) return;
        _rainCloudStates.Remove(cloud);
        _rainCloudStates.Add(cloud, new RainCloudState
        {
            NextMoistureTime = Time.time + 0.6f,
            NextPhaseCheckTime = Time.time
        });
        if (!_activePrecipitationClouds.Contains(cloud)) _activePrecipitationClouds.Add(cloud);
    }

    private void PruneTrackedRainClouds()
    {
        for (int i = _activePrecipitationClouds.Count - 1; i >= 0; i--)
        {
            Cloud cloud = _activePrecipitationClouds[i];
            if (cloud != null && cloud.state == 1) continue;
            if (cloud != null) _rainCloudStates.Remove(cloud);
            _activePrecipitationClouds.RemoveAt(i);
        }
    }

    private void ClearTrackedRainClouds()
    {
        for (int i = 0; i < _activePrecipitationClouds.Count; i++)
        {
            Cloud cloud = _activePrecipitationClouds[i];
            if (cloud != null) _rainCloudStates.Remove(cloud);
        }
        _activePrecipitationClouds.Clear();
    }

    private string PrecipitationCloudType(WorldTile tile)
    {
        return _ageClimateProfile.ForceSnow ||
               (TryGetClimate(tile, out ClimateCell cell) && ClimateCelsius(cell.Temperature) <= 0f)
            ? "cloud_snow"
            : "cloud_rain";
    }

    internal void UpdatePrecipitationPhase(Cloud cloud, WorldTile landingTile)
    {
        if (cloud == null || landingTile == null ||
            !_rainCloudStates.TryGetValue(cloud, out RainCloudState state) ||
            Time.time < state.NextPhaseCheckTime) return;
        state.NextPhaseCheckTime = Time.time + 0.25f;
        string wanted = PrecipitationCloudType(landingTile);
        if (cloud.asset?.id != wanted) cloud.setType(wanted);
    }

    internal void ApplyRainfallMoisture(Cloud cloud, WorldTile landingTile)
    {
        if (AtmosphereReady) return; // Continuous precipitation owns the water budget.
        if (cloud == null || landingTile == null ||
            !_rainCloudStates.TryGetValue(cloud, out RainCloudState state) ||
            Time.time < state.NextMoistureTime) return;
        state.NextMoistureTime = Time.time + 1.5f;

        AddGroundMoisture(landingTile, 0.040f);
        for (int i = 0; i < ClimateNeighbourCount(landingTile); i++)
            AddGroundMoisture(ClimateNeighbour(landingTile,i), 0.018f);
    }

    private void AddGroundMoisture(WorldTile tile, float amount)
    {
        if (!TryGetClimate(tile, out ClimateCell cell)) return;
        // 陆地降水更直接补充土壤水汽；海洋维持原结算量。保留少量蒸发余量，
        // 避免长时间降雨把相对湿度永久锁死在 100%。
        bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;
        if (!ocean) amount *= LandRainfallMoistureMultiplier;
        cell.Humidity = Mathf.Min(0.98f, cell.Humidity + amount);
    }

    private bool IsCoastal(WorldTile tile)
    {
        if (tile == null) return false;
        bool land = tile.Type != null && tile.Type.layer_type != TileLayerType.Ocean;
        for (int i = 0; i < ClimateNeighbourCount(tile); i++)
        {
            WorldTile n = ClimateNeighbour(tile,i);
            if (n?.Type == null) continue;
            if ((n.Type.layer_type == TileLayerType.Ocean) == land) return true;
        }
        return false;
    }

    private static readonly HashSet<string> ManagedBiomes = new HashSet<string>
    {
        "biome_grass", "biome_maple", "biome_birch", "biome_jungle",
        "biome_swamp", "biome_desert", "biome_savanna", "biome_permafrost"
    };

    internal static HashSet<string> ManagedBiomesForPatch => ManagedBiomes;
    internal bool IsBiomeClimateAcceptableForPatch(WorldTile tile, string biomeId,
        float temperature, float humidity)
    {
        if (tile == null || string.IsNullOrEmpty(biomeId) || !ManagedBiomes.Contains(biomeId))
            return false;
        // 自然扩张只允许写入当前气候真正选中的群系。宽容区仅用于已经写入的
        // 群系防抖，不能作为继续向外扩张的许可，否则群系会越过气候边界。
        return IsBiomeWithinTolerance(tile, biomeId, temperature, humidity) &&
               SelectBiome(tile, temperature, humidity) == biomeId;
    }

    internal void AcceptExternalBiomeExpansion(WorldTile tile, string biomeId)
    {
        if (tile?.data == null || string.IsNullOrEmpty(biomeId) || !ManagedBiomes.Contains(biomeId)) return;
        int tileId = tile.data.tile_id;
        _acceptedBiomeExpansions[tileId] = biomeId;
        _biomeTransitionStates.Remove(tileId);
        int pixel = tile.pos.y * MapBox.width + tile.pos.x;
        if (pixel >= 0 && pixel < _cellIndexByPixel.Length)
        {
            int cellIndex = _cellIndexByPixel[pixel];
            if (cellIndex >= 0) InvalidateQueuedBiome(cellIndex);
        }
    }

    private void EvaluateClimateBiome(int cellIndex, WorldTile tile, ClimateCell c)
    {
        if (tile?.Type == null || tile.data == null || tile.main_type?.ocean == true)
        {
            InvalidateQueuedBiome(cellIndex);
            return;
        }
        int tileId = tile.data.tile_id;
        bool seedableBareSoil = IsClimateBiomeSeedableBareSoil(tile);
        string current = tile.Type.biome_asset?.id ?? string.Empty;
        if (!seedableBareSoil && (!tile.Type.is_biome || !ManagedBiomes.Contains(current)))
        {
            _biomeTransitionStates.Remove(tileId);
            _acceptedBiomeExpansions.Remove(tileId);
            InvalidateQueuedBiome(cellIndex);
            return;
        }
        if (!seedableBareSoil &&
            _acceptedBiomeExpansions.TryGetValue(tileId, out string acceptedBiome))
        {
            if (acceptedBiome == current && SelectBiome(tile, c.Temperature, c.Humidity) == current && IsBiomeWithinTolerance(
                    tile, current, c.Temperature, c.Humidity) &&
                HasExpectedBiomeTerrainVariant(tile, current))
            {
                _biomeTransitionStates.Remove(tileId);
                InvalidateQueuedBiome(cellIndex);
                return;
            }
            _acceptedBiomeExpansions.Remove(tileId);
        }
        string wanted = SelectBiome(tile, c.Temperature, c.Humidity);
        if (seedableBareSoil)
        {
            // 裸地由独立游标负责，不进入普通群系转换队列。
            _biomeTransitionStates.Remove(tileId);
            InvalidateQueuedBiome(cellIndex);
            return;
        }
        if (!seedableBareSoil && current == wanted && HasExpectedBiomeTerrainVariant(tile, wanted))
        {
            _biomeTransitionStates.Remove(tileId);
            InvalidateQueuedBiome(cellIndex);
            return;
        }
        if (!_biomeTransitionStates.TryGetValue(tileId, out BiomeTransitionState pending) ||
            pending.Candidate != wanted)
        {
            _biomeTransitionStates[tileId] = new BiomeTransitionState
            {
                Candidate = wanted,
                SinceSeasonTime = _seasonClock
            };
            InvalidateQueuedBiome(cellIndex);
            return;
        }
        if (_seasonClock - pending.SinceSeasonTime < BiomeTransitionDelay) return;
        if (_queuedBiomeCandidates.TryGetValue(cellIndex, out string queued) && queued == wanted) return;
        _queuedBiomeCandidates[cellIndex] = wanted;
        _pendingBiomeTransitions.Enqueue(new QueuedBiomeTransition
        {
            CellIndex = cellIndex,
            Candidate = wanted
        });
    }

    private void InvalidateQueuedBiome(int cellIndex)
    {
        // Queue<T> 不适合中途删除；移除字典标记即可让旧队列项在出队时失效。
        _queuedBiomeCandidates.Remove(cellIndex);
    }

    /// <summary>
    /// 没有 top tile 的普通土壤无法依靠自身参与原版群系扩张，因此由气候系统
    /// 主动播种。道路、农田或其他特殊顶层不属于“裸地”；岩石、树木和城市建筑
    /// 可以正常位于群系上方，因此不应阻止其下方土壤获得群系。
    /// </summary>
    private static bool IsClimateBiomeSeedableBareSoil(WorldTile tile)
    {
        if (tile?.data == null || tile.main_type == null || tile.Type == null ||
            tile.main_type.ocean || tile.main_type.liquid || tile.Type.lava ||
            tile.top_type != null)
            return false;
        return tile.main_type == TileLibrary.soil_low || tile.main_type == TileLibrary.soil_high;
    }

    private void QueueBareBiomeRepair(int cellIndex)
    {
        if (cellIndex < 0 || cellIndex >= _cells.Length ||
            !_queuedBareBiomeRepairs.Add(cellIndex)) return;
        _pendingBareBiomeRepairs.Enqueue(cellIndex);
    }

    /// <summary>
    /// 裸土不使用普通群系的 90 秒防抖队列。优先处理地形变更登记的格点，随后用
    /// 独立游标按气候遍历顺序（赤道到两极）扫描全图，确保遗漏的裸土也会被修复。
    /// </summary>
    private void RepairBareBiomeTiles(WorldTile[] tiles)
    {
        if (tiles == null || _climateTraversalOrder.Length == 0) return;

        int repaired = 0;
        int queuedAtStart = _pendingBareBiomeRepairs.Count;
        for (int i = 0; i < queuedAtStart && repaired < BareBiomeRepairsPerTick; i++)
        {
            int index = _pendingBareBiomeRepairs.Dequeue();
            _queuedBareBiomeRepairs.Remove(index);
            bool completed = TryRepairBareBiomeTile(index, tiles, out bool changed);
            if (!completed) QueueBareBiomeRepair(index);
            if (changed) repaired++;
        }

        int total = _climateTraversalOrder.Length;
        int scans = Math.Min(BareBiomeScansPerTick, total);
        for (int n = 0; n < scans; n++)
        {
            int orderPosition = (_bareBiomeCursor + n) % total;
            int index = _climateTraversalOrder[orderPosition];
            if (index < 0 || index >= tiles.Length || !IsClimateBiomeSeedableBareSoil(tiles[index]))
                continue;
            if (repaired >= BareBiomeRepairsPerTick)
            {
                QueueBareBiomeRepair(index);
                continue;
            }

            bool completed = TryRepairBareBiomeTile(index, tiles, out bool changed);
            if (!completed) QueueBareBiomeRepair(index);
            if (changed) repaired++;
        }
        _bareBiomeCursor = (_bareBiomeCursor + scans) % total;
    }

    /// <summary>
    /// 直接设置群系 top tile，避免 terraform 特效/动作队列使裸土写入丢失；写后同时
    /// 校验贴图类型和群系资产。返回 false 表示地块仍是裸土，需要下周期重试。
    /// </summary>
    private bool TryRepairBareBiomeTile(int index, WorldTile[] tiles, out bool changed)
    {
        changed = false;
        if (index < 0 || index >= tiles.Length || index >= _cells.Length) return true;
        WorldTile tile = tiles[index];
        ClimateCell cell = _cells[index];
        if (!IsClimateBiomeSeedableBareSoil(tile) || cell == null) return true;

        string wanted = SelectBiome(tile, cell.Temperature, cell.Humidity);
        BiomeAsset biome = AssetManager.biome_library.get(wanted);
        TopTileType top = GetBiomeTopForTerrain(biome, tile);
        if (top == null) return false;

        _acceptedBiomeExpansions.Remove(tile.data.tile_id);
        _biomeTransitionStates.Remove(tile.data.tile_id);
        InvalidateQueuedBiome(index);
        try
        {
            _applyingClimateBiome = true;
            tile.setTopTileType(top, true);
        }
        finally { _applyingClimateBiome = false; }

        bool success = tile.top_type?.id == top.id && tile.Type?.biome_asset?.id == wanted;
        if (!success) return false;
        changed = true;
        Fields?.MarkSurfaceDirty();
        ApplyPolarFreezing(tile, cell);
        return true;
    }

    private void ApplyQueuedBiomeTransitions(WorldTile[] tiles, int budget)
    {
        int applied = 0;
        int inspected = 0;
        int inspectionBudget = Math.Max(TilesPerTick, budget * 8);
        while (applied < budget && inspected < inspectionBudget && _pendingBiomeTransitions.Count > 0)
        {
            QueuedBiomeTransition queued = _pendingBiomeTransitions.Dequeue();
            inspected++;
            int index = queued.CellIndex;
            if (!_queuedBiomeCandidates.TryGetValue(index, out string currentCandidate) ||
                currentCandidate != queued.Candidate) continue;
            _queuedBiomeCandidates.Remove(index);
            if (index < 0 || index >= tiles.Length || index >= _cells.Length) continue;

            WorldTile tile = tiles[index];
            ClimateCell cell = _cells[index];
            if (tile?.Type == null || tile.data == null || cell == null ||
                tile.main_type?.ocean == true) continue;
            bool seedableBareSoil = IsClimateBiomeSeedableBareSoil(tile);
            if (seedableBareSoil)
            {
                QueueBareBiomeRepair(index);
                _biomeTransitionStates.Remove(tile.data.tile_id);
                continue;
            }
            string current = tile.Type.biome_asset?.id ?? string.Empty;
            if (!tile.Type.is_biome || !ManagedBiomes.Contains(current))
            {
                _biomeTransitionStates.Remove(tile.data.tile_id);
                continue;
            }

            string wanted = SelectBiome(tile, cell.Temperature, cell.Humidity);
            if (wanted != queued.Candidate ||
                (current == wanted && HasExpectedBiomeTerrainVariant(tile, wanted)))
            {
                _biomeTransitionStates.Remove(tile.data.tile_id);
                continue;
            }
            BiomeAsset biome = AssetManager.biome_library.get(wanted);
            TopTileType top = GetBiomeTopForTerrain(biome, tile);
            if (top == null) continue;
            _acceptedBiomeExpansions.Remove(tile.data.tile_id);
            try
            {
                _applyingClimateBiome = true;
                MapAction.terraformTop(tile, top, AssetManager.terraform.get("flash"));
            }
            finally { _applyingClimateBiome = false; }
            _biomeTransitionStates.Remove(tile.data.tile_id);
            // 群系替换可能重设 frozen 状态，因此对已执行修改的地块重新应用冰雪。
            ApplyPolarFreezing(tile, cell);
            applied++;
        }
    }

    /// <summary>
    /// 复用原版规则：soil_low 选择 tile_low，soil_high 选择 tile_high。
    /// 对无排名的特殊主地块保留低地回退，避免动态/第三方群系资产为空。
    /// </summary>
    private static TopTileType GetBiomeTopForTerrain(BiomeAsset biome, WorldTile tile)
    {
        if (biome == null || tile == null) return null;
        TopTileType top = biome.getTile(tile);
        if (top != null) return top;
        if (tile.main_type?.rank_type == TileRank.High) return biome.getTileHigh();
        return biome.getTileLow() ?? biome.getTileHigh();
    }

    private static bool HasExpectedBiomeTerrainVariant(WorldTile tile, string biomeId)
    {
        if (tile == null || string.IsNullOrEmpty(biomeId)) return false;
        BiomeAsset biome = AssetManager.biome_library.get(biomeId);
        TopTileType expected = GetBiomeTopForTerrain(biome, tile);
        return expected != null && tile.top_type?.id == expected.id;
    }


    private void RefreshClimateAverages(bool force = false)
    {
        if (!force && Time.unscaledTime < _nextAverageRefresh) return;
        if (!force && _averageFrame == Time.frameCount) return;
        _averageFrame = Time.frameCount;
        WorldTile[] tiles = World.world?.tiles_list;
        if (tiles == null || tiles.Length != _cells.Length || _cells.Length == 0) return;
        if (force) _averageCursor = 0;
        if (_averageCursor == 0)
        {
            _averageTemperatureSum = _averageHumiditySum = 0d;
            _averageTemperatureCount = _averageLandCount = 0;
        }
        int end = force ? tiles.Length : Math.Min(tiles.Length, _averageCursor + 16384);
        for (int i = _averageCursor; i < end; i++)
        {
            WorldTile tile = tiles[i];
            ClimateCell cell = _cells[i];
            if (tile == null || cell == null) continue;
            _averageTemperatureSum += cell.Temperature;
            _averageTemperatureCount++;
            bool ocean = tile.main_type?.ocean == true || tile.Type?.layer_type == TileLayerType.Ocean;
            if (ocean) continue;
            _averageHumiditySum += cell.Humidity;
            _averageLandCount++;
        }

        _averageCursor = end;
        if (end < tiles.Length) return;
        _averageTemperature = _averageTemperatureCount > 0 ? (float)(_averageTemperatureSum / _averageTemperatureCount) : 0f;
        _averageLandHumidity = _averageLandCount > 0 ? (float)(_averageHumiditySum / _averageLandCount) : 0f;
        _averageCursor = 0;
        _nextAverageRefresh = Time.unscaledTime + 2f;
    }

    internal bool TryGetClimate(WorldTile tile, out ClimateCell cell)
    {
        cell = null;
        if (tile == null || _cellIndexByPixel.Length == 0) return false;
        int pixel = tile.pos.y * MapBox.width + tile.pos.x;
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return false;
        int index = _cellIndexByPixel[pixel];
        if (index < 0 || index >= _cells.Length) return false;
        cell = _cells[index];
        return true;
    }

    internal bool TryGetWindTarget(WorldTile tile, bool waterOnly, int distance, out WorldTile target)
    {
        target = null;
        if (!TryGetClimate(tile, out ClimateCell cell) || cell.Wind.sqrMagnitude < 0.001f) return false;
        int x = Mathf.RoundToInt(tile.pos.x + cell.Wind.x * distance);
        x=HorizontalWrap && !waterOnly ? HorizontalTopology.Wrap(x,MapBox.width) : Mathf.Clamp(x,0,MapBox.width-1);
        int y = Mathf.Clamp(Mathf.RoundToInt(tile.pos.y + cell.Wind.y * distance), 0, MapBox.height - 1);
        WorldTile candidate = MapBox.instance.GetTileSimple(x, y);
        if (candidate == null) return false;
        if (waterOnly && !candidate.isGoodForBoat())
        {
            // 沿风向逐格回退，船只永远不会被局地风目标推上陆地。
            for (int d = distance - 1; d >= 1; d--)
            {
                x = Mathf.Clamp(Mathf.RoundToInt(tile.pos.x + cell.Wind.x * d), 0, MapBox.width - 1);
                y = Mathf.Clamp(Mathf.RoundToInt(tile.pos.y + cell.Wind.y * d), 0, MapBox.height - 1);
                candidate = MapBox.instance.GetTileSimple(x, y);
                if (candidate?.isGoodForBoat() == true) break;
            }
            if (candidate?.isGoodForBoat() != true) return false;
        }
        target = candidate;
        return true;
    }

    internal Vector2 GetCloudWind(Cloud cloud, WorldTile tile, ClimateCell cell)
    {
        bool rainCloud = cloud != null && _rainCloudStates.TryGetValue(cloud, out _);
        float driftMultiplier = rainCloud ? 1.12f : 1f;
        Vector2 baseWind = cell.Wind * Mathf.Lerp(0.55f, 1.55f, cell.WindSpeed) * driftMultiplier;
        if (tile == null || cell.Wind.sqrMagnitude < 0.001f) return baseWind;
        Vector2 direction = cell.Wind.normalized;
        WorldTile barrier = null;
        for (int distance = 1; distance <= 3; distance++)
        {
            int x = Mathf.RoundToInt(tile.pos.x + direction.x * distance);
            int y = Mathf.RoundToInt(tile.pos.y + direction.y * distance);
            WorldTile candidate = CloudTerrainSample(x, y);
            if (IsSummit(candidate)) { barrier = candidate; break; }
        }
        if (barrier == null) return baseWind;

        // 山峰在云的下风方时，比较左右两条绕行路径，选择较低一侧。
        Vector2 left = new Vector2(-direction.y, direction.x);
        Vector2 right = -left;
        WorldTile leftTile = CloudTerrainSample(
            Mathf.RoundToInt(tile.pos.x + left.x * 2f),Mathf.RoundToInt(tile.pos.y + left.y * 2f));
        WorldTile rightTile = CloudTerrainSample(
            Mathf.RoundToInt(tile.pos.x + right.x * 2f),Mathf.RoundToInt(tile.pos.y + right.y * 2f));
        float leftElevation = GetTerrainElevation(leftTile);
        float rightElevation = GetTerrainElevation(rightTile);
        Vector2 detour = leftElevation <= rightElevation ? left : right;
        float blockStrength = IsSnowPeak(barrier) ? 0.88f : 0.68f;
        Vector2 redirected = Vector2.Lerp(direction, detour, blockStrength).normalized;
        float remainingSpeed = Mathf.Lerp(0.42f, 0.16f, blockStrength);
        return redirected * baseWind.magnitude * remainingSpeed;
    }

    internal void RestoreSuitableBiome(WorldTile tile, ClimateCell cell)
    {
        if (tile?.data != null) _acceptedBiomeExpansions.Remove(tile.data.tile_id);
        string wanted = SelectBiome(tile, cell.Temperature, cell.Humidity);
        BiomeAsset biome = AssetManager.biome_library.get(wanted);
        TopTileType top = GetBiomeTopForTerrain(biome, tile);
        if (top != null) MapAction.terraformTop(tile, top, AssetManager.terraform.get("flash"));
    }

    private void OnGUI()
    {
        if (_cells.Length == 0) return;
        // WorldBox 的滚动/模态窗口由独立 Canvas 绘制；本模组的 OnGUI 层级在其后。
        // 任意窗口打开时暂停全部覆盖层，避免夜幕、图层和提示框压暗弹窗。
        if (ClimateUiLayout.NativePopupVisible) return;
        Bench.bench("mod.OnGUI", "cpu");
        Bench.bench("mod.OverlayText", "cpu");
        DrawOverlayText();
        Bench.benchEnd("mod.OverlayText", "cpu", false, 0);
        if (!_showPanel) { Bench.benchEnd("mod.OnGUI", "cpu", false, 0); return; }
        Matrix4x4 previousMatrix=GUI.matrix;
        GUI.BeginGroup(ClimateUiLayout.Gameplay);
        float panelScale=ClimateUiLayout.PanelScale;
        GUI.matrix=Matrix4x4.Scale(new Vector3(panelScale,panelScale,1));
        try
        {
        GUI.depth = -10;
        RefreshClimateAverages();
        string season = SeasonName();
        GUI.Box(new Rect(8, 80, 285, 550), "气候与四季 [F8] · " +
            (_gpuAtmosphere == null ? "CPU" : _gpuFirstSnapshot ? "GPU" : "GPU启动中"));
        GUI.Label(new Rect(18, 105, 225, 22), $"北半球：{season}  南半球：{OppositeSeasonName()}");
        GUI.Label(new Rect(18, 127, 255, 22), $"平均温度：{FormatTemperature(_averageTemperature)}  陆地湿度：{_averageLandHumidity:P0}");
        GUI.Label(new Rect(18, 149, 265, 22), $"累计：雨云 {_rainEvents} 雷暴 {_stormEvents} 龙卷 {_tornadoEvents}");
        GUI.Label(new Rect(18, 555, 270, 22), $"热带系统：累计生成 {_cycloneTotalEvents} / 当前活跃 {ActiveCycloneCount()}");
        GUI.Label(new Rect(18, 577, 270, 22), $"阶段去重：低压 {_depressionEvents} / 风暴 {_tropicalStormEvents}");
        GUI.Label(new Rect(18, 599, 270, 22), $"累计命名：台风 {_typhoonEvents} 飓风 {_hurricaneEvents} 气旋 {_tropicalCycloneEvents}");
        GUI.Label(new Rect(18, 171, 270, 22), $"F3 云 F4 高 F5 风 F6 温 F7 湿  当前：{LayerName()}");
        GUI.Label(new Rect(18, 193, 55, 22), "模板：");
        if (GUI.Button(new Rect(68, 193, 58, 24), TemplateButtonName(ClimateTemplate.NorthernHemisphere)))
            SelectTemplate(ClimateTemplate.NorthernHemisphere);
        if (GUI.Button(new Rect(130, 193, 58, 24), TemplateButtonName(ClimateTemplate.SouthernHemisphere)))
            SelectTemplate(ClimateTemplate.SouthernHemisphere);
        if (GUI.Button(new Rect(192, 193, 58, 24), TemplateButtonName(ClimateTemplate.Global)))
            SelectTemplate(ClimateTemplate.Global);
        GUI.Label(new Rect(18, 222, 55, 22), "温标：");
        if (GUI.Button(new Rect(68, 220, 120, 25), _temperatureUnit == TemperatureUnit.Celsius ? "摄氏 °C" : "华氏 °F"))
            ToggleTemperatureUnit();
        bool riverBusy = _pendingRiverPixels.Count > 0;
        string riverButtonText = riverBusy
            ? $"生成中 {_pendingRiverPixels.Count}"
            : Time.unscaledTime < _riverButtonStatusUntil ? _riverButtonStatus : "生成河流";
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !riverBusy;
        if (GUI.Button(new Rect(192, 220, 76, 25), riverButtonText)) TriggerRiverGeneration();
        GUI.enabled = previousEnabled;
        float greenhouseCelsius = _greenhouseGasLevel * 33f;
        GUI.Label(new Rect(18, 249, 255, 22), $"温室气体：{_greenhouseGasLevel * 100f:F0}%  (+{greenhouseCelsius:F1} °C)");
        float greenhouseValue = GUI.HorizontalSlider(new Rect(18, 274, 250, 18), _greenhouseGasLevel, 0f, 2f);
        SetGreenhouseGasLevel(greenhouseValue);
        GUI.Label(new Rect(18, 289, 45, 18), "0%");
        GUI.Label(new Rect(232, 289, 48, 18), "200%");
        GUI.Label(new Rect(18, 311, 265, 22),
            $"大气厚度：{_atmosphereThickness * 100f:F0}%  光{SurfaceLightTransmission() * 100f:F0}%  热{SurfaceSolarHeatTransmission() * 100f:F0}%");
        float atmosphereValue = GUI.HorizontalSlider(new Rect(18, 336, 250, 18),
            _atmosphereThickness, 0f, 2f);
        SetAtmosphereThickness(atmosphereValue);
        GUI.Label(new Rect(18, 351, 45, 18), "0%");
        GUI.Label(new Rect(232, 351, 48, 18), "200%");

        GUI.Label(new Rect(18, 373, 250, 20), $"纬度南界：{FormatCoordinate(_latitudeMinDegrees, true)}");
        SetLatitudeMinimum(GUI.HorizontalSlider(new Rect(18, 394, 250, 18),
            _latitudeMinDegrees, -90f, _latitudeMaxDegrees - 5f));
        GUI.Label(new Rect(18, 416, 250, 20), $"纬度北界：{FormatCoordinate(_latitudeMaxDegrees, true)}");
        SetLatitudeMaximum(GUI.HorizontalSlider(new Rect(18, 437, 250, 18),
            _latitudeMaxDegrees, _latitudeMinDegrees + 5f, 90f));
        GUI.Label(new Rect(18, 459, 250, 20), $"经度西界：{FormatCoordinate(_longitudeMinDegrees, false)}");
        SetLongitudeMinimum(GUI.HorizontalSlider(new Rect(18, 480, 250, 18),
            _longitudeMinDegrees, -180f, _longitudeMaxDegrees - 10f));
        GUI.Label(new Rect(18, 502, 250, 20), $"经度东界：{FormatCoordinate(_longitudeMaxDegrees, false)}");
        SetLongitudeMaximum(GUI.HorizontalSlider(new Rect(18, 523, 250, 18),
            _longitudeMaxDegrees, _longitudeMinDegrees + 10f, 180f));
        TrackCoordinateSliderInteraction();
        }
        finally { GUI.matrix=previousMatrix; GUI.EndGroup(); Bench.benchEnd("mod.OnGUI", "cpu", false, 0); }
    }

    internal bool IsPointerOnClimatePanel => _showPanel && ClimateUiLayout.PointerInPanel;
    /// <summary>
    /// IMGUI 只保留文字类覆盖内容：图例、经纬标签、气压带标注与悬停提示框。
    /// 地图上的全部图形（图层纹理、夜幕、等压线、风尾迹、参考线）已由
    /// ClimateLayerRenderer 的世界空间渲染体绘制。
    /// </summary>
    private void DrawOverlayText()
    {
        // 回绕窗口合成时地图由切片相机呈现，世界空间图层随之进入各分片；
        // 文字标注跟随主投影没有意义，仅保留提示框。
        if (HorizontalCameraWrap.Active?.Running == true)
        {
            if (_visibleLayer != ClimateLayer.None)
            {
                GUI.depth = -50;
                DrawMouseClimateTooltip(Camera.main, HorizontalCameraWrap.Active.MapWindow);
            }
            return;
        }
        Camera camera = Camera.main;
        if (camera == null) return;
        Rect mapRect = ComputeMapRect(camera);
        if (mapRect.width <= 1f || mapRect.height <= 1f) return;
        GUI.depth = 1000;

        if (_visibleLayer == ClimateLayer.Temperature)
        {
            DrawLatitudeLongitudeLabels(mapRect);
            DrawSubsolarLabel(mapRect);
        }
        if (_visibleLayer == ClimateLayer.Wind) DrawWindBeltLabels(mapRect);
        if (_visibleLayer == ClimateLayer.None) return;

        DrawLegend();
        Rect interactiveMapRect = IntersectRects(mapRect, ClimateUiLayout.Gameplay);
        DrawMouseClimateTooltip(camera, interactiveMapRect);
    }

    private static Rect ComputeMapRect(Camera camera)
    {
        // 地图左下角与右上角投影到屏幕；GUI 原点在左上，需要翻转屏幕 Y。
        Vector3 bottomLeft = camera.WorldToScreenPoint(new Vector3(0f, 0f, 0f));
        Vector3 topRight = camera.WorldToScreenPoint(new Vector3(MapBox.width, MapBox.height, 0f));
        float left = Mathf.Min(bottomLeft.x, topRight.x);
        float right = Mathf.Max(bottomLeft.x, topRight.x);
        float top = Screen.height - Mathf.Max(bottomLeft.y, topRight.y);
        float bottom = Screen.height - Mathf.Min(bottomLeft.y, topRight.y);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect IntersectRects(Rect a, Rect b)
    {
        float xMin = Mathf.Max(a.xMin, b.xMin);
        float yMin = Mathf.Max(a.yMin, b.yMin);
        float xMax = Mathf.Min(a.xMax, b.xMax);
        float yMax = Mathf.Min(a.yMax, b.yMax);
        return xMax <= xMin || yMax <= yMin ? new Rect(0f, 0f, 0f, 0f) :
            Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private void DrawLegend()
    {
        Rect legend = new Rect(305, 82, 190, 42);
        string legendText = _visibleLayer == ClimateLayer.AirHumidity ? "空气相对湿度：橙（干）→ 蓝（湿）" :
            _visibleLayer == ClimateLayer.Rainfall ? "降水：橙（无）→ 蓝（强）；F7切换" :
            _visibleLayer == ClimateLayer.Temperature
            ? "温度：蓝（低）→ 绿 → 红（高）"
            : _visibleLayer == ClimateLayer.Humidity
                ? "土壤湿度：橙（干）→ 蓝（湿）；F7切换"
                : _visibleLayer == ClimateLayer.Clouds
                    ? "大气云量：透明（少）→ 白（浓密）"
                : _visibleLayer == ClimateLayer.Elevation
                    ? "等高：蓝（水深）→ 绿（低地）→ 白（峰顶）"
                    : "气压：蓝（低）→ 红（高）；流线随风移动";
        GUI.Box(legend, legendText);
    }

    private void DrawLatitudeLongitudeLabels(Rect mapRect)
    {
        Color oldColor = GUI.color;
        for (int longitude = Mathf.CeilToInt(_longitudeMinDegrees / 30f) * 30;
             longitude <= _longitudeMaxDegrees; longitude += 30)
        {
            if (longitude % 60 != 0) continue;
            float x = mapRect.x + Mathf.InverseLerp(_longitudeMinDegrees, _longitudeMaxDegrees, longitude) * mapRect.width;
            string suffix = longitude < 0 ? "W" : longitude > 0 ? "E" : "";
            GUI.color = new Color(1f, 1f, 1f, 0.82f);
            GUI.Label(new Rect(x + 3f, mapRect.y + 2f, 54f, 20f), $"{Mathf.Abs(longitude)}°{suffix}");
        }
        for (int latitude = Mathf.CeilToInt(_latitudeMinDegrees / 30f) * 30;
             latitude <= _latitudeMaxDegrees; latitude += 30)
        {
            float y = mapRect.y + (1f - LatitudeDegreesToMapY(latitude)) * mapRect.height;
            string suffix = latitude < 0 ? "S" : latitude > 0 ? "N" : "赤道";
            string label = latitude == 0 ? suffix : $"{Mathf.Abs(latitude)}°{suffix}";
            GUI.color = latitude == 0 ? new Color(1f, 0.88f, 0.28f, 0.95f) : new Color(1f, 1f, 1f, 0.86f);
            GUI.Label(new Rect(mapRect.x + 4f, y - 19f, 62f, 20f), label);
        }
        GUI.color = oldColor;
    }

    private void DrawSubsolarLabel(Rect mapRect)
    {
        float latitudeDegrees = CurrentSolarDeclinationRadians() * Mathf.Rad2Deg;
        float normalizedY = LatitudeDegreesToMapY(latitudeDegrees);
        bool insideLatitude = latitudeDegrees >= _latitudeMinDegrees && latitudeDegrees <= _latitudeMaxDegrees;
        float solarLongitude = NormalizeLongitudeDegrees(CurrentSubsolarLongitudeRadians() * Mathf.Rad2Deg);
        bool insideLongitude = TryLongitudeDegreesToMapX(solarLongitude, out float normalizedX);
        float screenX = mapRect.x + normalizedX * mapRect.width;
        float screenY = mapRect.y + (1f - normalizedY) * mapRect.height;

        float degrees = Mathf.Abs(latitudeDegrees);
        string hemisphere = degrees < 0.05f ? "赤道" :
            latitudeDegrees > 0f ? $"北纬 {degrees:F1}°" : $"南纬 {degrees:F1}°";
        string label = insideLatitude && insideLongitude
            ? $"☀ 太阳直射点  {hemisphere}"
            : $"☀ 直射点在范围外  {hemisphere}";
        float labelX = Mathf.Clamp(screenX + 14f, mapRect.x + 4f, mapRect.xMax - 180f);
        float labelY = Mathf.Clamp(screenY - 28f, mapRect.y + 4f, mapRect.yMax - 26f);
        GUI.Box(new Rect(labelX, labelY, 176f, 25f), label);
    }

    private void DrawWindBeltLabels(Rect mapRect)
    {
        float declination = CurrentSolarDeclinationRadians() * Mathf.Rad2Deg;
        DrawWindBeltLabel(mapRect, declination * 0.65f, "赤道低压带");
        DrawWindBeltLabel(mapRect, 30f + declination * 0.24f, "副热带高压带");
        DrawWindBeltLabel(mapRect, -30f + declination * 0.24f, "副热带高压带");
        DrawWindBeltLabel(mapRect, 60f + declination * 0.14f, "副极地低压带");
        DrawWindBeltLabel(mapRect, -60f + declination * 0.14f, "副极地低压带");
    }

    private void DrawWindBeltLabel(Rect mapRect, float latitude, string name)
    {
        if (latitude < _latitudeMinDegrees || latitude > _latitudeMaxDegrees) return;
        float y = mapRect.y + (1f - LatitudeDegreesToMapY(latitude)) * mapRect.height;
        GUI.color = new Color(1f, 1f, 1f, 0.88f);
        GUI.Label(new Rect(mapRect.x + 5f, y - 18f, 170f, 20f),
            $"{Mathf.Abs(latitude):F1}°{(latitude < -0.05f ? "S" : latitude > 0.05f ? "N" : "")}  {name}");
    }

    private void DrawMouseClimateTooltip(Camera camera, Rect mapRect)
    {
        // 悬停原版 UI（含本模组面板，经 isOverUI 补丁）时不画地图提示框，
        // 避免与原版提示框叠在一起；只有指向地图本身时才显示。
        if (World.world?.isOverUI() == true) return;
        Vector2 mouseGui = Event.current.mousePosition;
        if (!mapRect.Contains(mouseGui)) return;
        Vector3 mouseScreen = new Vector3(mouseGui.x, Screen.height - mouseGui.y, 0f);
        Vector3 world = camera.ScreenToWorldPoint(mouseScreen);
        int x = Mathf.FloorToInt(world.x);
        int y = Mathf.FloorToInt(world.y);
        if (HorizontalCameraWrap.Active?.Running == true) x = HorizontalTopology.Wrap(x,MapBox.width);
        if (x < 0 || y < 0 || x >= MapBox.width || y >= MapBox.height) return;
        int pixel = y * MapBox.width + x;
        if (pixel < 0 || pixel >= _cellIndexByPixel.Length) return;
        int cellIndex = _cellIndexByPixel[pixel];
        if (cellIndex < 0 || cellIndex >= _cells.Length) return;

        ClimateCell cell = _cells[cellIndex];
        WorldTile hoveredTile = World.world.tiles_list[cellIndex];
        string displayedTemperature = FormatTemperature(hoveredTile, cell.Temperature);
        float signedLatitude = GetSignedLatitude(y);
        float longitudeDegrees = LongitudeDegreesAt(x);
        float absoluteLatitude = Mathf.Abs(signedLatitude);
        string latitudeName = absoluteLatitude < 0.12f ? "赤道" :
            absoluteLatitude > 0.82f ? (signedLatitude > 0f ? "北极圈" : "南极圈") :
            (signedLatitude > 0f ? "北半球" : "南半球");
        string primary = _visibleLayer == ClimateLayer.AirHumidity ? $"空气相对湿度：{cell.RelativeHumidity:P1}" :
            _visibleLayer == ClimateLayer.Rainfall ? $"降水强度：{cell.RainRate * 1000f:F2}（模拟单位）" :
            _visibleLayer == ClimateLayer.Temperature
            ? $"温度：{displayedTemperature}"
            : _visibleLayer == ClimateLayer.Humidity
                ? $"湿度：{cell.Humidity:P1}"
                : _visibleLayer == ClimateLayer.Clouds
                    ? $"云量：{cell.CloudCover:P1}"
                : _visibleLayer == ClimateLayer.Elevation
                    ? $"相对高度：{ContourElevationAtPixel(pixel):P1}  等高带 {ContourBand(pixel)}"
                    : $"{WindBeltName(signedLatitude)}  气压：{cell.Pressure:F1} hPa" +
                      $"\n风向：{WindDirectionName(cell.Wind)}  {WindSpeedKmh(cell):F0} km/h";
        bool climateSeaIce = hoveredTile?.main_type?.ocean == true && IsFrozenSeaTop(hoveredTile.top_type);
        bool frozen = hoveredTile?.data?.frozen == true;
        string frozenLabel = climateSeaIce ? "  海冰" : frozen ? "  冰雪" : string.Empty;
        string valueText = primary + $"\n土壤：{cell.Humidity:P1}  地温：{displayedTemperature}" +
                           $"\n空气：{cell.RelativeHumidity:P1}  气温：{cell.AirTemperatureC:F1} °C" +
                           $"\n日照：{cell.Sunlight:P1}  {latitudeName}" +
                           $"\n经纬：{FormatCoordinate(signedLatitude * 90f, true)}  " +
                           $"{FormatCoordinate(longitudeDegrees, false)}" +
                           $"\n地块：({x}, {y})  当地：{LocalSeasonName(signedLatitude)}{frozenLabel}" +
                           $"\n类型：{hoveredTile?.main_type?.id ?? "无"} / " +
                           $"{hoveredTile?.top_type?.id ?? "无"} / " +
                           $"{hoveredTile?.Type?.biome_asset?.id ?? "无"}";
        const float boxWidth = 285f;
        float boxHeight = _visibleLayer == ClimateLayer.Wind ? 152f : 134f;
        float boxX = Mathf.Min(mouseGui.x + 16f, Screen.width - boxWidth - 8f);
        if (ClimateUiLayout.Gameplay.height<boxHeight || ClimateUiLayout.Gameplay.width<boxWidth) return;
        float boxY = Mathf.Max(0,Mathf.Min(mouseGui.y + 18f, ClimateUiLayout.Gameplay.yMax - boxHeight - 8f));
        GUI.Box(new Rect(boxX, boxY, boxWidth, boxHeight), valueText);
    }

    private string SeasonName()
    {
        return new[] { "春", "夏", "秋", "冬" }[(int)CurrentSeason];
    }

    private string CurrentAgeDisplayName()
    {
        switch (_currentAgeId)
        {
            case "age_hope": return "希望";
            case "age_sun": return "烈日";
            case "age_dark": return "黑暗";
            case "age_tears": return "泪水";
            case "age_moon": return "月亮";
            case "age_chaos": return "混沌";
            case "age_wonders": return "奇迹";
            case "age_ice": return "冰霜";
            case "age_ash": return "灰烬";
            case "age_despair": return "绝望";
            default: return string.IsNullOrEmpty(_currentAgeId) ? "未知" : _currentAgeId;
        }
    }

    private string OppositeSeasonName()
    {
        return new[] { "春", "夏", "秋", "冬" }[((int)CurrentSeason + 2) & 3];
    }

    private string LocalSeasonName(float signedLatitude)
    {
        if (Mathf.Abs(signedLatitude) < 0.12f)
        {
            float convergence = Mathf.Abs(Mathf.Sin((_seasonClock / (SeasonSeconds * 4f)) * Mathf.PI * 2f));
            return convergence < 0.55f ? "赤道湿季" : "赤道较干季";
        }
        return signedLatitude > 0f ? SeasonName() : OppositeSeasonName();
    }

    private string TemplateButtonName(ClimateTemplate template)
    {
        string name = template == ClimateTemplate.Global ? "全球" :
            template == ClimateTemplate.NorthernHemisphere ? "北半球" : "南半球";
        return _template == template ? "●" + name : name;
    }

    private string LayerName()
    {
        if (_visibleLayer == ClimateLayer.Elevation) return "等高";
        if (_visibleLayer == ClimateLayer.Wind) return "风向";
        if (_visibleLayer == ClimateLayer.Temperature) return "温度";
        if (_visibleLayer == ClimateLayer.Humidity) return "土壤湿度";
        if (_visibleLayer == ClimateLayer.AirHumidity) return "空气湿度";
        if (_visibleLayer == ClimateLayer.Rainfall) return "降水";
        if (_visibleLayer == ClimateLayer.Clouds) return "大气云图";
        return "关闭";
    }

    private int ContourBand(int pixel)
    {
        return Mathf.Clamp(Mathf.FloorToInt(ContourElevationAtPixel(pixel) * 12f) + 1, 1, 12);
    }

    private static float WindSpeedKmh(ClimateCell cell) => cell.WindSpeed * 100f;

    private static string WindDirectionName(Vector2 wind)
    {
        float angle = Mathf.Repeat(Mathf.Atan2(wind.y, wind.x) * Mathf.Rad2Deg + 360f, 360f);
        string[] directions = { "东", "东北", "北", "西北", "西", "西南", "南", "东南" };
        return directions[Mathf.RoundToInt(angle / 45f) & 7];
    }
}
