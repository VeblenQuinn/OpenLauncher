<#
  build-cubiomes.ps1 —— 编译种子地图引擎（cubiomes.dll）

  为什么需要这个脚本：
    种子地图的群系计算由 cubiomes 完成。上游（Cubitect/cubiomes）只更新到
    1.21 Winter Drop，26.x 支持在社区分支（xpple / Conflux-Union）里。
    DLL 会一并编进 tools/cubiomes-shim/olshim.c 这层薄封装：cubiomes 的
    Generator 结构 sizeof 有 192200 字节，托管侧猜错大小就会写越界崩进程，
    所以结构体布局和内存生命周期都留在 C 侧。

  用法：
    powershell -ExecutionPolicy Bypass -File tools\build-cubiomes.ps1
    powershell -ExecutionPolicy Bypass -File tools\build-cubiomes.ps1 -Cc C:\path\to\zig.exe

  产物：release\cubiomes.dll
  之后把脚本打印的 SHA-256 填进 Branding.cs 的 EngineSha256，并上传到 EngineUrl。
#>
param(
    [string]$Cc,          # zig.exe 或 gcc.exe，自动探测
    [string]$Commit = 'f75f0a360e9ba3630478eecd0d5935f913e00ef9',   # 含 MC_26_3
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $repo 'release\cubiomes.dll' }
$work = Join-Path $env:TEMP 'cubiomes-build'
$src  = Join-Path $work 'src'

# ---------- 1. 找编译器 ----------
if (-not $Cc) {
    $cands = @(
        (Join-Path $env:TEMP 'zig\x\ziglang\zig.exe'),
        'C:\msys64\mingw64\bin\gcc.exe',
        'C:\mingw64\bin\gcc.exe',
        'C:\TDM-GCC-64\bin\gcc.exe'
    )
    foreach ($c in $cands) { if (Test-Path $c) { $Cc = $c; break } }
    if (-not $Cc) {
        foreach ($n in @('zig', 'gcc')) {
            $cmd = Get-Command $n -ErrorAction SilentlyContinue
            if ($cmd) { $Cc = $cmd.Source; break }
        }
    }
}
if (-not $Cc -or -not (Test-Path $Cc)) {
    Write-Host '找不到 C 编译器。三种办法：' -ForegroundColor Yellow
    Write-Host '  1) 装 MSYS2 / MinGW-w64，把 gcc 加进 PATH'
    Write-Host '  2) pip install ziglang（自带 zig，用 zig cc 当编译器）'
    Write-Host '  3) 用 -Cc 直接指定 zig.exe 或 gcc.exe 的完整路径'
    exit 2
}
$isZig = (Split-Path -Leaf $Cc) -like 'zig*'
Write-Host "编译器：$Cc $(if ($isZig) { '(zig cc)' } else { '(gcc)' })"

# ---------- 2. 取源码 ----------
New-Item -ItemType Directory -Force -Path $work | Out-Null
if (-not (Test-Path (Join-Path $src 'biomes.h'))) {
    $tar = Join-Path $work 'cubiomes.tar.gz'
    $url = "https://codeload.github.com/Conflux-Union/cubiomes/tar.gz/$Commit"
    Write-Host "下载源码：$url"
    curl.exe -L --ssl-no-revoke -o $tar $url
    if (-not (Test-Path $tar) -or (Get-Item $tar).Length -lt 10000) { throw '源码下载失败' }
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    tar.exe -xzf $tar -C $src --strip-components=1
}
if (-not (Test-Path (Join-Path $src 'biomes.h'))) { throw '源码解压失败' }

# ---------- 3. 编译 ----------
# 只编启动器用得到的部分，清单来自 Conflux-Union/conflux-map 的 native/CUBIOMES_COMMIT
$files = @(
    'biomenoise.c', 'biomes.c', 'generator.c', 'layers.c', 'noise.c',
    'terrainnoise.c', 'finders.c', 'terrain_features.c', 'util.c',
    'features\end_city.c', 'features\fortress.c', 'features\abandoned_camp.c',
    'features\stronghold.c', 'features\ore.c'
)
$paths = @()
foreach ($f in $files) {
    $p = Join-Path $src $f
    if (Test-Path $p) { $paths += $p } else { Write-Host "跳过缺失的 $f" -ForegroundColor DarkYellow }
}
$paths += Join-Path $PSScriptRoot 'cubiomes-shim\olshim.c'
if (-not (Test-Path $paths[-1])) { throw "找不到 $($paths[-1])" }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
Write-Host "编译 $($paths.Count) 个源文件 → $Out"

if ($isZig) {
    $zargs = @('cc','-target','x86_64-windows-gnu','-O2','-std=c99','-shared','-o',$Out) +
             $paths + @('-I.', "-I$src", '-Wl,--export-all-symbols')
    $p = Start-Process -FilePath $Cc -ArgumentList $zargs -WorkingDirectory $src -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput (Join-Path $work 'cc.out') -RedirectStandardError (Join-Path $work 'cc.err')
} else {
    $gargs = @('-O2','-std=c99','-shared','-o',$Out) + $paths + @('-I.', "-I$src", '-Wl,--export-all-symbols')
    $p = Start-Process -FilePath $Cc -ArgumentList $gargs -WorkingDirectory $src -NoNewWindow -Wait -PassThru `
         -RedirectStandardOutput (Join-Path $work 'cc.out') -RedirectStandardError (Join-Path $work 'cc.err')
}
$errText = Get-Content (Join-Path $work 'cc.err') -ErrorAction SilentlyContinue
if ($p.ExitCode -ne 0) {
    Write-Host '编译失败：' -ForegroundColor Red
    $errText | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
    exit 1
}
if ($errText) { $errText | Select-Object -Last 10 | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow } }

if (-not (Test-Path $Out)) { throw '没有生成 dll' }

# ---------- 4. 自检 ----------
$sha = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host ("已生成 $Out  (" + [math]::Round((Get-Item $Out).Length / 1KB, 0) + " KB)") -ForegroundColor Green
Write-Host "SHA-256: $sha"
Write-Host ''
Write-Host '跑一遍自检（会真正算一块地形）…'
$verify = Join-Path $PSScriptRoot 'verify-engine.ps1'
if (Test-Path $verify) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $verify -Dll $Out
    if ($LASTEXITCODE -ne 0) { Write-Host '自检未通过，先别发布。' -ForegroundColor Red; exit 1 }
}
Write-Host ''
Write-Host '接下来：'
Write-Host "  1) 把 dll 上传到 Branding.EngineUrl 指向的地址"
Write-Host "  2) 把下面的哈希填进 src/SquidLauncher/Branding.cs 的 EngineSha256"
Write-Host "     $sha"
Write-Host '  3) 本地试玩：直接复制到 <数据目录>\tools\cubiomes.dll'
