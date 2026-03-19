using Microsoft.Extensions.DependencyInjection;

namespace PulseTerm.Controls.DependencyInjection;

public static class ControlsServiceCollectionExtensions
{
    public static IServiceCollection AddPulseTermControls(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
