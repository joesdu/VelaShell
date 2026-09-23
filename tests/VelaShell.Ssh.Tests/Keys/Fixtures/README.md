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

## 这些钥是公开的

它们只存在于本仓库的用例里，从未用于任何真实主机，口令也写在上面。
**不要把它们当成"泄漏的密钥"处理** —— 也不要拿它们去连任何东西。
