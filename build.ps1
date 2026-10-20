param([switch]$SelfContained)
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishRoot = Join-Path $projectRoot "publish"
$target = Join-Path $publishRoot "win-x64"
$stage = Join-Path $publishRoot (".stage-" + [Guid]::NewGuid().ToString("N"))
$backup = Join-Path $publishRoot (".previous-" + [Guid]::NewGuid().ToString("N"))
$exePath = Join-Path $target "AllinAIQuotaTray.exe"

foreach ($process in (Get-Process AllinAIQuotaTray -ErrorAction SilentlyContinue)) {
    if ($process.Path -eq $exePath) { throw "请先退出正在运行的 AllinAI Quota，再重新发布。" }
}

Push-Location $projectRoot
try {
    $selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    dotnet publish ./allinai-quota-tray-windows.csproj `
      -c Release -r win-x64 `
      --self-contained $selfContainedValue `
      -p:PublishSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    $expected = ([xml](Get-Content ./allinai-quota-tray-windows.csproj)).Project.PropertyGroup.Version
    $info = (Get-Item (Join-Path $stage "AllinAIQuotaTray.exe")).VersionInfo
    if ($info.ProductVersion.Split('+')[0] -ne $expected -or $info.FileVersion -ne "$expected.0") {
        throw "发布产物版本与项目版本不一致。"
    }
    if (Test-Path $target) { Move-Item $target $backup }
    try { Move-Item $stage $target }
    catch {
        if ((Test-Path $backup) -and -not (Test-Path $target)) { Move-Item $backup $target }
        throw
    }
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Write-Host "Published v$expected to $target"
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Pop-Location
}
