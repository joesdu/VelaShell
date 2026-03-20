using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using PulseTerm.Presentation.Services;
using PulseTerm.Presentation.ViewModels;

namespace PulseTerm.Presentation.DependencyInjection;

public static class PresentationServiceCollectionExtensions
{
    public static IServiceCollection AddPulseTermPresentation(this IServiceCollection services)
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
