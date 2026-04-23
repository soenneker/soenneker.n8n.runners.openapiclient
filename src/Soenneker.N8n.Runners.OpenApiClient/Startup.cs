using Microsoft.Extensions.DependencyInjection;
using Soenneker.Kiota.Util.Registrars;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.N8n.Runners.OpenApiClient.Utils;
using Soenneker.N8n.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Playwrights.Installation.Registrars;

namespace Soenneker.N8n.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddRunnersManagerAsSingleton()
                .AddPlaywrightInstallationUtilAsSingleton()
                .AddKiotaUtilAsSingleton();

        return services;
    }
}
