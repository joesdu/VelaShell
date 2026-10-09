using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Core.XServer;

namespace VelaShell.ViewModels;

/// <summary>
/// 标题栏 X Server 浮层里的一行:一个连着的 X 程序 —— 叫什么、来自哪个会话、几个窗口、占多少内存,能断开。
/// 浮层开着时每隔一会儿按 <see cref="Key" /> 就地刷新(不整行重建,悬停时冒出来的断开按钮不会一闪就没)。
/// </summary>
public sealed class XServerClientItemViewModel : ReactiveObject
{
    internal XServerClientItemViewModel(XServerClient client) => Update(client);

    /// <summary>断开它用的键(<see cref="XServerClient.Key" />)。</summary>
    public string Key { get; private set; } = "";

    /// <summary>程序名:<c>WM_CLASS</c>,没有退到窗口标题,都没有写「X 程序 #编号」。</summary>
    public string Name
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>名字下面那一行的窗口标题(与名字相同或没有时为空,不显示)。</summary>
    public string Detail
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>从哪来:SSH 会话的 <c>user@host:port</c>,本机程序写「本机」。</summary>
    public string Source
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>「2 个窗口 · 3.4 MB」。</summary>
    public string Stats
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>正抓着整个 X Server(别的 X 程序都在等它):行上标「独占中」。</summary>
    public bool HoldsServerGrab
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>已经以 Retain 模式断开、只剩资源:行上写明,断开就是把资源清掉。</summary>
    public bool Retained
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>按服务端此刻的样子更新(同一个 <see cref="Key" />)。</summary>
    internal void Update(XServerClient client)
    {
        Key = client.Key;
        Name = client.Name.Length > 0 ? client.Name
            : client.Title.Length > 0 ? client.Title
            : Strings.Format("XServer_PanelUnnamed", client.Id);
        Detail = client.Name.Length > 0 && client.Title.Length > 0 && client.Title != client.Name ? client.Title : "";
        Source = client.Source ?? Strings.Get("XServer_PanelLocal");
        Stats = Strings.Format("XServer_PanelStats", client.Windows, RemoteFileInfoViewModel.FormatSize(client.MemoryBytes));
        HoldsServerGrab = client.HoldsServerGrab;
        Retained = client.Retained;
    }
}
