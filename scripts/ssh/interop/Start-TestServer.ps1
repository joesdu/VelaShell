#Requires -Version 7.0
<#
.SYNOPSIS
    起一台真的 OpenSSH 服务端，供互操作用例连。

.DESCRIPTION
    互操作测试的价值全在「对端是真的」—— 我们自己的测试桩再全，
    也只能验证我们对规范的理解与自己一致。真正的坑在
    「OpenSSH 实际怎么做」与「规范怎么写」的差值里
    （SFTP 的 SYMLINK 参数顺序就是最有名的一个）。

    脚本用 docker 起一个 sshd，配好一个用户、一把密钥，
    并把连接参数写进环境变量文件。

.PARAMETER Port
    本机映射端口。默认 2222。

.PARAMETER Image
    用哪个镜像。默认 linuxserver/openssh-server（带 sftp-server 与常用工具）。

.EXAMPLE
    pwsh scripts/ssh/interop/Start-TestServer.ps1
    dotnet test --filter "TestCategory=Interop"
    pwsh scripts/ssh/interop/Stop-TestServer.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 2222,
    [string]$Image = 'lscr.io/linuxserver/openssh-server:latest',
    [string]$ContainerName = 'velashell-ssh-interop',

    # 装 xauth 并打开 X11Forwarding —— X11 互操作用例需要它。
    # 现成镜像默认两样都没有，而**没有真实对端就等于没验**
    # （velashell-docs/zh/ssh/design/architecture.md §11.2.13）。
    [switch]$X11
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Windows 上 ssh-keygen / ssh 会拒绝「别人也读得到」的私钥文件。
# ssh-keygen 自己生成的那把 ACL 是对的，但 Copy-Item 出来的会继承目录的 ACL，
# 症状是一句 "UNPROTECTED PRIVATE KEY FILE" 然后整个脚本停在这。
function Protect-KeyFile([string]$Path) {
    if (-not $IsWindows) {
        return
    }
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls $Path /inheritance:r /grant:r "${me}:(R,W)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "收紧 $Path 的权限失败。"
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw '找不到 docker。互操作测试需要它来起一台真的 sshd。'
}

$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$keyDir = Join-Path $repoRoot 'artifacts/interop'
$keyPath = Join-Path $keyDir 'id_ed25519'
$encKeyPath = Join-Path $keyDir 'id_ed25519_enc'
$caPath = Join-Path $keyDir 'ca'
$certPath = "$keyPath-cert.pub"
$hostCaPath = Join-Path $keyDir 'host_ca'
$hostKeyPub = Join-Path $keyDir 'server_host_ed25519.pub'
$hostCertPath = Join-Path $keyDir 'server_host_ed25519-cert.pub'
$encPassphrase = 'interop-passphrase'

# 已经在跑就先收掉 —— 留着一台上一轮的服务端，
# 会让「为什么这条用例过了那条没过」变得无从查起。
Write-Step "清掉可能残留的容器 $ContainerName"
docker rm -f $ContainerName 2>$null | Out-Null

New-Item -ItemType Directory -Force -Path $keyDir | Out-Null

foreach ($stale in @($keyPath, "$keyPath.pub", $certPath,
                     $encKeyPath, "$encKeyPath.pub", $caPath, "$caPath.pub",
                     $hostCaPath, "$hostCaPath.pub", $hostKeyPub, $hostCertPath)) {
    if (Test-Path $stale) {
        Remove-Item -Force $stale -ErrorAction SilentlyContinue
    }
}

Write-Step '生成一把测试用的 ed25519 密钥'
& ssh-keygen -t ed25519 -f $keyPath -N '' -C 'velashell-interop' -q
if ($LASTEXITCODE -ne 0) {
    throw 'ssh-keygen 失败。Windows 上它随 OpenSSH 可选功能提供。'
}

# 同一把钥再来一份**带口令**的。加密的 OpenSSH 私钥现在读得了（bcrypt_pbkdf），
# 而这条路径只有对着真 ssh-keygen 写出来的文件才验得了 ——
# 自己写一遍加密侧再自解自，两边一起错也照样"通过"。
Write-Step '再做一份带口令的同一把钥（验 bcrypt_pbkdf）'
Copy-Item $keyPath $encKeyPath -Force
Copy-Item "$keyPath.pub" "$encKeyPath.pub" -Force
Protect-KeyFile $encKeyPath
& ssh-keygen -p -f $encKeyPath -P '' -N $encPassphrase -q
if ($LASTEXITCODE -ne 0) {
    throw '给测试密钥加口令失败。'
}

# 证书认证：签一张用户证书，principal 就是登录名。
Write-Step '签一张用户证书（验证书认证）'
& ssh-keygen -t ed25519 -f $caPath -N '' -C 'velashell-interop-ca' -q
if ($LASTEXITCODE -ne 0) {
    throw '生成 CA 密钥失败。'
}
& ssh-keygen -s $caPath -I 'velashell-interop' -n 'velashell' -V '-5m:+52w' -z 1 "$keyPath.pub"
if ($LASTEXITCODE -ne 0) {
    throw '签发用户证书失败。'
}

