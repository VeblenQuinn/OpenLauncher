# 一键编译发布版：输出 dist\SquidLauncher.exe（单文件，Win10/11 自带 .NET Framework 4.8，双击即用）
# 需要 .NET SDK 8+（https://dotnet.microsoft.com/download），不需要 Visual Studio。
$ErrorActionPreference = 'Stop'
# 点来源（. .\build.ps1）时 $MyInvocation.MyCommand.Path 可能为空，优先用 $PSScriptRoot。
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not $dotnet) { throw '找不到 dotnet，请先安装 .NET SDK' }

$proj = Join-Path $root 'src\SquidLauncher\SquidLauncher.csproj'
$objDir = Join-Path $root 'src\SquidLauncher\obj'

function Invoke-Compile {
    $log = Join-Path $env:TEMP 'squidlauncher-build.log'
    # 原生命令的 stderr 在 EAP=Stop 下会当异常抛，这里临时放宽。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $dotnet build $proj -c Release -nologo -v m *> $log
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    $text = ''
    if (Test-Path $log) { $text = Get-Content $log -Raw }
    [pscustomobject]@{ Code = $code; Text = $text; Log = $log }
}

$result = Invoke-Compile

# BG1001/BG1002（找不到 *.baml）说明 obj 里的 WPF 中间产物坏了，通常是并发编译或上次中断留下的。
# 清掉 obj 重编一次即可，不需要动源码。
if ($result.Code -ne 0 -and $result.Text -match 'BG1001|BG1002|MSB3021|MSB3027|另一进程|another process') {
    Write-Host 'WPF 中间产物损坏（BG1001/BG1002），清理 obj 后重试……'
    Get-Process VBCSCompiler -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $objDir -Recurse -Force -ErrorAction SilentlyContinue
    $result = Invoke-Compile
}

if ($result.Code -ne 0) {
    Write-Host $result.Text
    throw "编译失败（完整日志：$($result.Log)）"
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\src\SquidLauncher\bin\Release\SquidLauncher.exe" $dist -Force

# 不再生成 SquidLauncher.exe.config：它原来的三块内容都已写在代码里
#   · 长路径开关  → App 的静态构造器 AppContext.SetSwitch
#   · 并发连接 256 → Http 的静态构造器 ServicePointManager.DefaultConnectionLimit
#   · supportedRuntime → 编译目标已是 net48，Win10/11 自带
# 清掉旧版本残留的那份，避免新旧同时跑时行为不一致
Remove-Item "$dist\SquidLauncher.exe.config" -Force -ErrorAction SilentlyContinue

$exe = Get-Item "$dist\SquidLauncher.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"已输出 $($exe.FullName)"
"大小 $([math]::Round($exe.Length / 1KB)) KB   SHA-256 $hash"
