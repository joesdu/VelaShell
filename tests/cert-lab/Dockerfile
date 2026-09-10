# 只认 OpenSSH 用户证书的 sshd 靶机。
#
# 基础镜像由 setup.sh 通过 --build-arg BASE 传入:
#   - 优先用本机已有的 test_sshserver(Tmds.Ssh 测试套件留下的,已为 SSH 测试调好)
#   - 否则回落到 alpine 现装 openssh-server(需要能访问镜像仓库)
ARG BASE=alpine:3.20
FROM ${BASE}

# alpine 基底才需要现装;test_sshserver 基底里 sshd 与 testuser 都已就位。
RUN if [ ! -x /usr/sbin/sshd ]; then \
      apk add --no-cache openssh-server && ssh-keygen -A && \
      adduser -D -s /bin/sh testuser && passwd -u testuser; \
    fi

# CA 的【公钥】进服务器,私钥永远留在签发机 —— 这正是证书方案的意义:
# 服务器只需认识 CA,不必再逐台登记每个人的公钥。
COPY ca.pub /etc/ssh/velashell_user_ca.pub

# 下面每一行都在堵一条回退路径。少堵一条,"连上了"就不再等于"证书生效了" ——
# 走的可能是普通公钥或密码,而你以为验的是证书。这类假阳性最难发现,
# 因为它长得和成功一模一样。
#
# 主 sshd_config 开头就 Include 本目录,而 OpenSSH 是"首次出现者胜",
# 所以 00- 前缀能盖掉发行版自带的同名项。
RUN rm -rf /home/testuser/.ssh /root/.ssh \
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
 && grep -q '^Include /etc/ssh/sshd_config.d/' /etc/ssh/sshd_config \
      || sed -i '1i Include /etc/ssh/sshd_config.d/*.conf' /etc/ssh/sshd_config

EXPOSE 22
CMD ["/usr/sbin/sshd", "-D", "-e"]
