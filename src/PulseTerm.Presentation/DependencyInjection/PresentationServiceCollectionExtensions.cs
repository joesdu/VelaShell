using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace PulseTerm.Presentation.DependencyInjection;

public static class PresentationServiceCollectionExtensions
{
    public static IServiceCollection AddPulseTermPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<WorkspaceHostViewModel>();

        return services;
    }
}
