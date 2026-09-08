## 目录结构

- `ModClass.cs`、`Source/**.cs` —— 模组 C# 源码；`ClimateSystem` 是分部类，按功能拆在 `Source/ClimateSystem.*.cs`。
- `.UnityProject/` —— Unity 2022.3.60f1 工程，只放 shader（`.shader`）、compute（`.compute`）和打包脚本（`Assets/Editor/ClimateBundleBuilder.cs`）。
- `Gpu/climatelayers` —— AssetBundle 产物，运行时由 `Source/Render/ClimateShaderAssets.cs` 加载。
- `mod.json` —— 模组清单。

游戏源码参考./.GameSource

## 编译规则

1. 本地检查：`dotnet build ClimateWeather.csproj`。csproj 里 `<Compile Remove=".UnityProject\**" />` 把 Unity 工程排除在编译外。
2. 游戏运行时：NeoModLoader（NML）启动时递归编译模组文件夹里的所有 `.cs`，只有两类例外：
   - 文件名或目录名以 `.` 开头；
   - 目录名在 NML 黑名单里：`bin`、`obj`、`Properties`、`packages`、`packages.config`、`packages-lock.json`、`packages-lock.xml`、`GameResources`、`GameResourcesReplace`、`AssetBundles`。

约定：模组代码只能放根目录或 `Source/`；`.UnityProject` 里的内容不参与 NML 编译。

## shader / compute 发布流程

1. 关闭游戏。
2. 运行 `powershell -ExecutionPolicy Bypass -File build-shaders.ps1`，需要 Unity 2022.3.60f1（可用 `-UnityPath` 指定）。产物写到 `Gpu/climatelayers`。
3. 新增 compute 文件要同步加进 `ClimateBundleBuilder.cs` 的资产清单。

## 验证流程

1. 启动游戏（改了 C# 必须重启游戏才生效）。
2. 日志：`%USERPROFILE%\AppData\LocalLow\mkarpenko\WorldBox\Player.log`，模组日志前缀 `[ClimateWeather]` / `[ClimateWeather GPU]`。
3. 游戏内 F8 开面板，F3-F7 切换气候图层；性能测量用 `Bench.bench_enabled = true`，数据在 `Bench.getGroup("cpu")`。

## 代码约定

- 大气与地表温度在 GPU 模拟、直写显示纹理，CPU 分片回读供玩法逻辑使用；任一环节校验失败整体回退 CPU 批处理，回退无跳变。
- 逐格循环内不做字符串操作；群系/地形类型判断在初始化时打包成数值缓冲，地形变化时增量刷新。
- 注释写"为什么"，用中文。
