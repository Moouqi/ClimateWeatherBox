using UnityEngine;

namespace ClimateWeather;

/// <summary>
/// 世界空间图层渲染器：夜幕面片（乘在数据图层之上）、数据图层面片、
/// 线条 Mesh 三件套，全部挂在游戏自带的 MapOverlay 排序层。
/// 副相机（接缝预览、回绕窗口）因此自动拍到全部气候视觉，无需再复制绘制路径。
/// </summary>
public sealed class ClimateLayerRenderer : MonoBehaviour
{
    internal static ClimateLayerRenderer Active { get; private set; }

    private const string SortingLayerName = "MapOverlay";
    private const int LayerOrder = -8;
    private const int LineOrder = -7;
    private const int NightOrder = -6;

    private GameObject _layerObject;
    private GameObject _lineObject;
    private GameObject _nightObject;
    private Material _layerMaterial;
    private Material _nightMaterial;
    private Material _lineMaterial;
    private readonly ClimateLineMesh _lines = new();
    private bool _assetsFailed;
    private bool _mapBuilt;
    private bool _sortedChecked;

    private void Awake()
    {
        Active = this;
    }

    private void OnDestroy()
    {
        if (Active == this) Active = null;
        if (_layerMaterial != null) Destroy(_layerMaterial);
        if (_nightMaterial != null) Destroy(_nightMaterial);
        if (_lineMaterial != null) Destroy(_lineMaterial);
        ClimateShaderAssets.Release();
    }

    /// <summary>世界尺寸变化（新世界/重载）时由 ClimateSystem.EnsureWorld 调用。</summary>
    internal void EnsureMap(int mapWidth, int mapHeight, int airWidth, int airHeight)
    {
        if (!_assetsFailed && _layerMaterial == null)
        {
            _layerMaterial = ClimateShaderAssets.CreateMaterial("ClimateWeather/Layers");
            _nightMaterial = ClimateShaderAssets.CreateMaterial("ClimateWeather/Night");
            _lineMaterial = ClimateShaderAssets.CreateMaterial("ClimateWeather/Lines");
            if (_layerMaterial == null || _nightMaterial == null || _lineMaterial == null)
            {
                _assetsFailed = true;
                Debug.LogWarning("[ClimateWeather] 世界空间渲染材质创建失败，图层与夜幕不可用");
                HideAll();
                return;
            }
        }
        if (_assetsFailed) return;

        RebuildQuad(ref _layerObject, "ClimateLayerQuad", _layerMaterial, mapWidth, mapHeight,
            LayerOrder, -8f);
        RebuildQuad(ref _nightObject, "ClimateNightQuad", _nightMaterial, mapWidth, mapHeight,
            NightOrder, -6f);
        RebuildLineObject();
        _lines.Reset();
        _mapBuilt = true;
    }

