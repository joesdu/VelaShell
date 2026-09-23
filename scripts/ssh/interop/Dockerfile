# SPDX-License-Identifier: MIT
# Copyright 2026 VelaShell Labs
#
# ============================================================================
# 互操作测试用的 sshd —— 我们自己控制配置的那一个
# ============================================================================
#
# 为什么不直接用现成镜像：
#
# `linuxserver/openssh-server` 很好用，但它是 `X11Forwarding no` 而且**没装
# xauth**。X11 转发的成功路径在那上面一条都验不了 —— 而
# velashell-docs/zh/ssh/design/architecture.md §11.2.13 刚刚用一个密钥派生的 bug 证明过：
# **只跑自己的测试桩，验的是「我们对规范的理解与自己一致」，不是「能用」。**
#
# 所以这里自己搭一个：真的 OpenSSH，配置由我们说了算。
#
# 用法（Start-TestServer.ps1 -X11 会自动做）：
#   docker build -t velashell-ssh-interop:x11 scripts/ssh/interop
#   docker run -d -p 2222:2222 -e PUBLIC_KEY="ssh-ed25519 ..." velashell-ssh-interop:x11

ARG ALPINE_VERSION=3.21
FROM alpine:${ALPINE_VERSION}

# xauth 是 X11 转发的**服务端**依赖：sshd 靠它把我们发过去的假 cookie
# 写进远端的 .Xauthority。没有它的话 sshd 会直接拒绝 x11-req。
RUN apk add --no-cache openssh-server openssh-client xauth socat

RUN ssh-keygen -A \
 && adduser -D -s /bin/sh velashell \
 && echo "velashell:velashell" | chpasswd \
 && mkdir -p /home/velashell/.ssh \
 && chown -R velashell:velashell /home/velashell/.ssh \
 && chmod 700 /home/velashell/.ssh

# 这些开关就是这个镜像存在的理由 —— 把互操作用例要验的面全打开。
RUN printf '%s\n' \
    'Port 2222' \
    'PermitRootLogin no' \
    'PasswordAuthentication yes' \
    'PubkeyAuthentication yes' \
    'X11Forwarding yes' \
    'X11UseLocalhost yes' \
    'X11DisplayOffset 10' \
    'AllowTcpForwarding yes' \
    'AllowStreamLocalForwarding yes' \
    'AllowAgentForwarding yes' \
    'PermitTunnel yes' \
    'Compression yes' \
    'Subsystem sftp /usr/lib/ssh/sftp-server' \
    > /etc/ssh/sshd_config.d/velashell.conf \
 && printf '%s\n' 'Include /etc/ssh/sshd_config.d/*.conf' > /tmp/include \
 && cat /tmp/include /etc/ssh/sshd_config > /tmp/merged \
 && mv /tmp/merged /etc/ssh/sshd_config \
 && rm /tmp/include

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 2222
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
