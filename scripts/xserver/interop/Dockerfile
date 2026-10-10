# VelaShell.XServer 互操作靶场的客户端一侧:几个真实的 Xlib / XCB 程序。
#   docker build -t velashell-xclients scripts/xserver/interop
# 用法见同目录 Run-Client.ps1。
# default-jdk:Swing 程序(RealClientTests 的 Java 用例,源文件直接 java X.java 跑)验窗口管理器的判定。
# twm:单窗口(rootful)模式下由远端的窗口管理器接管顶层(F13)。
FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends x11-apps x11-utils x11-xserver-utils xterm xauth xclip xdotool x11-xkb-utils \
    xinput mesa-utils default-jdk twm \
 && rm -rf /var/lib/apt/lists/*
