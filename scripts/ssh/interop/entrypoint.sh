#!/bin/sh
# SPDX-License-Identifier: MIT
# Copyright 2026 VelaShell Labs
#
# 把 PUBLIC_KEY 写进 authorized_keys，然后起 sshd。
# 与 linuxserver 镜像的约定保持一致，这样 Start-TestServer.ps1 两条路一样传参。

set -e

if [ -n "$PUBLIC_KEY" ]; then
    echo "$PUBLIC_KEY" > /home/velashell/.ssh/authorized_keys
    chown velashell:velashell /home/velashell/.ssh/authorized_keys
    chmod 600 /home/velashell/.ssh/authorized_keys
fi

# -e：日志走 stderr，docker logs 里能直接看到 —— 出问题时这是第一手资料。
exec /usr/sbin/sshd -D -e
