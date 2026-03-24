using Microsoft.OpenApi;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Postman.Converter.Abstract;

/// <summary>
/// A utility library that converts Postman schemas to OpenApi
/// </summary>
public interface IPostmanConverter
{
    /// <summary>
    /// Converts a Postman collection JSON payload into an OpenAPI document.
    /// </summary>
    ValueTask<OpenApiDocument> Convert(string postmanJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts a Postman collection JSON payload into an OpenAPI v3 JSON string.
    /// </summary>
    ValueTask<string> ConvertToJson(string postmanJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Postman collection from a URL and converts it into an OpenAPI document.
    /// </summary>
    ValueTask<OpenApiDocument> ConvertUrl(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Postman collection from a URL and converts it into an OpenAPI v3 JSON string.
    /// </summary>
    ValueTask<string> ConvertUrlToJson(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Postman collection file and converts it into an OpenAPI document.
    /// </summary>
    ValueTask<OpenApiDocument> ConvertFile(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Postman collection file and converts it into an OpenAPI v3 JSON string.
    /// </summary>
    ValueTask<string> ConvertFileToJson(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Postman collection file and saves the converted OpenAPI JSON to disk.
    /// </summary>
    ValueTask SaveOpenApiFile(string postmanFilePath, string openApiFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Postman collection from a URL and saves the converted OpenAPI JSON to disk.
    /// </summary>
    ValueTask SaveOpenApiUrl(string url, string openApiFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes an OpenAPI document as v3 JSON.
    /// </summary>
    string ToJson(OpenApiDocument document);
}
