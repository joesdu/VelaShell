#Requires -Version 7.0
<#
.SYNOPSIS
    收掉互操作测试用的 sshd 容器。

.DESCRIPTION
    留着一台上一轮的服务端，会让「为什么这条用例过了那条没过」变得无从查起 ——
    跑完就收掉。
#>
[CmdletBinding()]
param(
    [string]$ContainerName = 'velashell-ssh-interop'
)

$ErrorActionPreference = 'Stop'

Write-Host "==> 收掉容器 $ContainerName" -ForegroundColor Cyan
docker rm -f $ContainerName 2>$null | Out-Null

Write-Host '已收掉。' -ForegroundColor Green