$publicKey = Get-Content "$keyPath.pub" -Raw

Write-Step "起容器（镜像 $Image，端口 $Port）"
docker run -d `
    --name $ContainerName `
    -e PUID=1000 -e PGID=1000 `
    -e USER_NAME=velashell `
    -e USER_PASSWORD=velashell `
    -e PASSWORD_ACCESS=true `
    -e PUBLIC_KEY="$publicKey" `
    -e DOCKER_MODS=linuxserver/mods:openssh-server-ssh-tunnel `
    -p "${Port}:2222" `
    $Image | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "docker run 失败。"
}

Write-Step '等 sshd 起来'
$deadline = (Get-Date).AddSeconds(90)
$ready = $false

while ((Get-Date) -lt $deadline) {
    try {
        $probe = [System.Net.Sockets.TcpClient]::new()
        $probe.Connect('127.0.0.1', $Port)
        $stream = $probe.GetStream()
        $stream.ReadTimeout = 3000

        $buffer = [byte[]]::new(64)
        $read = $stream.Read($buffer, 0, $buffer.Length)
        $banner = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)

        $probe.Close()

        if ($banner.StartsWith('SSH-2.0-')) {
            Write-Host "    对端版本：$($banner.Trim())" -ForegroundColor DarkGray
            $ready = $true
            break
        }
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $ready) {
    docker logs $ContainerName
    throw "等了 90 秒 sshd 还没起来。"
}

if ($X11) {
    # ⚠️ 这一段是**运行时改配置**，不是构建一个镜像。
    #
    # 本来想写 Dockerfile（scripts/ssh/interop/Dockerfile 还在），但这台机器上
    # Docker Hub 拉不动（auth.docker.io 超时），而 lscr.io 是通的。
    # Alpine 的包源本身能连，所以退一步：在已经起来的容器里装 xauth、
    # 改配置、让 sshd 重读。CI 上 Docker Hub 通，那边可以改回构建镜像。
    Write-Step '装 xauth 并打开 X11Forwarding'

    # ⚠️ **要重试，而且失败时要把输出打出来。**
    # 容器刚起来那一两秒里网络还没通，apk 会失败；而把输出丢进 /dev/null
    # 的话，错误信息就只剩一句「装 xauth 失败」—— 那指不到任何地方。
    $installed = $false
    $lastOutput = ''

    for ($attempt = 1; $attempt -le 5 -and -not $installed; $attempt++) {
        $lastOutput = docker exec $ContainerName sh -c 'apk update && apk add --no-cache xauth' 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) {
            $installed = $true
            break
        }

        Write-Host "    第 $attempt 次装 xauth 没成，2 秒后重试" -ForegroundColor DarkYellow
        Start-Sleep -Seconds 2
    }

    if (-not $installed) {
        Write-Host $lastOutput
        throw '在容器里装 xauth 失败（上面是 apk 的输出）。'
    }


    # ⚠️ **配置文件的位置要从 sshd 的命令行里问出来，不能写死。**
    #
    # linuxserver 的镜像跑的是 `sshd -f /config/sshd/sshd_config`，
    # 而不是 /etc/ssh/sshd_config。改错文件的表现极具迷惑性：
    # 文件里明明写着 X11Forwarding yes，服务端却照样拒绝 —— 因为那份根本没被读。
    #
    # HUP 也只发给 sshd 那一个 pid。第一版按 comm 里带 "sshd" 就 HUP，
    # 结果把不该碰的也碰了。
    $configure = @'
set -e

pid=''
conf='/etc/ssh/sshd_config'

for p in /proc/[0-9]*; do
    [ -r "$p/cmdline" ] || continue
    cmd=$(tr '\0' ' ' < "$p/cmdline" 2>/dev/null) || continue
    case "$cmd" in
        *sshd*listener*|*sshd*-D*)
            pid="${p#/proc/}"
            f=$(printf '%s' "$cmd" | sed -n 's/.*-f \([^ ][^ ]*\).*/\1/p')
            [ -n "$f" ] && conf="$f"
            break
            ;;
    esac
done

[ -n "$pid" ] || { echo '在容器里找不到 sshd 进程'; exit 1; }
[ -f "$conf" ] || { echo "sshd 说的配置文件不存在：$conf"; exit 1; }
echo "sshd pid=$pid 配置=$conf"

sed -i 's/^X11Forwarding .*/X11Forwarding yes/' "$conf"
grep -q '^X11Forwarding yes' "$conf" || echo 'X11Forwarding yes' >> "$conf"
grep -q '^X11UseLocalhost' "$conf" || echo 'X11UseLocalhost yes' >> "$conf"
grep -q '^AllowStreamLocalForwarding' "$conf" || echo 'AllowStreamLocalForwarding yes' >> "$conf"

kill -HUP "$pid"

command -v xauth >/dev/null || { echo 'xauth 没装上'; exit 1; }
grep -q '^X11Forwarding yes' "$conf" || { echo 'X11Forwarding 没打开'; exit 1; }
'@

    # ⚠️ 去掉 CR：.gitattributes 让 .ps1 按 CRLF 检出，这段 here-string 就带着 \r，
    #    而容器里的 sh 会把 `set -e\r` 当成非法选项（「set: illegal option -」）。
    $configure = $configure -replace "`r", ''
    docker exec $ContainerName sh -c $configure
    if ($LASTEXITCODE -ne 0) {
        throw '打开 X11Forwarding 失败。'
    }

    Start-Sleep -Milliseconds 500
    Write-Host '    xauth 已装、X11Forwarding 已打开' -ForegroundColor DarkGray
}

