using Microsoft.Extensions.DependencyInjection;

namespace VelaShell.Controls.DependencyInjection;

/// <summary>VelaShell.Controls 的依赖注入注册扩展。</summary>
public static class ControlsServiceCollectionExtensions
{
    /// <summary>将 VelaShell.Controls 提供的服务注册到容器。</summary>
    public static IServiceCollection AddVelaShellControls(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
