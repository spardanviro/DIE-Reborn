<#
.SYNOPSIS
  打一个能直接放到云服务器 (Windows x64) 上跑的服务器包。

.DESCRIPTION
  服务器上什么都不用装:
    - .NET 运行时    自包含发布 (win-x64), 打进包里
    - 游戏数据       cachedata.sexp / cacheattributs.sexp / Maps\ (约 7 MB), 放进 gamedata\
                     服务端优先读程序旁边的 gamedata\, 见 GameDataLoader.DefaultGameRoot
    - Steam          服务端以匿名游戏服务器登录 Steam 核验玩家, 没装 Steam 的机器要
                     steamclient64.dll / tier0_s64.dll / vstdlib_s64.dll 放在 exe 旁边
                     (Valve 签名, 从本机 Steam 目录拷; SteamCMD 里也是这三个)
    - 公网地址       public-host.txt, 默认取仓库根那份

  **不带账号库** —— 更新服务器时整个覆盖也不会冲掉线上的账号。第一次部署想带上本机的
  账号, 加 -IncludeDatabase (要先关掉本机服务端)。

.EXAMPLE
  双击仓库根的「打包服务器.cmd」 (做的就是下面这句)
  powershell -ExecutionPolicy Bypass -File tools\make-server-pack.ps1
