using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Services;

/// <summary>
/// agent 转发的逐次确认:在 UI 线程弹 <see cref="AgentSignPromptView" />,交回用户的裁决。
/// </summary>
/// <remarks>
/// <para>
/// <b>一次只弹一个。</b>几条会话同时有人要签名时排队 —— 同时叠两个模态框,
/// 用户分不清哪个是哪条会话的。排队期间调用方的期限照样在走,等不到的按拒绝。
/// </para>
/// <para>
/// <b>fail-closed</b>(与 <see cref="HostKeyPromptDialogService" /> 同一口径):没有主窗口、弹窗出错、
/// 期限到了或通道关了,一律拒绝;期限到时还开着的窗口当场收起。
/// </para>
/// </remarks>
public sealed class AgentSignPromptDialogService : IAgentSignPrompt
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <inheritdoc />
    public async Task<AgentSignDecision> ConfirmAsync(AgentSignRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AgentSignDecision.Deny;
        }

        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested
                    || Application.Current?.ApplicationLifetime
                        is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                {
                    return AgentSignDecision.Deny;
                }

                var dialog = new AgentSignPromptView { DataContext = new AgentSignPromptViewModel(request) };

                // 期限到了或通道关了:收起窗口。关窗没有 Result,落在下面的拒绝上。
                await using CancellationTokenRegistration _ = cancellationToken.Register(
                    () => Dispatcher.UIThread.Post(() => dialog.Close()));

                AgentSignDecision? result = await dialog.ShowDialog<AgentSignDecision?>(owner);
                return cancellationToken.IsCancellationRequested ? AgentSignDecision.Deny : result ?? AgentSignDecision.Deny;
            });
        }
        catch
        {
            return AgentSignDecision.Deny;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