    private static void RebuildQuad(ref GameObject target, string name, Material material,
        int width, int height, int sortingOrder, float z)
    {
        if (target != null) Destroy(target);
        target = new GameObject(name);
        Mesh mesh = new Mesh { name = name };
        mesh.vertices = new[]
        {
            new Vector3(0f, 0f, z),
            new Vector3(width, 0f, z),
            new Vector3(width, height, z),
            new Vector3(0f, height, z),
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.bounds = new Bounds(new Vector3(width * 0.5f, height * 0.5f, 0f), new Vector3(width, height, 1f));
        target.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = target.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingLayerName = SortingLayerName;
        renderer.sortingOrder = sortingOrder;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        target.SetActive(false);
    }

    private void RebuildLineObject()
    {
        if (_lineObject != null) Destroy(_lineObject);
        _lineObject = new GameObject("ClimateLineMesh");
        _lineObject.AddComponent<MeshFilter>().sharedMesh = _lines.Mesh;
        MeshRenderer renderer = _lineObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _lineMaterial;
        renderer.sortingLayerName = SortingLayerName;
        renderer.sortingOrder = LineOrder;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        _lineObject.SetActive(false);
    }

    private void LateUpdate()
    {
        Bench.bench("mod.RenderLate", "cpu");
        try
        {
            UpdateRenderer();
        }
        finally
        {
            Bench.benchEnd("mod.RenderLate", "cpu", false, 0);
        }
    }

    private void UpdateRenderer()
    {
        ClimateSystem climate = ClimateSystem.Active;
        bool visible = _mapBuilt && !_assetsFailed && climate != null &&
                       climate.ReadyForRendering && World.world != null &&
                       !ClimateUiLayout.NativePopupVisible;
        if (!visible)
        {
            HideAll();
            return;
        }

        if (!_sortedChecked)
        {
            _sortedChecked = true;
            Debug.Log("[ClimateWeather] 世界空间渲染启用：MapOverlay=" +
                      (SortingLayer.NameToID(SortingLayerName) != 0) +
                      "，图层顺序 " + LayerOrder + "/" + LineOrder + "/" + NightOrder);
        }

        ClimateLayer layer = climate.VisibleLayer;
        climate.Fields.ConfigureAtmosphereWrap(climate.HorizontalWrap);
        bool layerVisible = layer != ClimateLayer.None;
        bool linesVisible = layer is ClimateLayer.Wind or ClimateLayer.Temperature;
        if (_layerObject != null) _layerObject.SetActive(layerVisible);
        if (_lineObject != null) _lineObject.SetActive(linesVisible);
        if (_nightObject != null) _nightObject.SetActive(true);

        if (layerVisible && _layerMaterial != null)
        {
            _layerMaterial.SetInt("_LayerMode", LayerModeIndex(layer));
            _layerMaterial.SetTexture("_SurfaceA", climate.Fields.SurfaceA);
            _layerMaterial.SetTexture("_SurfaceB", climate.Fields.SurfaceB);
            _layerMaterial.SetTexture("_Atmos", climate.Fields.Atmos);
        }
        if (_nightMaterial != null)
        {
            _nightMaterial.SetTexture("_LightMask", climate.Fields.LightMask);
            _nightMaterial.SetFloat("_Declination", climate.DeclinationRadians);
            _nightMaterial.SetFloat("_SubsolarLon", climate.SubsolarLongitudeRadians);
            _nightMaterial.SetFloat("_LatMin", climate.LatitudeMinDegrees);
            _nightMaterial.SetFloat("_LatMax", climate.LatitudeMaxDegrees);
            _nightMaterial.SetFloat("_LonMin", climate.LongitudeMinDegrees);
            _nightMaterial.SetFloat("_LonMax", climate.LongitudeMaxDegrees);
            _nightMaterial.SetFloat("_NightAlpha", climate.NightAlphaUniform);
            _nightMaterial.SetFloat("_DayDim", climate.DayDimUniform);
        }
        if (linesVisible)
        {
            if (layer == ClimateLayer.Wind) _lines.AdvanceWind(climate);
            _lines.Rebuild(Camera.main, climate, layer);
        }
    }

    /// <summary>模拟枚举到 shader 模式索引：0 温度 1 土壤 2 空气湿度 3 降水 4 云 5 等高 6 气压。</summary>
    private static int LayerModeIndex(ClimateLayer layer)
    {
        return layer switch
        {
            ClimateLayer.Temperature => 0,
            ClimateLayer.Humidity => 1,
            ClimateLayer.AirHumidity => 2,
            ClimateLayer.Rainfall => 3,
            ClimateLayer.Clouds => 4,
            ClimateLayer.Elevation => 5,
            ClimateLayer.Wind => 6,
            _ => 0,
        };
    }

    private void HideAll()
    {
        if (_layerObject != null) _layerObject.SetActive(false);
        if (_lineObject != null) _lineObject.SetActive(false);
        if (_nightObject != null) _nightObject.SetActive(false);
    }
}
