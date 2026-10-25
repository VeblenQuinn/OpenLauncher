param(
    # 默认检查仓库里打包的那份引擎
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OpenLauncher\Assets\cubiomes.dll')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Dll)) { Write-Output "FAIL: missing $Dll"; exit 1 }
$dir = Split-Path -Parent $Dll

$cs = @'
using System;
using System.Runtime.InteropServices;
public static class Ol
{
    const string D = "cubiomes.dll";
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern bool SetDllDirectory(string p);

    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr ol_gen_new();
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern void ol_gen_free(IntPtr g);
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern int ol_gen_sizeof();
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern int ol_newest_version();
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)] public static extern int ol_min_supported();
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ol_render(IntPtr g, int mc, int dim, ulong seed,
        int scale, int bx, int bz, int w, int h, int y, int[] outIds);
    [DllImport(D, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ol_biome_at(IntPtr g, int mc, int dim, ulong seed,
        int scale, int x, int y, int z);

    public static string Init(string dllDir) { SetDllDirectory(dllDir); return "ok"; }
}
'@
Add-Type -TypeDefinition $cs -Language CSharp

[void][Ol]::Init($dir)
Write-Output ("DLL_KB=" + [math]::Round((Get-Item $Dll).Length/1KB,0))
Write-Output ("sizeof_Generator=" + [Ol]::ol_gen_sizeof())
Write-Output ("MC_NEWEST=" + [Ol]::ol_newest_version() + " MC_MIN=" + [Ol]::ol_min_supported())

function RenderStats($gen, $mc, $seed, $scale, $w, $h, $dim) {
    $ids = New-Object int[] ($w * $h)
    $bx = -[int]($w / 2 * $scale)
    $bz = -[int]($h / 2 * $scale)
    $y = [Math]::Max(1, [int](64 / $scale))
    $rc = [Ol]::ol_render($gen, $mc, $dim, [uint64]$seed, $scale, $bx, $bz, $w, $h, $y, $ids)
    if ($rc -ne 0) { return "render_rc=$rc" }
    $set = @{}
    $min = 9999; $max = -9999
    foreach ($v in $ids) { $set[$v] = 1; if ($v -lt $min) { $min = $v }; if ($v -gt $max) { $max = $v } }
    return "kinds=$($set.Count) min=$min max=$max first=$($ids[0])"
}

$gen = [Ol]::ol_gen_new()
if ($gen -eq [IntPtr]::Zero) { Write-Output 'FAIL: ol_gen_new returned null'; exit 1 }
try {
    Write-Output ('26.3   seed=0    : ' + (RenderStats $gen 35 0 16 128 128 0))
    Write-Output ('26.3   seed=1234 : ' + (RenderStats $gen 35 1234 16 128 128 0))
    Write-Output ('1.21.3 seed=0    : ' + (RenderStats $gen 27 0 16 128 128 0))
    Write-Output ('1.18.2 seed=0    : ' + (RenderStats $gen 22 0 16 128 128 0))
    Write-Output ('26.3   nether    : ' + (RenderStats $gen 35 0 16 64 64 -1))
    $b = [Ol]::ol_biome_at($gen, 35, 0, [uint64]0, 4, 0, 16, 0)
    Write-Output ("biome_at(0,0) 26.3 = $b")
}
finally { [Ol]::ol_gen_free($gen) }

Write-Output 'PASS'
exit 0
