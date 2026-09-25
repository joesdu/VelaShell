#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright 2026 VelaShell Labs
#
# 在**容器里**跑：让 sshd 出示一张主机证书（HostCertificate），供主机证书的互操作用例验证。
# 用法（从宿主机，先把签好的证书 docker cp 进容器）：
#   docker exec <容器> sh /tmp/host-cert.sh /config/ssh_host_keys/ssh_host_ed25519_key-cert.pub
#
# 与 trust-ca.sh 分开放的理由相同：这段 sh 塞进 PowerShell 的 here-string 里，
# 两层转义叠起来之后「这一行到底会变成什么」谁也说不清。

set -e

certificate="$1"
if [ -z "$certificate" ] || [ ! -f "$certificate" ]; then
    echo "主机证书不存在：$certificate" >&2
    exit 1
fi

# 配置文件的位置从 sshd 的命令行里问出来（见 trust-ca.sh 的说明）。
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

chmod 644 "$certificate"
grep -q "^HostCertificate $certificate\$" "$conf" || echo "HostCertificate $certificate" >> "$conf"

kill -HUP "$pid"

echo "HostCertificate 已配好：$certificate"
