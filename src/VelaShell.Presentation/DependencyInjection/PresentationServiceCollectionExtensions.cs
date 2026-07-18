using Microsoft.Extensions.DependencyInjection;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.DependencyInjection;

/// <summary>表现层(视图模型与工作流服务)的依赖注入注册扩展。</summary>
public static class PresentationServiceCollectionExtensions
{
    /// <summary>向容器注册 VelaShell 表现层所需的视图模型与工作流服务。</summary>
    /// <param name="services">要注册服务的服务集合。</param>
    /// <returns>返回同一服务集合以支持链式调用。</returns>
    public static IServiceCollection AddVelaShellPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<TabBarViewModel>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<IConnectionWorkflowService, ConnectionWorkflowService>();
        services.AddSingleton<IConnectionDiagnosticsService, ConnectionDiagnosticsService>();
        services.AddSingleton<ITunnelWorkflowService, TunnelWorkflowService>();
        return services;
    }
}