Write-Step '让 sshd 信任这把 CA（证书认证）'
# 容器侧的那段 sh 单独放在 trust-ca.sh 里 —— 理由写在那个文件的头部。
$caPublicKey = (Get-Content "$caPath.pub" -Raw).Trim()
$trustScript = Join-Path $PSScriptRoot 'trust-ca.sh'

# ⚠️ **用 docker cp 传文件，不要 `Get-Content | docker exec -i sh -s`。**
# PowerShell 给原生命令的管道收尾时补的是 CRLF，于是脚本末尾多出一个孤零零的
# `\r`，sh 把它当成一条命令 —— 报出来是一句莫名其妙的 "sh: : not found"，
# 而前面的脚本其实已经跑完了，看上去就像「成功了又失败了」。
docker cp $trustScript "${ContainerName}:/tmp/trust-ca.sh" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw '把 trust-ca.sh 拷进容器失败。'
}

docker exec $ContainerName sh /tmp/trust-ca.sh $caPublicKey
if ($LASTEXITCODE -ne 0) {
    throw '配置 TrustedUserCAKeys 失败 —— 证书认证的互操作用例会连不上。'
}
Start-Sleep -Milliseconds 500

# 主机证书：给 sshd 正在用的 ed25519 主机密钥签一张，让它出示证书。
# 客户端这边用 known_hosts 的 @cert-authority 去验 —— 这条路径只有对着真 ssh-keygen 签的证书才验得了。
Write-Step '给 sshd 的主机密钥签一张主机证书（验 @cert-authority）'
& ssh-keygen -t ed25519 -f $hostCaPath -N '' -C 'velashell-interop-host-ca' -q
if ($LASTEXITCODE -ne 0) {
    throw '生成主机 CA 失败。'
}

$containerHostKey = '/config/ssh_host_keys/ssh_host_ed25519_key'
docker cp "${ContainerName}:$containerHostKey.pub" $hostKeyPub | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "从容器里取主机公钥失败（$containerHostKey.pub）。"
}

# 主体写 127.0.0.1 与 localhost：用例按这两个名字连。
& ssh-keygen -s $hostCaPath -h -I 'velashell-interop-host' -n '127.0.0.1,localhost' -V '-5m:+52w' $hostKeyPub
if ($LASTEXITCODE -ne 0) {
    throw '签发主机证书失败。'
}

docker cp $hostCertPath "${ContainerName}:$containerHostKey-cert.pub" | Out-Null
docker cp (Join-Path $PSScriptRoot 'host-cert.sh') "${ContainerName}:/tmp/host-cert.sh" | Out-Null
docker exec $ContainerName sh /tmp/host-cert.sh "$containerHostKey-cert.pub"
if ($LASTEXITCODE -ne 0) {
    throw '配置 HostCertificate 失败 —— 主机证书的互操作用例会连不上。'
}
Start-Sleep -Milliseconds 500

Write-Step '写环境变量'
$envFile = Join-Path $keyDir 'env.ps1'
@"
# 由 Start-TestServer.ps1 生成。`. 这个文件之后再跑 dotnet test。
`$env:VELASHELL_SSH_INTEROP = '1'
`$env:VELASHELL_SSH_INTEROP_HOST = '127.0.0.1'
`$env:VELASHELL_SSH_INTEROP_PORT = '$Port'
`$env:VELASHELL_SSH_INTEROP_USER = 'velashell'
`$env:VELASHELL_SSH_INTEROP_PASSWORD = 'velashell'
`$env:VELASHELL_SSH_INTEROP_KEY = '$($keyPath -replace '\\', '\\')'
`$env:VELASHELL_SSH_INTEROP_KEY_ENCRYPTED = '$($encKeyPath -replace '\\', '\\')'
`$env:VELASHELL_SSH_INTEROP_KEY_PASSPHRASE = '$encPassphrase'
`$env:VELASHELL_SSH_INTEROP_CERT = '$($certPath -replace '\\', '\\')'
`$env:VELASHELL_SSH_INTEROP_HOST_CA = '$("$hostCaPath.pub" -replace '\\', '\\')'
`$env:VELASHELL_SSH_INTEROP_X11 = '$(if ($X11) { '1' } else { '0' })'
"@ | Set-Content -Path $envFile -Encoding UTF8

Write-Host ''
Write-Host '服务端已就绪。接下来：' -ForegroundColor Green
Write-Host "  . $envFile"
Write-Host '  dotnet test tests/VelaShell.Ssh.Tests/VelaShell.Ssh.Tests.csproj --filter "TestCategory=Interop"'
Write-Host "  pwsh scripts/ssh/interop/Stop-TestServer.ps1"
