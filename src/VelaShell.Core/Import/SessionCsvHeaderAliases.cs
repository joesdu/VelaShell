namespace VelaShell.Core.Import;

/// <summary>
/// CSV 表头的别名:英文的常见叫法,以及中日韩的写法。给从零自己做表格的人用 ——
/// 他们不会去背 <see cref="SessionCsv.Header" /> 里的英文列名,写的多半是「主机」「端口」「用户名」。
/// </summary>
/// <remarks>
/// 这些词是拿来<b>匹配用户输入</b>的,不是界面文案,所以不走资源文件;单放一个文件,是为了让
/// 「C# 里不许写死中文」的守门测试能按文件放行这一处,而不必把整个 <see cref="SessionCsv" /> 放行。
/// 导出永远写英文列名,不随界面语言变。比对前统一小写、去掉空白与 <c>_ - .</c>。
/// </remarks>
internal static class SessionCsvHeaderAliases
{
    /// <summary>各列的别名。</summary>
    public static IReadOnlyList<(SessionCsvColumns Column, string[] Names)> All { get; } =
    [
        (SessionCsvColumns.Id, ["uuid", "guid"]),
        (SessionCsvColumns.Name, ["label", "title", "session", "session name", "名称", "名字", "连接名", "会话名", "名稱", "連線名稱", "名前", "接続名", "이름"]),
        (SessionCsvColumns.Group, ["folder", "分组", "分組", "グループ", "그룹"]),
        (SessionCsvColumns.Protocol, ["type", "scheme", "协议", "協定", "協議", "プロトコル", "프로토콜"]),
        (SessionCsvColumns.Host, ["hostname", "address", "ip", "server", "主机", "地址", "主機", "ホスト", "호스트"]),
        (SessionCsvColumns.Port, ["端口", "連接埠", "ポート", "포트"]),
        (SessionCsvColumns.Username, ["user", "login", "用户名", "用户", "使用者名稱", "使用者", "帳號", "ユーザー名", "ユーザー", "사용자", "사용자 이름"]),
        (SessionCsvColumns.Auth, ["auth method", "authentication", "认证方式", "认证", "驗證方式", "認證方式", "認証方式", "認証", "인증 방식", "인증"]),
        (SessionCsvColumns.Password, ["pass", "pwd", "密码", "密碼", "パスワード", "비밀번호"]),
        (SessionCsvColumns.PrivateKey, ["key", "key file", "identity file", "private key path", "私钥", "私鑰", "秘密鍵", "개인 키"]),
        (SessionCsvColumns.Certificate, ["cert", "certificate file", "证书", "證書", "証明書", "인증서"]),
        (SessionCsvColumns.Credential, ["shared credential", "共享凭据", "凭据", "共用憑證", "憑證", "共有資格情報", "資格情報", "공유 자격 증명", "자격 증명"]),
        (SessionCsvColumns.JumpHost, ["proxy jump", "jump", "bastion", "跳板机", "跳板主机", "跳板", "跳板機", "跳板主機", "踏み台", "踏み台ホスト", "점프 호스트"]),
        (SessionCsvColumns.Tags, ["tag", "标签", "標籤", "タグ", "태그"]),
        (SessionCsvColumns.Notes, ["note", "description", "comment", "remark", "备注", "说明", "備註", "說明", "メモ", "備考", "메모"])
    ];
}
