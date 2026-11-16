#Requires -Version 7.0
<#
.SYNOPSIS
    抓取 models.dev 的最新模型规格,打成 AI 插件内置的快照(随发行包一起发)。

.DESCRIPTION
    AI 插件「拉取模型」时用 models.dev 补每个型号的上下文窗口与单价。没有外网、被墙、或 models.dev
    临时不可用时,插件只能用本地缓存;从没连通过的新装用户连缓存都没有。发版时把最新数据打进插件,
    这些用户也有一份可用的规格(插件把「本地缓存」与「内置快照」合并,同一型号以较新的为准,见 ModelsDevCatalog)。

    产出两个文件(都不进版本库,见 .gitignore):
      <OutputDirectory>/models-dev.json.gz   原始 api.json 的 gzip(插件的解析器直接认原始格式)
      <OutputDirectory>/models-dev.stamp     抓取时刻(UTC,ISO 8601),插件据此判断新旧

    发版流水线(.github/workflows/release.yml)在 prepare-release 里跑一次、作为构建产物分发给各平台,
    四个平台打进去的是同一份。本地开发不必跑:没有快照时插件的行为与以前一样。

.PARAMETER OutputDirectory
    输出目录;默认是 AI 插件工程的 Assets 目录。

.PARAMETER Optional
    抓取或校验失败时只给警告、以 0 退出(发版不该因为 models.dev 临时不可用而失败)。

.PARAMETER MaxAgeMonths
    只留这么多个月内发布或更新过的模型(release_date / last_updated 任一在期内;没有日期的保留)。
    只裁内置快照:插件联网刷新时保存的是完整数据,并与快照合并(见 ModelsDevCatalog),
    能连 models.dev 的用户不会因此丢掉老型号的规格。默认 12;0 表示不按日期裁。
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..' 'plugins' 'VelaShell.Plugin.Ai' 'Assets'),
    [switch]$Optional,
    [ValidateRange(0, 240)]
    [int]$MaxAgeMonths = 12
)

$ErrorActionPreference = 'Stop'
$SourceUrl = 'https://models.dev/api.json'
# 少于这么多家就当拿到的是错误页 / 改版后的空壳,不拿它覆盖(线上有上百家)
$MinimumProviders = 20

function Fail([string]$message) {
    if ($Optional) {
        Write-Warning "models.dev snapshot skipped: $message"
        exit 0
    }
    throw $message
}

$json = $null
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $SourceUrl -TimeoutSec 60 -UseBasicParsing
        $json = $response.Content
        break
    }
    catch {
        Write-Warning "Download attempt $attempt failed: $($_.Exception.Message)"
        if ($attempt -lt 3) { Start-Sleep -Seconds (5 * $attempt) }
    }
}
if ([string]::IsNullOrWhiteSpace($json)) { Fail 'could not download api.json' }

# 校验:顶层是对象、带 models 表的供应商足够多 —— 解析规则在插件里(ModelsDevCatalog.Parse),这里只防明显的坏数据
try {
    $document = [System.Text.Json.JsonDocument]::Parse($json)
}
catch {
    Fail "api.json is not valid JSON: $($_.Exception.Message)"
}
try {
    if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Fail 'api.json root is not an object' }
    $providers = 0
    foreach ($provider in $document.RootElement.EnumerateObject()) {
        $models = [System.Text.Json.JsonElement]::new()
        if ($provider.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Object -and
            $provider.Value.TryGetProperty('models', [ref]$models) -and
            $models.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
            $providers++
        }
    }
    if ($providers -lt $MinimumProviders) { Fail "api.json lists only $providers providers (expected at least $MinimumProviders)" }
}
finally {
    $document.Dispose()
}

