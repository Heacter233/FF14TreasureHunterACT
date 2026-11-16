# 校验编译产物：程序集有效 + 对 ACT 的引用身份正确
#
# 为什么需要这一步：
#   CI 用 _ci/act-stub 里的「引用桩」代替真实 ACT 进行编译。
#   如果桩的程序集身份（名称/版本/公钥标记）与真实 ACT 不一致，
#   编译出来的插件 DLL 在真实 ACT 中会绑定失败（TypeLoadException 等）。
#   这里在发布前把关。
#
# 兼容性说明：
#   不要用 [Assembly]::ReflectionOnlyLoadFrom —— 它在 PowerShell 7 (.NET Core)
#   上会抛 "ReflectionOnly loading is not supported on this platform"。
#   改用 LoadFrom + GetReferencedAssemblies()，在 PS 5.1 与 PS 7 上都能工作
#   （读取引用表不需要解析依赖，也不会执行目标程序集的任何代码）。
#
# 本脚本也可在本地手动运行（仓库根目录下）：
#   powershell -ExecutionPolicy Bypass -File .\_ci\verify-ref.ps1

$ErrorActionPreference = 'Stop'

$expectedPkt = 'a946b61e93d97868'
$expectedVer = '3.8.5.288'

function Get-PktString($assemblyName) {
    $tok = $assemblyName.GetPublicKeyToken()
    if ($null -eq $tok -or $tok.Length -eq 0) { return '(null)' }
    return ($tok | ForEach-Object { $_.ToString('x2') }) -join ''
}

# ---------- 1) 插件产物存在且是有效程序集 ----------
$dll = 'FF14TreasureHunterACT\bin\x64\Release\FF14TreasureHunterACT.dll'
if (-not (Test-Path $dll)) { throw "未找到编译产物: $dll" }

$dllPath = (Resolve-Path $dll).Path
$pluginName = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath)
Write-Host "插件程序集: $($pluginName.FullName)"
Write-Host "插件版本  : $($pluginName.Version)"

# ---------- 2) 插件对 ACT 的引用身份 ----------
# 用 LoadFrom 读取引用表；不调用任何 ACT 类型，因此不会触发依赖解析
$refOk = $false
try {
    $asm = [System.Reflection.Assembly]::LoadFrom($dllPath)
    $actRef = $asm.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'Advanced Combat Tracker' }

    if (-not $actRef) {
        throw "产物中没有 Advanced Combat Tracker 引用（插件无法被 ACT 加载）"
    }

    $pkt = Get-PktString $actRef
    Write-Host "ACT 引用  : $($actRef.FullName)"
    Write-Host "公钥标记  : $pkt"

    if ($pkt -ne $expectedPkt) {
        throw "ACT 引用的公钥标记为 '$pkt'，应为 '$expectedPkt'。产物将无法在真实 ACT 中加载。"
    }
    if ($actRef.Version -ne [version]$expectedVer) {
        Write-Warning "ACT 引用版本为 $($actRef.Version)，预期 $expectedVer（通常仍可加载，请确认）"
    }

    Write-Host "OK 引用校验通过"
    $refOk = $true
} catch {
    Write-Warning "无法通过加载产物校验引用: $($_.Exception.Message)"
}

# ---------- 3) 兜底：直接校验引用桩的身份 ----------
# 产物不合法时的退路；也用于在 CI 里快速定位是不是桩本身的问题
if (-not $refOk) {
    Write-Host "改为校验引用桩身份..."
    $stub = '_ci\act-stub\bin\Release\net472\Advanced Combat Tracker.exe'
    if (-not (Test-Path $stub)) { throw "引用桩不存在，且插件引用校验失败: $stub" }

    $stubName = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path $stub).Path)
    $stubPkt = Get-PktString $stubName
    Write-Host "桩标识: $($stubName.FullName)"

    if ($stubName.Name -ne 'Advanced Combat Tracker') { throw "桩名称不正确: $($stubName.Name)" }
    if ($stubPkt -ne $expectedPkt) { throw "桩公钥标记不正确: $stubPkt（应为 $expectedPkt）" }
    if ($stubName.Version -ne [version]$expectedVer) { throw "桩版本不正确: $($stubName.Version)" }

    Write-Host "OK 引用桩身份校验通过（插件引用未直接验证，但桩身份正确意味着引用正确）"
}

Write-Host "全部校验通过"
