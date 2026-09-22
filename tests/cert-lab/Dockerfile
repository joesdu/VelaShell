# 只认 OpenSSH 用户证书的 sshd 靶机。
#
# 基础镜像由 setup.sh 通过 --build-arg BASE 传入:优先挑本机**已经有**的那一个
# (Docker Hub 在不少网络下拉不动,现装 openssh-server 会在第一步就失败),
# 实在没有才回落到 alpine 现装。
ARG BASE=alpine:3.20
FROM ${BASE}

# 各基底的 sshd 落点不一样:linuxserver 那份叫 sshd.pam,alpine 装出来的叫 sshd。
# 统一软链到 /usr/local/sbin/sshd,CMD 就不必跟着基底变。
# testuser 同理:自带就用自带的,没有才建。
RUN set -e; \
    if   [ -x /usr/sbin/sshd ];     then SSHD=/usr/sbin/sshd; \
    elif [ -x /usr/sbin/sshd.pam ]; then SSHD=/usr/sbin/sshd.pam; \
    else apk add --no-cache openssh-server; SSHD=/usr/sbin/sshd; fi; \
    mkdir -p /usr/local/sbin; ln -sf "$SSHD" /usr/local/sbin/sshd; \
    ssh-keygen -A; \
    id testuser >/dev/null 2>&1 || adduser -D -s /bin/sh testuser

# CA 的【公钥】进服务器,私钥永远留在签发机 —— 这正是证书方案的意义:
# 服务器只需认识 CA,不必再逐台登记每个人的公钥。
COPY ca.pub /etc/ssh/velashell_user_ca.pub

# 下面每一行都在堵一条回退路径。少堵一条,"连上了"就不再等于"证书生效了" ——
# 走的可能是普通公钥或密码,而你以为验的是证书。这类假阳性最难发现,
# 因为它长得和成功一模一样。
#
# OpenSSH 是"首次出现者胜",所以配置要经主文件开头的 Include 进来,00- 前缀
# 才盖得掉发行版自带的同名项。发行版多半把那行 Include **注释掉**了(linuxserver
# 的镜像就是),所以先取消注释,没有那一行的再插一行。
#
# 账号还得是**没被锁**的:adduser 建出来的用户 shadow 里是 "!",而 sshd 对锁着的账号
# 连公钥认证都直接拒("account is locked"),于是证书对了也登不上。把口令字段置成 "*" ——
# 不是锁,也照样没有口令可用(何况 PasswordAuthentication 已经关了)。
#
# 最后是**硬自检**:sshd -T 的实际生效配置里没出现我们的 CA、或还留着别的认证路径、
# 或账号是锁着的,就让构建当场失败。少了它,一台配置没生效的靶机会让"连上了"一路绿着 ——
# 那正是这套用例要防的假阳性。
# (AuthorizedKeysFile none 的表现是这一项**根本不出现**:没有文件可查,自然没得打印。)
RUN rm -rf /home/testuser/.ssh /root/.ssh \
 && sed -i 's|^testuser:[^:]*:|testuser:*:|' /etc/shadow \
 && mkdir -p /etc/ssh/sshd_config.d \
 && printf '%s\n' \
      'TrustedUserCAKeys /etc/ssh/velashell_user_ca.pub' \
      'PubkeyAuthentication yes' \
      'AuthorizedKeysFile none' \
      'PasswordAuthentication no' \
      'PermitEmptyPasswords no' \
      'KbdInteractiveAuthentication no' \
      'GSSAPIAuthentication no' \
      'PermitRootLogin no' \
      'LogLevel VERBOSE' \
      > /etc/ssh/sshd_config.d/00-cert-only.conf \
 && sed -i 's|^#[[:space:]]*Include /etc/ssh/sshd_config.d/|Include /etc/ssh/sshd_config.d/|' /etc/ssh/sshd_config \
 && { grep -q '^Include /etc/ssh/sshd_config.d/' /etc/ssh/sshd_config \
      || sed -i '1i Include /etc/ssh/sshd_config.d/*.conf' /etc/ssh/sshd_config; } \
 && { /usr/local/sbin/sshd -t 2>&1 | grep -q 'Unsupported option GSSAPIAuthentication' \
      && sed -i '/^GSSAPIAuthentication /d' /etc/ssh/sshd_config.d/00-cert-only.conf \
      || true; } \
 && /usr/local/sbin/sshd -t \
 && /usr/local/sbin/sshd -T > /tmp/effective.conf \
 && grep -qix 'trustedusercakeys /etc/ssh/velashell_user_ca.pub' /tmp/effective.conf \
 && grep -qix 'pubkeyauthentication yes'                          /tmp/effective.conf \
 && grep -qix 'passwordauthentication no'                         /tmp/effective.conf \
 && grep -qix 'kbdinteractiveauthentication no'                   /tmp/effective.conf \
 && ! grep -qi '^authorizedkeysfile '                             /tmp/effective.conf \
 && ! grep -q  '^testuser:!'                                      /etc/shadow \
 && rm -f /tmp/effective.conf

EXPOSE 22

# 基底镜像可能自带 ENTRYPOINT(linuxserver 那套 s6 init 就是),不清掉的话我们的 CMD
# 只是被当成参数塞给它 —— 结果是**它那份 sshd 也跟着起来**,靶机上就多了一条我们没配过的
# 认证路径。这台机器存在的意义就是"除了证书哪条路都不通",所以必须清掉。
ENTRYPOINT []
CMD ["/usr/local/sbin/sshd", "-D", "-e"]
