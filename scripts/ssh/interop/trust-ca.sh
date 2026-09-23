#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright 2026 VelaShell Labs
#
# 在**容器里**跑：把一把 CA 公钥装进 sshd 的 TrustedUserCAKeys，让证书认证可用。
# 用法（从宿主机）：
#   Get-Content trust-ca.sh -Raw | docker exec -i <容器> sh -s -- "<ca 公钥一行>"
#
# 单独成文件，而不是塞进 Start-TestServer.ps1 的 here-string：
# 那里面要同时躲开 PowerShell 的 $ 展开与 sh 的转义，两层叠起来之后
# 「这一行到底会变成什么」谁也说不清 —— 而这段脚本一旦错了，
# 症状是证书认证静默地连不上，查起来一点线索都没有。

set -e

ca_public_key="$1"
if [ -z "$ca_public_key" ]; then
    echo '没传 CA 公钥。' >&2
    exit 1
fi

# ⚠️ **配置文件的位置要从 sshd 的命令行里问出来，不能写死。**
# linuxserver 的镜像跑的是 `sshd -f /config/sshd/sshd_config`，
# 改错文件的表现极具迷惑性：文件里明明写着 TrustedUserCAKeys，
# 服务端却照样拒绝 —— 因为那份根本没被读。
pid=''
conf='/etc/ssh/sshd_config'

for p in /proc/[0-9]*; do
    [ -r "$p/cmdline" ] || continue
    cmd=$(tr '\000' ' ' < "$p/cmdline" 2>/dev/null) || continue
    case "$cmd" in
        *sshd*listener*|*sshd*-D*)
            pid="${p#/proc/}"
            f=$(printf '%s' "$cmd" | sed -n 's/.*-f \([^ ][^ ]*\).*/\1/p')
            [ -n "$f" ] && conf="$f"
            break
            ;;
    esac
done

[ -n "$pid" ] || { echo '在容器里找不到 sshd 进程' >&2; exit 1; }
[ -f "$conf" ] || { echo "sshd 说的配置文件不存在：$conf" >&2; exit 1; }
echo "sshd pid=$pid 配置=$conf"

printf '%s\n' "$ca_public_key" > /etc/ssh/velashell_ca.pub
chmod 644 /etc/ssh/velashell_ca.pub

if grep -q '^TrustedUserCAKeys' "$conf"; then
    sed -i 's#^TrustedUserCAKeys .*#TrustedUserCAKeys /etc/ssh/velashell_ca.pub#' "$conf"
else
    echo 'TrustedUserCAKeys /etc/ssh/velashell_ca.pub' >> "$conf"
fi

kill -HUP "$pid"

grep -q '^TrustedUserCAKeys /etc/ssh/velashell_ca.pub' "$conf" \
    || { echo 'TrustedUserCAKeys 没写进去' >&2; exit 1; }

echo 'TrustedUserCAKeys 已配好'
