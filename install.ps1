# wtangent 一键安装（Windows PowerShell）：irm <RAW>/install.ps1 | iex
# 参数：install.ps1 [channel]（stable=正式版[默认] / beta=预发布）
# 环境变量：
#   AGENT_DEST          安装路径（缺省 %LOCALAPPDATA%\wtangent\bin\wtangent.exe，免管理员）
# 注意：本脚本常驻仓库根（raw 分发），不随 release 发布——引导器版本无关
$ErrorActionPreference = "Stop"

param([string]$Channel = "stable")

$destDir = Join-Path $env:LOCALAPPDATA "wtangent\bin"
$exe = Join-Path $destDir "wtangent.exe"

# 平台检测：x64 / arm64（Windows on ARM，如 Surface Pro X）
switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { $asset = "wtangent-win-x64.exe" }
    "ARM64" { $asset = "wtangent-win-arm64.exe" }
    default {
        Write-Host "[install] 不支持的架构: $($env:PROCESSOR_ARCHITECTURE)"
        exit 1
    }
}

$ProgressPreference = 'Continue'

# 解析频道下载源
switch ($Channel) {
    "stable" {
        $url = "https://github.com/WTangent-Org/WTangent/releases/latest/download/$asset"
        Write-Host "[install] 频道 stable（正式版）"
    }
    "beta" {
        # 查最新 prerelease tag
        $rel = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/WTangent-Org/WTangent/releases?per_page=20" -Headers @{ "User-Agent" = "wtangent-install" }
        $tag = ($rel | Where-Object { $_.prerelease } | Select-Object -First 1).tag_name
        if (-not $tag) { Write-Host "[install] 无 beta 版本"; exit 1 }
        $url = "https://github.com/WTangent-Org/WTangent/releases/download/$tag/$asset"
        Write-Host "[install] 频道 beta（预发布 $tag）"
    }
    default {
        Write-Host "[install] 未知频道: $Channel（支持 stable / beta）"
        exit 1
    }
}

try {
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $exe
} catch {
    Write-Host "[install] 下载失败: $($_.Exception.Message)"
    exit 1
}
Write-Host "[install] 已安装：$exe"

# 永久加入用户 PATH（HKCU 免管理员；写注册表 + 广播环境变更，新终端即生效）
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -split ';' -notcontains $destDir) {
    $newPath = if ([string]::IsNullOrEmpty($userPath)) { $destDir } else { "$userPath;$destDir" }
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    Write-Host "[install] 已永久加入用户 PATH：$destDir（新开终端可用 wtangent）"
} else {
    Write-Host "[install] 已在 PATH：$destDir"
}

& $exe --help 2>&1 | Select-Object -First 2

# 自动安装官方组件（serve/tui/client/git；失败不阻断，可手动 wtangent install）
Write-Host "[install] 自动安装官方组件（serve/tui/client/git）…"
try { & $exe install 2>&1 | Select-Object -Last 1 } catch { Write-Host "[install] 官方组件安装失败（可手动 wtangent install）" }

Write-Host "[install] 完成（当前会话用：`$env:Path = '$destDir;' + `$env:Path）"
