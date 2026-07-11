using Avalonia.Media.Imaging;
using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>
/// 关于页贡献者条目(设计 kGwqX,按用户要求仅保留头像与名称):
/// 头像从 GitHub 异步拉取,离线/失败时回退为首字母圆形占位;点击跳转 GitHub 主页。
/// </summary>
public sealed class ContributorViewModel(string handle) : ReactiveObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>GitHub 用户名(不含 @)。</summary>
    public string Handle { get; } = handle;

    public string Display => "@" + Handle;

    public string Url => $"https://github.com/{Handle}";

    /// <summary>头像加载失败时的首字母占位。</summary>
    public string Initial => Handle.Length > 0 ? Handle[..1].ToUpperInvariant() : "?";

    public Bitmap? Avatar
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasAvatar));
        }
    }

    public bool HasAvatar => Avatar is not null;

    /// <summary>拉取 GitHub 头像(72px,幂等;任何失败都静默保留占位)。</summary>
    public async Task LoadAvatarAsync()
    {
        if (Avatar is not null)
        {
            return;
        }
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync($"https://avatars.githubusercontent.com/{Handle}?s=72");
            using var stream = new MemoryStream(bytes);
            Avatar = new(stream);
        }
        catch
        {
            // 离线或 GitHub 不可达:保留首字母占位,不影响关于页其余内容。
        }
    }
}
