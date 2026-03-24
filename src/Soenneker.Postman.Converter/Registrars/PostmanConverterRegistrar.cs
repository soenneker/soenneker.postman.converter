using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.Utils.HttpClientCache.Registrar;

namespace Soenneker.Postman.Converter.Registrars;

/// <summary>
/// A utility library that converts Postman schemas to OpenApi
/// </summary>
public static class PostmanConverterRegistrar
{
    /// <summary>
    /// Adds <see cref="IPostmanConverter"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddPostmanConverterAsSingleton(this IServiceCollection services)
    {
        services.AddHttpClientCacheAsSingleton()
                .TryAddSingleton<IPostmanConverter, PostmanConverter>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IPostmanConverter"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddPostmanConverterAsScoped(this IServiceCollection services)
    {
        services.AddHttpClientCacheAsSingleton()
                .TryAddScoped<IPostmanConverter, PostmanConverter>();

        return services;
    }
}
