# VelaShell 全平台发布脚本(在 Windows 上运行)
# 产物(publish/ 目录):
#   Windows x64 / arm64  × 含运行时(self-contained)与不含运行时(framework-dependent,需已装 .NET 10 桌面运行时)→ zip
#   macOS   x64 / arm64  含运行时 → tar.gz(Apple 签名/公证与 .dmg 需在 macOS 上完成)
#   Linux   x64 / arm64  含运行时 → tar.gz
# 用法: pwsh scripts/publish-all.ps1 [-Configuration Release]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\VelaShell\VelaShell.csproj'
$outRoot = Join-Path $root 'publish'

# 版本号取自 Directory.Build.props 的 <Version>
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw '未能从 Directory.Build.props 读到 <Version>' }
Write-Host "== VelaShell $version ($Configuration) ==" -ForegroundColor Cyan

$targets = @(
    @{ Rid = 'win-x64';     SelfContained = $true;  Archive = 'zip';   Suffix = '' }
    @{ Rid = 'win-x64';     SelfContained = $false; Archive = 'zip';   Suffix = '-noruntime' }
    @{ Rid = 'win-arm64';   SelfContained = $true;  Archive = 'zip';   Suffix = '' }
    @{ Rid = 'win-arm64';   SelfContained = $false; Archive = 'zip';   Suffix = '-noruntime' }
    @{ Rid = 'osx-x64';     SelfContained = $true;  Archive = 'targz'; Suffix = '' }
    @{ Rid = 'osx-arm64';   SelfContained = $true;  Archive = 'targz'; Suffix = '' }
    @{ Rid = 'linux-x64';   SelfContained = $true;  Archive = 'targz'; Suffix = '' }
    @{ Rid = 'linux-arm64'; SelfContained = $true;  Archive = 'targz'; Suffix = '' }
)

if (Test-Path $outRoot) { Remove-Item $outRoot -Recurse -Force }
New-Item -ItemType Directory -Force $outRoot | Out-Null

foreach ($t in $targets) {
    $name = "VelaShell-$version-$($t.Rid)$($t.Suffix)"
    $dir = Join-Path $outRoot $name
    Write-Host "-- publish $name (self-contained=$($t.SelfContained))" -ForegroundColor Yellow
    dotnet publish $project -c $Configuration -r $t.Rid -o $dir `
        -p:SelfContained=$($t.SelfContained) -p:PublishSingleFile=true --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "publish 失败: $name" }

    if ($t.Archive -eq 'zip') {
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath (Join-Path $outRoot "$name.zip") -Force
    } else {
        # tar.gz 保留 Unix 可执行位语义(tar 在 Windows 上无权限位,解包后需 chmod +x VelaShell)
        tar -czf (Join-Path $outRoot "$name.tar.gz") -C $dir .
        if ($LASTEXITCODE -ne 0) { throw "tar 失败: $name" }
    }
    Remove-Item $dir -Recurse -Force
}

Write-Host "`n== 产物 ==" -ForegroundColor Cyan
Get-ChildItem $outRoot | Sort-Object Name | ForEach-Object {
    '{0,-55} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
