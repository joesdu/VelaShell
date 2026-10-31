using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 变量询问框记住上一次填的值:按快捷命令 + 占位名记,只在进程里;凭据类与留空的不记。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandVariableMemoryTests
{
    private static readonly QuickCommandTemplate Logs = QuickCommandTemplate.Parse("kubectl logs -f {{pod}} -n {{ns=default}}");

    [TestMethod]
    public void NothingRemembered_PrefillsTheDefaults()
    {
        var memory = new QuickCommandVariableMemory();

        IReadOnlyDictionary<string, string> initial = memory.Prefill(Guid.NewGuid(), Logs.Text, Logs);

        Assert.AreEqual("", initial["pod"]);
        Assert.AreEqual("default", initial["ns"]);
    }

    [TestMethod]
    public void RememberedValues_ArePrefilledForTheSameCommandOnly()
    {
        var memory = new QuickCommandVariableMemory();
        var logs = Guid.NewGuid();
        memory.Remember(logs, Logs.Text, Logs, new Dictionary<string, string> { ["pod"] = "web-1", ["ns"] = "prod" });

        IReadOnlyDictionary<string, string> again = memory.Prefill(logs, Logs.Text, Logs);
        Assert.AreEqual("web-1", again["pod"]);
        Assert.AreEqual("prod", again["ns"], "记下的值压过占位自己的默认值");

        IReadOnlyDictionary<string, string> other = memory.Prefill(Guid.NewGuid(), Logs.Text, Logs);
        Assert.AreEqual("", other["pod"], "按快捷命令记:另一条命令里同名的占位不预填");
    }

    [TestMethod]
    public void CommandsWithoutId_AreKeyedByTheirText()
    {
        var memory = new QuickCommandVariableMemory();
        memory.Remember(null, Logs.Text, Logs, new Dictionary<string, string> { ["pod"] = "web-1", ["ns"] = "prod" });

        Assert.AreEqual("web-1", memory.Prefill(null, Logs.Text, Logs)["pod"]);
        Assert.AreEqual("web-1", memory.Prefill(Guid.Empty, Logs.Text, Logs)["pod"], "Guid.Empty 当没有 id");
    }

    [TestMethod]
    public void CredentialLikeVariables_AreNeverRemembered()
    {
        // 询问框是明文输入框:预填一个口令就等于把它摆在屏幕上。
        var template = QuickCommandTemplate.Parse("mysql -u {{user}} -p{{password}} --token={{api_token}} --otp={{OTP}} {{db_密码}}");
        var memory = new QuickCommandVariableMemory();
        var id = Guid.NewGuid();
        memory.Remember(id, template.Text, template, new Dictionary<string, string>
        {
            ["user"] = "app",
            ["password"] = "s3cret",
            ["api_token"] = "t0k",
            ["OTP"] = "123456",
            ["db_密码"] = "x"
        });

        IReadOnlyDictionary<string, string> initial = memory.Prefill(id, template.Text, template);

        Assert.AreEqual("app", initial["user"]);
        Assert.AreEqual("", initial["password"]);
        Assert.AreEqual("", initial["api_token"]);
        Assert.AreEqual("", initial["OTP"]);
        Assert.AreEqual("", initial["db_密码"]);
        Assert.IsTrue(QuickCommandVariableMemory.IsSensitive("passwd"));
        Assert.IsFalse(QuickCommandVariableMemory.IsSensitive("pod"));
    }

    [TestMethod]
    [DataRow("pw")]
    [DataRow("db_pw")]
    [DataRow("pin")]
    [DataRow("sim_pin")]
    [DataRow("PIN")]
    [DataRow("dbPin")]
    [DataRow("key")]
    [DataRow("sshKey")]
    [DataRow("access_key")]
    [DataRow("auth")]
    [DataRow("OAuth")]
    [DataRow("mfa_code")]
    [DataRow("user_2fa")]
    [DataRow("jwt")]
    [DataRow("bearer")]
    [DataRow("session_cookie")]
    [DataRow("凭据")]
    [DataRow("登录凭证")]
    [DataRow("秘钥")]
    [DataRow("憑證")]
    [DataRow("暗証番号")]
    [DataRow("パスコード")]
    [DataRow("암호")]
    public void CommonCredentialNames_AreSensitive(string name) =>
        Assert.IsTrue(QuickCommandVariableMemory.IsSensitive(name), name);

    [TestMethod]
    [DataRow("pod")]
    [DataRow("ping_host")]
    [DataRow("mapping")]
    [DataRow("keyspace")]
    [DataRow("monkey")]
    [DataRow("author")]
    [DataRow("namespace")]
    [DataRow("目录")]
    public void ShortWords_OnlyCountAsWholeWords(string name) =>
        Assert.IsFalse(QuickCommandVariableMemory.IsSensitive(name), name);

    [TestMethod]
    public void SensitiveValues_AreWhatEndsUpInTheCommand_ForCredentialVariablesOnly()
    {
        var template = QuickCommandTemplate.Parse("login {{user}} -p {{password=changeme}} --pin={{pin}} --otp={{otp}}");

        IReadOnlyList<string> secrets = QuickCommandVariableMemory.SensitiveValues(template, new Dictionary<string, string>
        {
            ["user"] = "app",
            ["pin"] = "12\n34",
            ["otp"] = "  ",
        });

        Assert.AreSequenceEqual(["changeme", "12", "34"], secrets.ToArray(),
            "没给的用默认值(与替换同一口径);值里的换行替换时成了空格,按段认;空白的不算;普通变量不算");
    }

    [TestMethod]
    public void BlankAnswer_ForgetsTheOldValue_SoTheDefaultComesBack()
    {
        var memory = new QuickCommandVariableMemory();
        var id = Guid.NewGuid();
        memory.Remember(id, Logs.Text, Logs, new Dictionary<string, string> { ["pod"] = "web-1", ["ns"] = "prod" });

        memory.Remember(id, Logs.Text, Logs, new Dictionary<string, string> { ["pod"] = "web-2", ["ns"] = "  " });

        IReadOnlyDictionary<string, string> initial = memory.Prefill(id, Logs.Text, Logs);
        Assert.AreEqual("web-2", initial["pod"]);
        Assert.AreEqual("default", initial["ns"], "留空多半是想要默认值");
    }

    [TestMethod]
    public void OldestCommand_IsDroppedPastTheCap()
    {
        var memory = new QuickCommandVariableMemory();
        var first = Guid.NewGuid();
        memory.Remember(first, Logs.Text, Logs, new Dictionary<string, string> { ["pod"] = "web-1" });
        for (int i = 0; i < QuickCommandVariableMemory.MaxCommands; i++)
        {
            memory.Remember(Guid.NewGuid(), Logs.Text, Logs, new Dictionary<string, string> { ["pod"] = $"p{i}" });
        }

        Assert.AreEqual("", memory.Prefill(first, Logs.Text, Logs)["pod"]);
    }
}
