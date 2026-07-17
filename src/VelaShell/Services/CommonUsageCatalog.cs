namespace VelaShell.Services;

/// <summary>
/// 常用 CLI 工具的高频子命令/用法表,作为命令补全的上下文来源:键入
/// "git ch" 时补 "git checkout"、"docker lo" 时补 "docker logs -f" 等。
/// 纯静态查表,不执行远端探测;首词完整(出现空格)后才参与匹配,
/// 单词阶段交给历史与快捷命令,避免噪音。
/// </summary>
internal static class CommonUsageCatalog
{
    private static readonly Dictionary<string, string[]> Usages = new(StringComparer.Ordinal)
    {
        ["git"] =
        [
            "status",
            "pull",
            "push",
            "add .",
            "commit -m \"\"",
            "checkout ",
            "checkout -b ",
            "branch -a",
            "log --oneline -20",
            "diff",
            "stash",
            "stash pop",
            "merge ",
            "rebase ",
            "fetch --all --prune",
            "clone ",
            "reset --hard HEAD",
            "cherry-pick ",
            "log --graph --oneline --all",
            "remote -v",
            "tag ",
            "restore ",
        ],
        ["docker"] =
        [
            "ps -a",
            "images",
            "logs -f ",
            "exec -it ",
            "stats",
            "restart ",
            "stop ",
            "rm ",
            "rmi ",
            "build -t ",
            "pull ",
            "system prune -f",
            "compose up -d",
            "compose down",
            "compose logs -f",
            "compose ps",
            "network ls",
            "volume ls",
            "inspect ",
            "cp ",
            "system df",
        ],
        ["kubectl"] =
        [
            "get pods",
            "get svc",
            "get nodes",
            "get deploy",
            "describe pod ",
            "logs -f ",
            "exec -it ",
            "apply -f ",
            "delete pod ",
            "rollout restart deployment ",
            "top pods",
        ],
        ["systemctl"] =
        [
            "status ",
            "start ",
            "stop ",
            "restart ",
            "enable ",
            "disable ",
            "daemon-reload",
            "list-units --failed",
        ],
        ["journalctl"] = ["-u ", "-f", "-xe", "--since today", "--disk-usage"],
        ["npm"] =
        [
            "install",
            "install -D ",
            "run dev",
            "run build",
            "run test",
            "start",
            "outdated",
            "update",
        ],
        ["pnpm"] = ["install", "add ", "add -D ", "run dev", "run build", "update"],
        ["yarn"] = ["install", "add ", "add -D ", "dev", "build", "upgrade"],
        ["apt"] =
        [
            "update",
            "upgrade -y",
            "install ",
            "remove ",
            "autoremove -y",
            "search ",
            "list --upgradable",
        ],
        ["apt-get"] = ["update", "upgrade -y", "install ", "remove ", "autoremove -y"],
        ["dnf"] = ["install ", "update", "remove ", "search ", "list installed"],
        ["yum"] = ["install ", "update", "remove ", "search "],
        ["pacman"] = ["-Syu", "-S ", "-R ", "-Ss ", "-Q"],
        ["brew"] = ["install ", "upgrade", "update", "uninstall ", "list", "search "],
        ["pip"] = ["install ", "install -U ", "uninstall ", "list", "freeze"],
        ["cargo"] = ["build", "build --release", "run", "test", "check", "clippy", "add "],
        ["dotnet"] =
        [
            "build",
            "run",
            "test",
            "restore",
            "clean",
            "publish -c Release",
            "format",
            "watch run",
            "watch test",
            "new list",
            "new console -n ",
            "new classlib -n ",
            "pack",
            "package add ",
            "package list --outdated",
            "reference add ",
            "solution add ",
            "tool install -g ",
            "tool list -g",
            "tool update -g ",
            "workload list",
            "workload update",
            "sdk check",
            "nuget locals all --clear",
            "nuget push ",
            "store",
            "dev-certs https --trust",
            "user-secrets init",
            "user-secrets set ",
            "user-secrets list",
            "--info",
            "--version",
            "--list-sdks",
            "--list-runtimes",
            "--help",
        ],
        ["tar"] = ["-xzvf ", "-czvf ", "-xvf ", "-tvf "],
        ["ssh-keygen"] = ["-t ed25519 -C \"\"", "-t rsa -b 4096", "-R "],
        ["helm"] =
        [
            "repo update",
            "install ",
            "upgrade --install ",
            "list -A",
            "uninstall ",
            "rollback ",
            "status ",
        ],
        ["terraform"] =
        [
            "init",
            "plan",
            "apply",
            "apply -auto-approve",
            "destroy",
            "fmt",
            "validate",
            "state list",
            "output",
        ],
        ["go"] =
        [
            "build ./...",
            "run .",
            "test ./...",
            "mod tidy",
            "vet ./...",
            "fmt ./...",
            "get -u ",
        ],
        ["ufw"] =
        [
            "status verbose",
            "allow ",
            "deny ",
            "delete ",
            "enable",
            "disable",
            "reload",
        ],
        ["firewall-cmd"] =
        [
            "--list-all",
            "--reload",
            "--get-active-zones",
            "--permanent --add-port=",
        ],
        ["nginx"] = ["-t", "-s reload", "-V"],
        ["certbot"] = ["renew --dry-run", "renew", "certificates"],
        ["crontab"] = ["-l", "-e", "-l -u "],
        ["mvn"] =
        [
            "clean install",
            "clean package -DskipTests",
            "test",
            "spring-boot:run",
            "dependency:tree",
        ],
        ["gradle"] = ["build", "test", "clean build", "bootRun", "dependencies"],
        ["podman"] = ["ps -a", "images", "logs -f ", "exec -it ", "system prune -f"],
        ["rsync"] = ["-avz --progress ", "-avz --delete "],
    };

    /// <summary>
    /// 按 "工具 前缀" 返回完整候选("git ch" → "git checkout "…)。首词不在表中
    /// 或尚未键入空格时返回空。
    /// </summary>
    public static IEnumerable<string> Complete(string prefix)
    {
        int space = prefix.IndexOf(' ');
        if (space <= 0)
        {
            yield break;
        }
        string tool = prefix[..space];
        if (!Usages.TryGetValue(tool, out string[]? usages))
        {
            yield break;
        }
        foreach (string usage in usages)
        {
            string candidate = $"{tool} {usage}";
            if (
                candidate.Length > prefix.Length
                && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            )
            {
                yield return candidate.TrimEnd();
            }
        }
    }
}
