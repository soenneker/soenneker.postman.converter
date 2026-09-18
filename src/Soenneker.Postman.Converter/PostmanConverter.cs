using Microsoft.OpenApi;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.Postman.Converter.Internal;
using Soenneker.Postman.Converter.Options;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.File.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Postman.Converter;

public sealed class PostmanConverter : IPostmanConverter
{
    private readonly IHttpClientCache _httpClientCache;
    private readonly IFileUtil _fileUtil;

    public PostmanConverter(IHttpClientCache httpClientCache, IFileUtil fileUtil)
    {
        _httpClientCache = httpClientCache;
        _fileUtil = fileUtil;
    }

    public ValueTask<OpenApiDocument> Convert(string postmanJson, CancellationToken cancellationToken = default)
        => Convert(postmanJson, new PostmanConversionOptions(), cancellationToken);

    public ValueTask<OpenApiDocument> Convert(string postmanJson, PostmanConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postmanJson);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Variables);
        cancellationToken.ThrowIfCancellationRequested();
        var converter = new CollectionConverter(options, cancellationToken);
        string json = converter.Convert(postmanJson).ToJsonString();
        var result = OpenApiDocument.Parse(json, "json");
        if (result.Document == null || result.Diagnostic?.Errors.Count > 0)
            throw new InvalidOperationException("Generated OpenAPI could not be read: " +
                string.Join("; ", result.Diagnostic?.Errors.Select(error => error.Message) ?? []));
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result.Document);
    }

    public async ValueTask<OpenApiDocument> ConvertUrl(string url, PostmanConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(options);
        HttpClient client = await _httpClientCache.Get(nameof(PostmanConverter), cancellationToken).NoSync();
        string json = await client.GetStringAsync(url, cancellationToken).NoSync();
        return await Convert(json, options, cancellationToken).NoSync();
    }

    public async ValueTask<OpenApiDocument> ConvertFile(string filePath, PostmanConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);
        string json = await _fileUtil.Read(filePath, log: false, cancellationToken).NoSync();
        return await Convert(json, options, cancellationToken).NoSync();
    }
    public async ValueTask<string> ConvertToJson(string postmanJson, CancellationToken cancellationToken = default)
    {
        OpenApiDocument document = await Convert(postmanJson, cancellationToken)
            .NoSync();
        return ToJson(document);
    }

    public async ValueTask<OpenApiDocument> ConvertUrl(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        HttpClient httpClient = await _httpClientCache.Get(nameof(PostmanConverter), cancellationToken)
                                                      .NoSync();
        string postmanJson = await httpClient.GetStringAsync(url, cancellationToken)
                                             .NoSync();
        return await Convert(postmanJson, cancellationToken)
            .NoSync();
    }

    public async ValueTask<string> ConvertUrlToJson(string url, CancellationToken cancellationToken = default)
    {
        OpenApiDocument document = await ConvertUrl(url, cancellationToken)
            .NoSync();
        return ToJson(document);
    }

    public async ValueTask<OpenApiDocument> ConvertFile(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string postmanJson = await _fileUtil.Read(filePath, log: false, cancellationToken).NoSync();
        return await Convert(postmanJson, cancellationToken)
            .NoSync();
    }

    public async ValueTask<string> ConvertFileToJson(string filePath, CancellationToken cancellationToken = default)
    {
        OpenApiDocument document = await ConvertFile(filePath, cancellationToken)
            .NoSync();
        return ToJson(document);
    }

    public async ValueTask SaveOpenApiFile(string postmanFilePath, string openApiFilePath, CancellationToken cancellationToken = default)
    {
        string json = await ConvertFileToJson(postmanFilePath, cancellationToken)
            .NoSync();
        await Save(openApiFilePath, json, cancellationToken)
            .NoSync();
    }

    public async ValueTask SaveOpenApiUrl(string url, string openApiFilePath, CancellationToken cancellationToken = default)
    {
        string json = await ConvertUrlToJson(url, cancellationToken)
            .NoSync();
        await Save(openApiFilePath, json, cancellationToken)
            .NoSync();
    }

    public string ToJson(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stringWriter = new StringWriter(new StringBuilder(4096));
        var writer = new OpenApiJsonWriter(stringWriter);
        document.SerializeAsV3(writer);

        return stringWriter.ToString();
    }

    private async ValueTask Save(string openApiFilePath, string json, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openApiFilePath);
        await _fileUtil.WriteAtomically(openApiFilePath, json, log: false, cancellationToken).NoSync();
    }

}
