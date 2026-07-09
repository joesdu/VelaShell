using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.DependencyInjection;

public static class PresentationServiceCollectionExtensions
{
    public static IServiceCollection AddVelaShellPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<WorkspaceHostViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton<TabBarViewModel>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<IConnectionWorkflowService, ConnectionWorkflowService>();
        services.AddSingleton<ITunnelWorkflowService, TunnelWorkflowService>();

        return services;
    }
}