# 裁剪:只留插件解析会读的字段(见 ModelsDevCatalog.ReadSpec / IsUsableForChat),去掉已下架的型号。
# 仍是原始 api.json 的层级(供应商 → models → 模型 → limit / cost),插件的解析器原样认。
# 原始约 6.6 MB / gzip 约 670 KB;裁剪后小一个量级,发行包不为它明显变大。
$root = [System.Text.Json.Nodes.JsonNode]::Parse($json)
$slim = [System.Text.Json.Nodes.JsonObject]::new()
$cutoff = if ($MaxAgeMonths -gt 0) { [DateTime]::UtcNow.Date.AddMonths(-$MaxAgeMonths) } else { $null }
$keptModels = 0
$tooOld = 0

# release_date / last_updated 形如 2025-04-14,少数只到月份(2025-04)
function Get-ModelDate($node, [string]$field) {
    $value = $node[$field]
    if ($null -eq $value -or $value.GetValueKind() -ne [System.Text.Json.JsonValueKind]::String) { return $null }
    $text = [string]$value
    if ($text.Length -eq 7) { $text += '-01' }
    $parsed = [DateTime]::MinValue
    if ([DateTime]::TryParseExact($text, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal, [ref]$parsed)) {
        return $parsed
    }
    return $null
}
foreach ($provider in $root.AsObject()) {
    $models = $provider.Value?['models']
    if ($null -eq $models -or $models.GetValueKind() -ne [System.Text.Json.JsonValueKind]::Object) { continue }
    $kept = [System.Text.Json.Nodes.JsonObject]::new()
    foreach ($model in $models.AsObject()) {
        $m = $model.Value
        if ($null -eq $m -or $m.GetValueKind() -ne [System.Text.Json.JsonValueKind]::Object) { continue }
        if ($m['status'] -and [string]$m['status'] -eq 'deprecated') { continue }
        if ($null -ne $cutoff) {
            $released = Get-ModelDate $m 'release_date'
            $updated = Get-ModelDate $m 'last_updated'
            # 两个日期都有且都早于期限才算太旧;缺日期的不猜,保留
            if (($null -ne $released -or $null -ne $updated) -and
                ($null -eq $released -or $released -lt $cutoff) -and ($null -eq $updated -or $updated -lt $cutoff)) {
                $tooOld++
                continue
            }
        }
        $keptModels++
        $entry = [System.Text.Json.Nodes.JsonObject]::new()
        foreach ($field in 'id', 'name', 'reasoning') {
            if ($null -ne $m[$field]) { $entry[$field] = $m[$field].DeepClone() }
        }
        foreach ($group in @(@{ Name = 'limit'; Fields = 'context', 'output' }, @{ Name = 'cost'; Fields = 'input', 'output', 'cache_read' })) {
            $source = $m[$group.Name]
            if ($null -eq $source -or $source.GetValueKind() -ne [System.Text.Json.JsonValueKind]::Object) { continue }
            $part = [System.Text.Json.Nodes.JsonObject]::new()
            foreach ($field in $group.Fields) {
                if ($null -ne $source[$field]) { $part[$field] = $source[$field].DeepClone() }
            }
            $entry[$group.Name] = $part
        }
        $kept[$model.Key] = $entry
    }
    if ($kept.Count -gt 0) {
        $wrapper = [System.Text.Json.Nodes.JsonObject]::new()
        $wrapper['models'] = $kept
        $slim[$provider.Key] = $wrapper
    }
}
$json = $slim.ToJsonString()

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$target = Join-Path $OutputDirectory 'models-dev.json.gz'
$stamp = Join-Path $OutputDirectory 'models-dev.stamp'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
$file = [System.IO.File]::Create($target)
try {
    $gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionLevel]::SmallestSize)
    try { $gzip.Write($bytes, 0, $bytes.Length) } finally { $gzip.Dispose() }
}
finally {
    $file.Dispose()
}
[System.IO.File]::WriteAllText($stamp, [DateTimeOffset]::UtcNow.ToString('o'))

$size = (Get-Item $target).Length
Write-Host ("models.dev snapshot: {0} providers, {1} models kept, {2} older than {3} months dropped; {4:N0} bytes trimmed JSON → {5:N0} bytes gzip at {6}" -f `
        $slim.Count, $keptModels, $tooOld, $MaxAgeMonths, $bytes.Length, $size, $target)
