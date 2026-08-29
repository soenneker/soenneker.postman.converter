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
    /// <param name="postmanJson">Postman JSON for the convert operation.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the requested openAPI Document.</returns>
    ValueTask<OpenApiDocument> Convert(string postmanJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts a Postman collection JSON payload into an OpenAPI v3 JSON string.
    /// </summary>
    /// <param name="postmanJson">Postman JSON for the convert to json operation.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the text returned by convert To JSON.</returns>
    ValueTask<string> ConvertToJson(string postmanJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Postman collection from a URL and converts it into an OpenAPI document.
    /// </summary>
    /// <param name="url">URL of the resource to target.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the requested openAPI Document.</returns>
    ValueTask<OpenApiDocument> ConvertUrl(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Postman collection from a URL and converts it into an OpenAPI v3 JSON string.
    /// </summary>
    /// <param name="url">URL of the resource to target.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the text returned by convert URL To JSON.</returns>
    ValueTask<string> ConvertUrlToJson(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Postman collection file and converts it into an OpenAPI document.
    /// </summary>
    /// <param name="filePath">Path of the file to use.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the requested openAPI Document.</returns>
    ValueTask<OpenApiDocument> ConvertFile(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Postman collection file and converts it into an OpenAPI v3 JSON string.
    /// </summary>
    /// <param name="filePath">Path of the file to use.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the text returned by convert File To JSON.</returns>
    ValueTask<string> ConvertFileToJson(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a Postman collection file and saves the converted OpenAPI JSON to disk.
    /// </summary>
    /// <param name="postmanFilePath">Path of the postman file to use.</param>
    /// <param name="openApiFilePath">Path of the open api file to use.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the openapi file has been saved.</returns>
    ValueTask SaveOpenApiFile(string postmanFilePath, string openApiFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Postman collection from a URL and saves the converted OpenAPI JSON to disk.
    /// </summary>
    /// <param name="url">URL of the resource to target.</param>
    /// <param name="openApiFilePath">Path of the open api file to use.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the openapi url has been saved.</returns>
    ValueTask SaveOpenApiUrl(string url, string openApiFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes an OpenAPI document as v3 JSON.
    /// </summary>
    /// <param name="document">Document to read, persist, or update.</param>
    /// <returns>The text produced by to JSON.</returns>
    string ToJson(OpenApiDocument document);
}
