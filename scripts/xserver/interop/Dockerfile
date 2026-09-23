# VelaShell.XServer 互操作靶场的客户端一侧:几个真实的 Xlib / XCB 程序。
#   docker build -t velashell-xclients scripts/xserver/interop
# 用法见同目录 Run-Client.ps1。
FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends x11-apps x11-utils x11-xserver-utils xterm xauth xclip \
 && rm -rf /var/lib/apt/lists/*
