<#
.SYNOPSIS
    SkillCap100 模组构建 / 校验 / 部署脚本（SPT 4.1.6）。

.DESCRIPTION
    1. 编译服务端模组 (net10.0) 与客户端插件 (netstandard2.1)；
    2. 用 tools/SkillCap100.Verify 静态校验所有 Harmony 补丁目标与 IL 常量；
    3. 部署到：
         <GameRoot>\SPT_Runtime\user\mods\SkillCap100\SkillCap100.Server.dll
         <GameRoot>\BepInEx\plugins\SkillCap100\SkillCap100.Client.dll

.PARAMETER GameRoot
    游戏根目录（含 EscapeFromTarkov.exe / BepInEx / SPT_Runtime）。

.PARAMETER NoDeploy
    只编译与校验，不复制文件到游戏目录。

.PARAMETER SkipVerify
    跳过静态校验（不推荐）。

.PARAMETER Uninstall
    从游戏目录删除本模组已部署的文件。

.EXAMPLE
    pwsh -File .\build.ps1
    pwsh -File .\build.ps1 -NoDeploy
    pwsh -File .\build.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$GameRoot,
    [switch]$NoDeploy,
    [switch]$SkipVerify,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
if (-not $GameRoot) {
    $GameRoot = 'D:\game\Client.0.16.9.5.40743'   # 工程已移出游戏目录，写死；可用 -GameRoot 覆盖
}

$serverProject = Join-Path $repoRoot 'src\SkillCap100.Server\SkillCap100.Server.csproj'
$clientProject = Join-Path $repoRoot 'src\SkillCap100.Client\SkillCap100.Client.csproj'
$verifyProject = Join-Path $repoRoot 'tools\SkillCap100.Verify\SkillCap100.Verify.csproj'

$serverOut = Join-Path $repoRoot 'src\SkillCap100.Server\bin\Release\net10.0\SkillCap100.Server.dll'
$clientOut = Join-Path $repoRoot 'src\SkillCap100.Client\bin\Release\netstandard2.1\SkillCap100.Client.dll'
$verifyExe = Join-Path $repoRoot 'tools\SkillCap100.Verify\bin\Release\net10.0\SkillCap100.Verify.exe'

$serverDeployDir = Join-Path $GameRoot 'SPT_Runtime\user\mods\SkillCap100'
$clientDeployDir = Join-Path $GameRoot 'BepInEx\plugins\SkillCap100'

Write-Host "GameRoot = $GameRoot" -ForegroundColor Cyan

function Remove-DeployedFiles {
    foreach ($dir in @($serverDeployDir, $clientDeployDir)) {
        if (Test-Path $dir) {
            Write-Host "删除 $dir" -ForegroundColor Yellow
            Remove-Item $dir -Recurse -Force
        }
    }
}

if ($Uninstall) {
    Remove-DeployedFiles
    Write-Host '已卸载 SkillCap100（配置与备份文件保留在 moddev\SkillCap100 下）。' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------- 1. 编译
foreach ($project in @($serverProject, $clientProject, $verifyProject)) {
    Write-Host "`n=== 编译 $([IO.Path]::GetFileName($project)) ===" -ForegroundColor Cyan
    & dotnet build $project -c Release --nologo "/p:GameRoot=$GameRoot" -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "编译失败：$project"
    }
}

# ---------------------------------------------------------------- 2. 校验
if (-not $SkipVerify) {
    Write-Host "`n=== 静态校验补丁目标 ===" -ForegroundColor Cyan
    & $verifyExe $GameRoot --plugin $clientOut --mod $serverOut
    if ($LASTEXITCODE -ne 0) {
        throw '静态校验失败：补丁目标或 IL 结构与预期不符，已中止部署。请查看上面的 FAIL 项。'
    }
}

if ($NoDeploy) {
    Write-Host "`n-NoDeploy：跳过部署。" -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------- 3. 部署
# 服务端在运行时，user/mods 下的 DLL 被占用，复制会失败 —— 提前检查并给出明确提示
$runningServer = Get-Process -Name 'SPT.Server' -ErrorAction SilentlyContinue
if ($runningServer) {
    Write-Host "`n检测到服务端正在运行（PID: $($runningServer.Id -join ', ')），DLL 被占用，已跳过部署。" -ForegroundColor Yellow
    Write-Host '编译与静态校验已完成；如需部署，请先关闭服务端再重新运行本脚本。' -ForegroundColor Yellow
    return
}

Write-Host "`n=== 部署到游戏目录 ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $serverDeployDir, $clientDeployDir | Out-Null

Copy-Item $serverOut $serverDeployDir -Force

# 服务端配置文件：已存在则不覆盖（保留玩家修改）
$serverConfigDest = Join-Path $serverDeployDir 'config.json'
if (Test-Path $serverConfigDest) {
    Write-Host "服务端配置已存在，保留：$serverConfigDest" -ForegroundColor Yellow
}
else {
    Copy-Item (Join-Path $repoRoot 'src\SkillCap100.Server\config.json') $serverConfigDest -Force
}

$clientDll = Join-Path $clientDeployDir 'SkillCap100.Client.dll'
Copy-Item $clientOut $clientDll -Force
Copy-Item (Join-Path $repoRoot 'src\SkillCap100.Client\bin\Release\netstandard2.1\SkillCap100.Client.pdb') $clientDeployDir -Force -ErrorAction SilentlyContinue

Write-Host "`n部署完成：" -ForegroundColor Green
Get-ChildItem $serverDeployDir | ForEach-Object { Write-Host "  $($_.FullName)" }
Get-ChildItem $clientDeployDir | ForEach-Object { Write-Host "  $($_.FullName)" }
Write-Host "`n提示：客户端插件首次启动会在 BepInEx\config\com.skillcap100.client.cfg 生成配置文件。" -ForegroundColor DarkGray
