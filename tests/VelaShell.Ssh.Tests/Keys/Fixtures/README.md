# 加密私钥样本

这里的私钥**全部由真的 `ssh-keygen`（OpenSSH 10.5p1）生成**，口令统一是：

```
correct horse battery staple
```

## 为什么不自己拼

本仓库其余的私钥用例（`PrivateKeyFileTests`）是**按格式现拼**出来的，
因为拼的过程本身就在验证我们对格式的理解。加密私钥这里反过来，必须用外部产物：

加密侧要是也由我们写，它和解密侧会**一起错** —— `bcrypt_pbkdf` 的轮数、
输出的字节序、密钥与 IV 的派生次序，随便哪一处理解偏了，自加密自解密都照样通过，
而拿到别人的钥就静默失败。这类错不会报错，只会在特定输入上给出错误结果。

所以标准答案由 OpenSSH 出：`.pub` 里那段 blob 是它写的，
用例断言「我们解出来的公钥与它逐字节相同」。

## 怎么重新生成

```bash
cd tests/VelaShell.Ssh.Tests/Keys/Fixtures
rm -f ed25519-* rsa-* ecdsa-*
PASS='correct horse battery staple'
gen() { local name=$1 type=$2 cipher=$3; shift 3
        ssh-keygen -q -t "$type" -f "./$name" -N "$PASS" \
                   -C "velashell-ssh test key ($name)" -Z "$cipher" "$@" </dev/null; }

gen ed25519-aes256ctr   ed25519 aes256-ctr
gen ed25519-aes256cbc   ed25519 aes256-cbc
gen ed25519-aes128ctr   ed25519 aes128-ctr
gen ed25519-aes256gcm   ed25519 aes256-gcm@openssh.com
gen ed25519-chachapoly  ed25519 chacha20-poly1305@openssh.com
gen ed25519-rounds64    ed25519 aes256-ctr -a 64
gen rsa-aes256ctr       rsa     aes256-ctr -b 2048
gen ecdsa-aes256ctr     ecdsa   aes256-ctr -b 256

ssh-keygen -q -t ed25519 -f ./ed25519-plain -N "" \
           -C "velashell-ssh test key (plain)" </dev/null
```

### 传统加密 PEM（`Proc-Type: 4,ENCRYPTED`）

OpenSSH 7.8 之前加口令时默认写这种格式（口令只经一次 MD5 派生、常配 3DES）。本库**不读**这种过时格式，
这几份样本用来验证它会被清楚地拒绝、并给出转换办法（`ssh-keygen -p`），而不是被误报成「口令不对」。
`legacy-rsa-aes128` 是 `ssh-keygen -m PEM` 写的（现实里最常见的那种）；3DES 与 EC 的两份由 `openssl` 写。

```bash
PASS='correct horse battery staple'
ssh-keygen -q -t rsa -b 2048 -m PEM -N "$PASS" \
           -C "velashell-ssh test key (legacy-rsa-aes128)" -f ./legacy-rsa-aes128 </dev/null
openssl genrsa 2048 | openssl rsa -des3 -traditional -passout "pass:$PASS" -out legacy-rsa-des3
openssl ecparam -name prime256v1 -genkey -noout | openssl ec -aes256 -passout "pass:$PASS" -out legacy-ecdsa-aes256
```

### 主机证书（`hostcert-*`）

`HostCertificateTests` 用的主机证书，全部由 `ssh-keygen -s` 签发，私钥不加密。
验签范围、字段边界这类理解偏差，只有对着别人签的证书才看得出来 —— 自己签自己验，两边会一起错。

| 文件 | 用途 |
| --- | --- |
| `hostcert-ca` / `hostcert-ca-rsa` / `hostcert-other-ca` | 签发用的 CA（ed25519 / RSA 3072 / 一个不被信任的 ed25519） |
| `hostcert-key`、`hostcert-ecdsa`、`hostcert-rsa`、`hostcert-rsa1024` | 主机密钥（ed25519 / P-256 / RSA 2048 / RSA 1024，最后一把用来验 RSA 长度下限） |
| `hostcert-key-cert.pub` | 合格：主体 `server.example,10.0.0.1`，永久有效 |
| `hostcert-window-cert.pub` | 有效期 2026-01-01 到 2027-01-01（用例传入固定时刻，不会随日期过期） |
| `hostcert-noprincipals-cert.pub` / `hostcert-usertype-cert.pub` | 没列主体 / 用户证书 |
| `hostcert-sha1-cert.pub` / `hostcert-rsasha512-cert.pub` | RSA CA 用 `ssh-rsa`（SHA-1）/ `rsa-sha2-512` 签 |
| `hostcert-othersigned-cert.pub` | 别的 CA 签的 |

```bash
k() { ssh-keygen -q -t "$1" -f "./$2" -N "" -C "velashell-ssh test key ($2)" "${@:3}" </dev/null; }
k ed25519 hostcert-ca;  k rsa hostcert-ca-rsa -b 3072;  k ed25519 hostcert-other-ca
k ed25519 hostcert-key; k ecdsa hostcert-ecdsa -b 256
k rsa hostcert-rsa -b 2048; k rsa hostcert-rsa1024 -b 1024

# 同一把主机钥签多张证书：ssh-keygen 把证书写到「<输入名>-cert.pub」，所以先复制成不同的名字，签完删掉副本。
for n in window noprincipals usertype sha1 rsasha512 othersigned; do cp hostcert-key.pub hostcert-$n.pub; done
s() { ssh-keygen -q "$@" </dev/null; }
s -s hostcert-ca -h -I host-valid -n server.example,10.0.0.1 hostcert-key.pub
s -s hostcert-ca -h -I host-window -n server.example -V 20260101:20270101 hostcert-window.pub
s -s hostcert-ca -h -I host-noprincipals hostcert-noprincipals.pub
s -s hostcert-ca -I host-usertype -n server.example hostcert-usertype.pub
s -s hostcert-ca-rsa -t ssh-rsa -h -I host-sha1 -n server.example hostcert-sha1.pub
s -s hostcert-ca-rsa -t rsa-sha2-512 -h -I host-rsasha512 -n server.example hostcert-rsasha512.pub
s -s hostcert-other-ca -h -I host-othersigned -n server.example hostcert-othersigned.pub
s -s hostcert-ca -h -I host-ecdsa -n server.example hostcert-ecdsa.pub
s -s hostcert-ca -h -I host-rsa -n server.example hostcert-rsa.pub
s -s hostcert-ca -h -I host-rsa1024 -n server.example hostcert-rsa1024.pub
for n in window noprincipals usertype sha1 rsasha512 othersigned; do rm hostcert-$n.pub; done
```

重新生成之后，`HostCertificateTests` 里那条指纹断言（`ssh-keygen -lf hostcert-key-cert.pub` 的输出）要跟着改。

## 这些钥是公开的

它们只存在于本仓库的用例里，从未用于任何真实主机，口令也写在上面。
**不要把它们当成"泄漏的密钥"处理** —— 也不要拿它们去连任何东西。
