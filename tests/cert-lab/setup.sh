#!/bin/sh
# 搭一台"只认 OpenSSH 用户证书"的 sshd 靶机,供 SshCertificateIntegrationTests 使用。
#
#   sh tests/cert-lab/setup.sh          搭好并启动
#   sh tests/cert-lab/setup.sh --down   拆掉
#
# 跑完按提示导出 VELASHELL_CERT_LAB,那两条集成测试才会真的执行(否则记为"未执行")。
set -e
cd "$(dirname "$0")"

NAME=velashell-cert-lab
PORT=${VELASHELL_CERT_PORT:-2223}

if [ "$1" = "--down" ]; then
    docker rm -f "$NAME" >/dev/null 2>&1 && echo "已停掉 $NAME" || echo "$NAME 未在运行"
    exit 0
fi

# ---- 1. CA、用户密钥、证书 ----
# 已经生成过就不再重来:重签会让证书换一把 CA,而靶机镜像里烤的是旧的那把公钥。
if [ ! -f ca ]; then
    echo "==> 生成 CA 与用户密钥"
    ssh-keygen -t ed25519 -f ca          -C velashell-test-ca   -N "" -q
    ssh-keygen -t ed25519 -f id_ed25519  -C velashell-test-user -N "" -q
    # -n testuser 是 principals:证书只对这个登录名有效。
    # -V 往前留 5 分钟,避开与容器之间的时钟偏差。
    ssh-keygen -s ca -I velashell-lab -n testuser -V -5m:+7d id_ed25519.pub
else
    echo "==> 复用已有的 CA 与证书(要重来就先删掉 tests/cert-lab/ca*)"
fi
chmod 600 id_ed25519 2>/dev/null || true

# ---- 2. 选基础镜像 ----
# 拉不到公共仓库时(常见于国内网络),本机若有 Tmds.Ssh 测试套件留下的
# test_sshserver 就直接拿来用,省掉一次外网拉取。
if docker image inspect test_sshserver:latest >/dev/null 2>&1; then
    BASE=test_sshserver:latest
else
    BASE=alpine:3.20
fi
echo "==> 基础镜像: $BASE"

# ---- 3. build & run ----
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker build -q --build-arg BASE="$BASE" -t "$NAME" . >/dev/null
docker run -d --name "$NAME" -p "$PORT":22 "$NAME" >/dev/null
sleep 1

# ---- 4. 自检:配置真的只剩证书这一条路吗 ----
echo "==> 生效配置"
docker exec "$NAME" sshd -T 2>/dev/null | grep -iE \
    "^(trustedusercakeys|authorizedkeysfile|passwordauthentication|gssapiauthentication|kbdinteractiveauthentication) " \
    | sed 's/^/    /'

cat <<EOF

靶机已就绪:127.0.0.1:$PORT,用户 testuser

跑集成测试:
    VELASHELL_CERT_LAB="\$(pwd)" dotnet test tests/VelaShell.Core.Tests/VelaShell.Core.Tests.csproj \
        --filter "FullyQualifiedName~SshCertificateIntegrationTests"

在 VelaShell 界面里手测 —— 认证方式选「证书认证」,证书文件选:
    $(pwd)/id_ed25519-cert.pub
  (私钥会按 -cert.pub 约定自动补上,这一步顺带验了自动推断)

看服务端怎么说:
    docker logs $NAME | grep -i "Accepted certificate"

拆掉:
    sh tests/cert-lab/setup.sh --down
EOF
