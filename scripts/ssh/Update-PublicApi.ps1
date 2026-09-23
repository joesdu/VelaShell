#!/usr/bin/env pwsh
<#
.SYNOPSIS
    把 PublicApiAnalyzers 报出来的 RS0016（未声明的公开 API）补进 PublicAPI.Unshipped.txt。

.DESCRIPTION
    公开面由 Microsoft.CodeAnalysis.PublicApiAnalyzers 钉住：新增任何 public 成员，
    都必须同时在 PublicAPI.Unshipped.txt 里登记一行。这是刻意的摩擦（src/VelaShell.Ssh/AGENTS.md §3.4）——
    让每一次公开面变更都是显式决定，而不是顺手加的。

    但「显式决定」指的是**看一眼列表并认可它**，不是手工抄几十行符号签名。
    这个脚本负责抄写那一步：跑一次构建、把 RS0016 的符号抓出来、排序去重写回去。
    你仍然要 review 生成的 diff —— 那才是门禁真正的价值所在。

    反向的 RS0017（文件里有、代码里已删的 API）不自动处理：删公开 API 是破坏性变更，
    必须有人明确知道自己在做什么。脚本只会把它们列出来提醒。

.PARAMETER Project
    要处理的项目路径。省略则处理 src/ 下全部可打包项目。

.PARAMETER WhatIf
    只显示会写入什么，不落盘。

.EXAMPLE
    ./scripts/ssh/Update-PublicApi.ps1
    ./scripts/ssh/Update-PublicApi.ps1 -Project src/VelaShell.Ssh -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Project
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$projects = if ($Project) {
    @(Resolve-Path $Project)
} else {
    Get-ChildItem -Path (Join-Path $repoRoot 'src/VelaShell.Ssh') -Filter '*.csproj' -Recurse |
        Select-Object -ExpandProperty FullName
}

if (-not $projects) {
    Write-Warning 'No projects found.'
    return
}

$anyChange = $false

foreach ($proj in $projects) {
    $projPath = if ((Get-Item $proj).PSIsContainer) {
        (Get-ChildItem -Path $proj -Filter '*.csproj' | Select-Object -First 1).FullName
    } else { [string]$proj }

    $projDir = Split-Path -Parent $projPath
    $projName = [System.IO.Path]::GetFileNameWithoutExtension($projPath)
    $unshipped = Join-Path $projDir 'PublicAPI.Unshipped.txt'

    Write-Host "== $projName" -ForegroundColor Cyan

    # -warnaserror:false：我们要的是诊断清单，不是让构建在第一条上就停。
    $output = & dotnet build $projPath -v minimal -nologo `
        -p:TreatWarningsAsErrors=false -p:EnforceCodeStyleInBuild=false 2>&1 | Out-String

    # RS0016 的消息形如：
    #   ... error RS0016: Symbol 'Foo.Bar -> Baz' is not part of the declared public API ...
    # 符号签名夹在第一对单引号之间。
    $added = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($m in [regex]::Matches($output, "RS0016:\s*Symbol\s*'([^']+)'")) {
        [void]$added.Add($m.Groups[1].Value)
    }

    # RS0017：文件里登记了、代码里已经没有的 API。只提醒，不自动删。
    $stale = [regex]::Matches($output, "RS0017:\s*Symbol\s*'([^']+)'") |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique

    if ($stale) {
        Write-Host '   RS0017 —— 这些条目在代码里已不存在（删公开 API 是破坏性变更，请手工确认后移除）：' -ForegroundColor Yellow
        $stale | ForEach-Object { Write-Host "     - $_" -ForegroundColor Yellow }
    }

    if ($added.Count -eq 0) {
        Write-Host '   公开面已是最新，无需改动。' -ForegroundColor DarkGray
        continue
    }

    $existing = if (Test-Path $unshipped) {
        Get-Content -LiteralPath $unshipped -Encoding utf8
    } else { @('#nullable enable') }

    $header  = $existing | Where-Object { $_ -like '#*' }
    if (-not $header) { $header = @('#nullable enable') }
    $entries = $existing | Where-Object { $_ -notlike '#*' -and $_.Trim() }

    $merged = @($entries) + @($added) | Sort-Object -Unique -CaseSensitive
    $content = (@($header) + $merged) -join "`n"
    $content += "`n"

    Write-Host "   +$($added.Count) 条，合计 $($merged.Count) 条" -ForegroundColor Green
    $added | Sort-Object | ForEach-Object { Write-Host "     + $_" -ForegroundColor DarkGreen }

    if ($PSCmdlet.ShouldProcess($unshipped, 'write')) {
        [System.IO.File]::WriteAllText($unshipped, $content, (New-Object System.Text.UTF8Encoding $false))
        $anyChange = $true
    }
}

if ($anyChange) {
    Write-Host ''
    Write-Host '公开面已更新。请 review 上面的 diff —— 门禁的价值在这一步，不在自动写文件。' -ForegroundColor Cyan
}

# 上面那次构建**本来就应该是红的**（RS0016 就是我们要采集的东西），
# 它的退出码不代表本脚本成功与否，别让它透出去污染调用方。
$global:LASTEXITCODE = 0
exit 0
