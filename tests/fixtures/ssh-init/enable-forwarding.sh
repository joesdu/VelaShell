#!/usr/bin/with-contenv bash
# linuxserver/openssh-server 的 custom-cont-init.d 钩子:容器起来前跑一次。
#
# 为什么需要它:这个镜像的模板默认写死 `AllowTcpForwarding no`,而
# **sshd 把 streamlocal 的远程转发也一并挡在那道闸后面** —— 实测(OpenSSH / Alpine)
# 即便 `AllowStreamLocalForwarding yes`,只要 `AllowTcpForwarding no`,
# `streamlocal-forward@openssh.com` 就回 SSH_MSG_REQUEST_FAILURE,
# 日志里是 "Received request ... to remote forward to path ..., but the request was denied."
#
# VelaShell 的 SSH agent 转发正是建立在这条通道上(见 `plan.md` §58),
# 所以端到端用例(`SshAgentForwardIntegrationTests`)需要把它打开。
# 只影响这个一次性测试容器,不代表任何生产建议。
set -eu

CONFIG=/config/sshd/sshd_config
[ -f "$CONFIG" ] || exit 0

sed -i 's/^AllowTcpForwarding .*/AllowTcpForwarding yes/' "$CONFIG"
grep -q '^AllowTcpForwarding' "$CONFIG" || echo 'AllowTcpForwarding yes' >> "$CONFIG"
grep -q '^AllowStreamLocalForwarding' "$CONFIG" || echo 'AllowStreamLocalForwarding yes' >> "$CONFIG"
