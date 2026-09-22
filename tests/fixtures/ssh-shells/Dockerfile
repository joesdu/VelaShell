# 多 shell SSH 测试服务端 —— 「文件浏览器跟随终端目录」的真机验证靶子。
#
# 为什么不复用 docker-compose.test.yml 里那台 linuxserver/openssh-server:
# 它的 sshd 以**普通用户**身份运行(rootless),没法 setuid,因此一台只认一个用户。
# 而这个功能要验的恰恰是「同一份代码面对 bash / zsh / fish / dash / ash 各自怎么表现」,
# 必须能按不同的登录 shell 分别登进去。
#
# 每个用户的登录 shell 不同,口令一律 velapass:
#   vela-bash / vela-zsh / vela-fish / vela-dash / vela-ash
# 另有两个"用户自己已经动过手脚"的账号,用来验注入与用户配置互不打扰:
#   vela-pyenv  —— PROMPT_COMMAND 结尾自带分号(pyenv-virtualenv 的真实形状,曾拼出 `;;` 语法错误)
#   vela-starship —— rc 里每次都重写 PS1(starship / p10k 那一类的最小复现)
FROM alpine:3.21

RUN apk add --no-cache openssh bash zsh fish dash \
    && ssh-keygen -A \
    && printf '/bin/bash\n/bin/zsh\n/usr/bin/fish\n/usr/bin/dash\n/bin/ash\n' >> /etc/shells

# sshd 整份重写:Alpine 自带的那份没有 sshd_config.d 的 Include,追加不如直写清楚。
# PerSourcePenalties 必须关:Docker 的 NAT 让所有连接都来自同一个网关地址,而每个用例的
# RequireContainer 都先开一条「不认证就关」的 TCP 探测 —— OpenSSH 9.8+ 把它记为 noauth 罚分,
# 攒满之后整批连接在版本交换前就被丢掉。症状是跑完一轮紧接着再跑就全红,而且跨实现串味。
RUN printf '%s\n' \
    'Port 2222' \
    'PermitRootLogin no' \
    'PasswordAuthentication yes' \
    'KbdInteractiveAuthentication no' \
    'UsePAM no' \
    'PrintMotd no' \
    'AcceptEnv LANG LC_*' \
    'Subsystem sftp internal-sftp' \
    'PerSourcePenalties no' \
    'MaxStartups 100' \
    > /etc/ssh/sshd_config

RUN set -eu; \
    for spec in \
        'vela-bash:/bin/bash' \
        'vela-zsh:/bin/zsh' \
        'vela-fish:/usr/bin/fish' \
        'vela-dash:/usr/bin/dash' \
        'vela-ash:/bin/ash' \
        'vela-pyenv:/bin/bash' \
        'vela-starship:/bin/bash' \
    ; do \
        user="${spec%%:*}"; shell="${spec#*:}"; \
        adduser -D -s "$shell" "$user"; \
        echo "$user:velapass" | chpasswd; \
    done

# pyenv-virtualenv 在 PROMPT_COMMAND 原本为空时就是设成带尾分号的那种形状。
# 写进 ~/.profile 而不是 ~/.bashrc:sshd 给交互式会话起的是**登录** shell,读的是 profile 系列。
# 旧版钩子在这里会拼出 `_pyenv_virtualenv_hook;;vela_shell_osc7` —— `;;` 出了 case 就是语法错误,
# 用户每敲一次回车看一行报错。这个账号把那一幕原样摆在真机上。
RUN printf '%s\n' \
    '_pyenv_virtualenv_hook() { :; }' \
    'PROMPT_COMMAND="_pyenv_virtualenv_hook;"' \
    >> /home/vela-pyenv/.profile

# starship / powerlevel10k 那一类:提示符由别人全权接管,每次都重写 PS1。
# 我们的片段绝不能依赖 PS1 里留下的东西(bash/zsh 走的是提示符钩子,不碰 PS1,正是为此)。
RUN printf '%s\n' \
    '__vela_fake_starship() { PS1="[fake-starship] \\w \\$ "; }' \
    'PROMPT_COMMAND="__vela_fake_starship"' \
    >> /home/vela-starship/.profile

EXPOSE 2222
CMD ["/usr/sbin/sshd", "-D", "-e"]
