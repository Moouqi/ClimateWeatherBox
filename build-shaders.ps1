# Build the climate shader AssetBundle with Unity 2022.3.60f1 (batch mode).
# Output: Gpu/climatelayers
param(
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\2022.3.60f1\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $UnityPath)) {
    Write-Error "Unity not found: $UnityPath ; pass -UnityPath pointing to the 2022.3.60f1 Unity.exe"
}

$project = Join-Path $PSScriptRoot ".UnityProject"
$log = Join-Path $project "build.log"
# Unity.exe is a GUI-subsystem binary; "&" would return before the build finishes.
# Quote paths manually: -ArgumentList does not quote array items.
$proc = Start-Process -FilePath "`"$UnityPath`"" `
    -ArgumentList @("-batchmode", "-quit", "-nographics",
        "-projectPath", "`"$project`"",
        "-executeMethod", "ClimateBundleBuilder.Build",
        "-logFile", "`"$log`"") `
    -WindowStyle Hidden -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    # Some environments (restricted cache dirs) make Unity exit non-zero even
    # when the bundle itself was built; trust the artifact + build log instead.
    $bundle = Join-Path $PSScriptRoot "Gpu\climatelayers"
    $builtLog = Select-String -Path $log -Pattern "Shader bundle" -SimpleMatch -Quiet
    if (-not ((Test-Path $bundle) -and $builtLog)) {
        Get-Content $log -Tail 40
        Write-Error "Unity build failed with exit code $($proc.ExitCode)"
    }
    Write-Warning "Unity exited with code $($proc.ExitCode), but the bundle was built."
}
$bundle = Join-Path $PSScriptRoot "Gpu\climatelayers"
Write-Host ("Build OK: {0} bytes" -f (Get-Item $bundle).Length)
