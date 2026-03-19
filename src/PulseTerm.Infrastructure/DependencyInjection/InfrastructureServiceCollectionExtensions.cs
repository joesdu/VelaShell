using Microsoft.Extensions.DependencyInjection;
using PulseTerm.Infrastructure.Persistence;

namespace PulseTerm.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPulseTermInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PulseTermStoragePaths>();

        return services;
    }
}
