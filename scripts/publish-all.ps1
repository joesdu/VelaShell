# VelaShell 全平台发布脚本(在 Windows 上运行)
# 产物(publish/ 目录):
#   Windows x64 / arm64 × 含运行时(self-contained)与不含运行时(framework-dependent)→ zip
#   Windows x64 / arm64 安装包:Velopack Setup.exe(含应用内自动更新 feed 文件)+ WiX MSI
#     (需要工具:dotnet tool install -g vpk --version 1.2.0 / -g wix --version 5.0.2;缺失则跳过)
#   macOS  x64 / arm64 含运行时 → tar.gz(Apple 签名/公证与 .dmg 需在 macOS 上完成)
#   Linux  x64 / arm64 含运行时 → tar.gz
# 用法: pwsh scripts/publish-all.ps1 [-Configuration Release]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\VelaShell\VelaShell.csproj'
$outRoot = Join-Path $root 'publish'
$icon = Join-Path $root 'src\VelaShell\Assets\velashell.ico'

# 版本号取自 Directory.Build.props 的 <Version>
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw '未能从 Directory.Build.props 读到 <Version>' }
$numeric = $version.Split('-')[0]   # MSI ProductVersion 不允许预发布后缀
Write-Host "== VelaShell $version ($Configuration) ==" -ForegroundColor Cyan

$haveVpk = [bool](Get-Command vpk -ErrorAction SilentlyContinue)
$haveWix = [bool](Get-Command wix -ErrorAction SilentlyContinue)
if (-not $haveVpk) { Write-Warning '未找到 vpk,跳过 Setup.exe(dotnet tool install -g vpk --version 1.2.0)' }
if (-not $haveWix) { Write-Warning '未找到 wix,跳过 MSI(dotnet tool install -g wix --version 5.0.2)' }

$targets = @(
    @{ Rid = 'win-x64';     SelfContained = $true;  Archive = 'zip';   Suffix = '';           Installers = $true }
    @{ Rid = 'win-x64';     SelfContained = $false; Archive = 'zip';   Suffix = '-noruntime'; Installers = $false }
    @{ Rid = 'win-arm64';   SelfContained = $true;  Archive = 'zip';   Suffix = '';           Installers = $true }
    @{ Rid = 'win-arm64';   SelfContained = $false; Archive = 'zip';   Suffix = '-noruntime'; Installers = $false }
    @{ Rid = 'osx-x64';     SelfContained = $true;  Archive = 'targz'; Suffix = '';           Installers = $false }
    @{ Rid = 'osx-arm64';   SelfContained = $true;  Archive = 'targz'; Suffix = '';           Installers = $false }
    @{ Rid = 'linux-x64';   SelfContained = $true;  Archive = 'targz'; Suffix = '';           Installers = $false }
    @{ Rid = 'linux-arm64'; SelfContained = $true;  Archive = 'targz'; Suffix = '';           Installers = $false }
)

if (Test-Path $outRoot) { Remove-Item $outRoot -Recurse -Force }
New-Item -ItemType Directory -Force $outRoot | Out-Null

foreach ($t in $targets) {
    $name = "VelaShell-$version-$($t.Rid)$($t.Suffix)"
    $dir = Join-Path $outRoot $name
    Write-Host "-- publish $name (self-contained=$($t.SelfContained))" -ForegroundColor Yellow
    dotnet publish $project -c $Configuration -r $t.Rid -o $dir `
        -p:SelfContained=$($t.SelfContained) -p:PublishSingleFile=true -p:DebugType=None --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "publish 失败: $name" }

    if ($t.Archive -eq 'zip') {
        Get-ChildItem $dir -Recurse -File -Filter '*.pdb' | Remove-Item -Force
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath (Join-Path $outRoot "$name.zip") -Force
    } else {
        # tar.gz 保留 Unix 可执行位语义(tar 在 Windows 上无权限位,解包后需 chmod +x VelaShell)
        tar -czf (Join-Path $outRoot "$name.tar.gz") -C $dir .
        if ($LASTEXITCODE -ne 0) { throw "tar 失败: $name" }
    }

    # Windows 安装包(基于含运行时的发布目录)
    if ($t.Installers) {
        if ($haveVpk) {
            Write-Host "-- vpk Setup.exe ($($t.Rid))" -ForegroundColor Yellow
            $vpkOut = Join-Path $outRoot "_vpk-$($t.Rid)"
            vpk pack --packId VelaShell --packVersion $version --packDir $dir `
                --mainExe VelaShell.exe --icon $icon --channel $t.Rid --outputDir $vpkOut
            if ($LASTEXITCODE -ne 0) { throw "vpk 失败: $($t.Rid)" }
            Move-Item (Join-Path $vpkOut "VelaShell-$($t.Rid)-Setup.exe") (Join-Path $outRoot "$name-Setup.exe")
            # 自动更新 feed:RELEASES-*/releases.json/assets.json/full.nupkg(Portable.zip 与自打 zip 重复,弃)
            Get-ChildItem $vpkOut -File | Where-Object { $_.Name -notlike '*Portable.zip' } |
                Move-Item -Destination $outRoot
            Remove-Item $vpkOut -Recurse -Force
        }
        if ($haveWix) {
            Write-Host "-- wix MSI ($($t.Rid))" -ForegroundColor Yellow
            $arch = $t.Rid.Replace('win-', '')
            # 安装向导(WixUI_InstallDir,中文界面)支持自定义安装目录;
            # 需要 UI 扩展:wix extension add -g WixToolset.UI.wixext/5.0.2
            wix build (Join-Path $root 'installer\VelaShell.wxs') -arch $arch -pdbtype none `
                -ext WixToolset.UI.wixext -culture zh-CN `
                -d ProductVersion=$numeric -d "PublishDir=$dir" -d "IconPath=$icon" `
                -d "LicenseRtf=$(Join-Path $root 'installer\License.rtf')" `
                -o (Join-Path $outRoot "$name.msi")
            if ($LASTEXITCODE -ne 0) { throw "wix 失败: $($t.Rid)" }
        }
    }
    Remove-Item $dir -Recurse -Force
}

# 校验文件(与 CI 同格式)
$sums = Get-ChildItem $outRoot -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $outRoot 'SHA256SUMS.txt') $sums -Encoding utf8NoBOM

Write-Host "`n== 产物 ==" -ForegroundColor Cyan
Get-ChildItem $outRoot -File | Sort-Object Name | ForEach-Object {
    '{0,-58} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