#>
param(
    # 服务器公网 IP 或域名。不给就读仓库根的 public-host.txt。
    [string] $PublicHost = '',

    [string] $GameRoot = $(if ($env:DIE_GAME_ROOT) { $env:DIE_GAME_ROOT } `
                           else { 'C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic' }),

    [string] $SteamDir = 'C:\Program Files (x86)\Steam',

    # 把本机开发服的账号库**整个**当成服务器的库 (只在第一次部署时用; 云上已有账号时绝不能用, 会冲掉所有人)
    [switch] $IncludeDatabase,

    # 把本机账号库放进 import\, 到服务器上运行「导入本机账号.cmd」按 Steam ID 合并 (不动别人的账号)
    [switch] $ImportLocalAccounts,

    # 默认放在 DIE_Reborn 外面 (仓库旁边), 免得打进 git
    [string] $OutDir = '',

    # 不压 zip (默认会在包目录旁边生成同名 .zip, 上传用)
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'

$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutDir) { $OutDir = Join-Path $repo '..\DIE_服务器包' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$server = Join-Path $OutDir 'DIE_Server'

if (-not $PublicHost) {
    $hostFile = Join-Path $repo 'public-host.txt'
    if (Test-Path $hostFile) {
        $PublicHost = Get-Content $hostFile -Encoding UTF8 |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') } |
            Select-Object -First 1
    }
    if (-not $PublicHost) { throw "没给 -PublicHost, public-host.txt 里也没有地址" }
}

$gameFiles = 'cachedata.sexp', 'cacheattributs.sexp'
foreach ($f in $gameFiles + 'Maps') {
    if (-not (Test-Path (Join-Path $GameRoot $f))) { throw "游戏目录里缺 $f ($GameRoot)" }
}
$steamDlls = 'steamclient64.dll', 'tier0_s64.dll', 'vstdlib_s64.dll'
foreach ($f in $steamDlls) {
    if (-not (Test-Path (Join-Path $SteamDir $f))) { throw "Steam 目录里缺 $f ($SteamDir)" }
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

# ---- 1. 自包含发布 ----
Write-Host "发布服务端 (Release, win-x64, 自包含) ..."
# NuGetAudit=false: 打包时不去 nuget.org 查漏洞库 —— 国内网络连不上时会刷一屏
# NU1900 警告, 看着像报错, 其实不影响打包。
& dotnet publish (Join-Path $repo 'src\DIE.Server') -c Release -r win-x64 --self-contained true `
    -o $server --nologo -v quiet -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# ---- 2. 游戏数据 ----
$data = Join-Path $server 'gamedata'
New-Item -ItemType Directory -Force $data | Out-Null
foreach ($f in $gameFiles) { Copy-Item (Join-Path $GameRoot $f) $data }
Copy-Item (Join-Path $GameRoot 'Maps') $data -Recurse

# ---- 3. Steam 游戏服务器运行库 ----
foreach ($f in $steamDlls) { Copy-Item (Join-Path $SteamDir $f) $server }

# ---- 4. 公网地址 ----
$hostText = @"
# 服务器公网地址 —— 发给从公网连进来的玩家, 他们的游戏拿它去连战斗服。
# 公网 IP 变了就改这一行, 重启服务器。
$PublicHost
"@
[System.IO.File]::WriteAllText((Join-Path $server 'public-host.txt'), ($hostText -replace "`r?`n", "`r`n"),
    (New-Object System.Text.UTF8Encoding($false)))

# ---- 4.2 离局惩罚免罚名单 (Steam ID), 仓库根有就带上 ----
$exemptFile = Join-Path $repo 'leave-punish-exempt.txt'
if (Test-Path $exemptFile) { Copy-Item $exemptFile $server }

# ---- 4.5 版本号: 云上跑的是哪一版, 以后好对比"上次部署之后改了什么" ----
$commit = (& git.exe -C $repo rev-parse --short HEAD 2>$null)
$dirty = (& git.exe -C $repo status --porcelain --untracked-files=no 2>$null)
if (-not $commit) { $commit = 'unknown' }
$stamp = "{0}{1}  打包于 {2}" -f $commit, $(if ($dirty) { ' (含未提交的改动)' } else { '' }),
    (Get-Date -Format 'yyyy-MM-dd HH:mm')
[System.IO.File]::WriteAllText((Join-Path $server 'version.txt'), $stamp + "`r`n",
    (New-Object System.Text.UTF8Encoding($false)))
if ($dirty) {
    Write-Warning "仓库里有未提交的改动 —— 这个包对不上任何一个提交。建议先提交再打包。"
}

# ---- 4.7 登陆器的补丁镜像 (国内直连 GitHub 下不动时, 登陆器从状态接口 8555 拉这里的) ----
# 由 tools\make-launcher-release.ps1 生成; 没出过发布就没有这个文件夹, 跳过。
$patchMirror = Join-Path $repo '..\DIE_登陆器发布\patch'
if (Test-Path (Join-Path $patchMirror 'latest.json')) {
    Copy-Item $patchMirror (Join-Path $server 'patch') -Recurse
    Write-Host "已带上登陆器补丁镜像 (patch\)"
}

# ---- 5. 账号库 (可选) ----
if ($IncludeDatabase) {
    if (Get-Process DIE.Server -ErrorAction SilentlyContinue) {
        throw "本机服务端还开着 —— 先关掉再带账号库 (开着时库文件可能只写了一半)"
    }
    $db = Join-Path $repo 'src\DIE.Server\bin\Debug\net9.0\data\die_reborn.db'
    if (-not (Test-Path $db)) { throw "本机没有账号库: $db" }
    foreach ($suffix in '', '-wal', '-shm') {
        if (Test-Path "$db$suffix") { Copy-Item "$db$suffix" (Join-Path $server 'data') }
    }
    Write-Host "已带上本机账号库"
}

# ---- 5.5 待合并的本机账号 (可选, 不会覆盖 data\) ----
if ($ImportLocalAccounts) {
    if (Get-Process DIE.Server -ErrorAction SilentlyContinue) {
        throw "本机服务端还开着 —— 先关掉再带账号库 (开着时库文件可能只写了一半)"
    }
    $db = Join-Path $repo 'src\DIE.Server\bin\Debug\net9.0\data\die_reborn.db'
    if (-not (Test-Path $db)) { throw "本机没有账号库: $db" }
    $import = Join-Path $server 'import'
    New-Item -ItemType Directory -Force $import | Out-Null
    foreach ($suffix in '', '-wal', '-shm') {
        if (Test-Path "$db$suffix") { Copy-Item "$db$suffix" $import }
    }
    Write-Host "已把本机账号库放进 import\ —— 到服务器上关服后运行「导入本机账号.cmd」"
}

# ---- 6. 开服脚本和说明 (模板在 tools\server-pack, cmd 是 GBK+CRLF, 原样拷) ----
$templates = Join-Path $PSScriptRoot 'server-pack'
Copy-Item (Join-Path $templates '启动服务器.cmd') $server
Copy-Item (Join-Path $templates '开放防火墙.cmd') $server
Copy-Item (Join-Path $templates '导入本机账号.cmd') $server

# 服务器列表里显示的名字 / 位置: 仓库根有自己填好的就用它, 没有就放模板 (全空 = 显示 IP、位置自动查)
$infoFile = Join-Path $repo 'server-info.txt'
if (-not (Test-Path $infoFile)) { $infoFile = Join-Path $templates 'server-info.txt' }
Copy-Item $infoFile (Join-Path $server 'server-info.txt')
$readme = [System.IO.File]::ReadAllText((Join-Path $templates '说明.txt')).Replace('{PUBLIC_HOST}', $PublicHost)
[System.IO.File]::WriteAllText((Join-Path $OutDir '说明.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

$size = (Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("已生成: {0}  ({1:N0} MB)" -f $OutDir, $size)

# ---- 7. 压成 zip ----
# 中文文件名要能在服务器上正常解压:
#   PowerShell 7 (.NET Core)       不给编码 -> 非 ASCII 名按 UTF-8 存并置 UTF-8 标志位, 什么系统解压都对
#   Windows PowerShell 5 (.NET Fx)  给 UTF-8 不置标志位, 资源管理器会按 GBK 读成乱码 -> 不给编码, 按系统代码页 (GBK) 存,
#                                   中文 Windows 解压是对的。「打包服务器.cmd」有 pwsh 就用 pwsh。
if (-not $NoZip) {
    $zip = "$OutDir.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($OutDir, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ("已压缩: {0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}
Write-Host "服务器公网地址: $PublicHost"
Write-Host "版本: $stamp"
Write-Host "上传部署后记得打标签: git.exe -C `"$repo`" tag deploy-$(Get-Date -Format 'yyyy-MM-dd') $commit"
