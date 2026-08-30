using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.Utils.HttpClientCache.Registrar;

namespace Soenneker.Postman.Converter.Registrars;

/// <summary>
/// Registers the Postman-to-OpenAPI converter and its HTTP dependency.
/// </summary>
public static class PostmanConverterRegistrar
{
    /// <summary>
    /// Adds <see cref="IPostmanConverter"/> as a singleton service.
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostmanConverterAsSingleton(this IServiceCollection services)
    {
        services.AddHttpClientCacheAsSingleton()
                .TryAddSingleton<IPostmanConverter, PostmanConverter>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IPostmanConverter"/> as a scoped service while retaining the singleton HTTP transport.
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostmanConverterAsScoped(this IServiceCollection services)
    {
        services.AddHttpClientCacheAsSingleton()
                .TryAddScoped<IPostmanConverter, PostmanConverter>();

        return services;
    }
}
