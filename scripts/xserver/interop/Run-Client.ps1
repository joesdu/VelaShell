<#
.SYNOPSIS
    在 Docker 里跑一个真实的 X 客户端,连到本机的 VelaShell.XServer(run-server.cs 起的那个)。

.EXAMPLE
    # 先在另一个终端:dotnet run scripts/xserver/interop/run-server.cs -- 42 ./xshots
    ./scripts/xserver/interop/Run-Client.ps1 -Cookie (Get-Content ./xshots/cookie.txt) -Command 'xdpyinfo'
    ./scripts/xserver/interop/Run-Client.ps1 -Cookie (Get-Content ./xshots/cookie.txt) -Command 'timeout 5 xterm -e "ls -la /; sleep 3"'

.NOTES
    镜像:docker build -t velashell-xclients scripts/xserver/interop
    容器经 host.docker.internal 连回本机,连接不是从 127.0.0.1 来的,所以服务端必须配 cookie(run-server.cs 已配)。
#>
param(
    [Parameter(Mandatory)] [string] $Cookie,
    [Parameter(Mandatory)] [string] $Command,
    [int] $Display = 42,
    [string] $Image = 'velashell-xclients'
)

$script = "xauth -q add host.docker.internal:$Display MIT-MAGIC-COOKIE-1 $Cookie 2>/dev/null; export DISPLAY=host.docker.internal:$Display; $Command"
docker run --rm --add-host=host.docker.internal:host-gateway $Image sh -c $script
