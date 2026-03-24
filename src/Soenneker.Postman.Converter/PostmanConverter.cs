using Microsoft.OpenApi;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.Utils.HttpClientCache.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Postman.Converter;

/// <inheritdoc cref="IPostmanConverter"/>
public sealed class PostmanConverter : IPostmanConverter
{
    private static readonly Regex _postmanVariableRegex = new("{{\\s*([^}]+?)\\s*}}", RegexOptions.Compiled);

    private static readonly HashSet<string> _ignoredHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Accept",
        "Content-Type"
    };

    private readonly IHttpClientCache _httpClientCache;
    private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);

    public PostmanConverter(IHttpClientCache httpClientCache)
    {
        _httpClientCache = httpClientCache;
    }

    public async ValueTask<OpenApiDocument> Convert(string postmanJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postmanJson);
        cancellationToken.ThrowIfCancellationRequested();

        JsonNode? rootNode = JsonNode.Parse(postmanJson);

        if (rootNode is not JsonObject root)
            throw new InvalidOperationException("Postman collection JSON root must be an object.");

        root = NormalizeCollectionRoot(root);

        JsonObject info = root["info"] as JsonObject ?? throw new InvalidOperationException("Postman collection is missing the 'info' object.");
        JsonArray items = root["item"] as JsonArray ?? throw new InvalidOperationException("Postman collection is missing the 'item' array.");

        Dictionary<string, PostmanVariable> variables = ReadVariables(root["variable"] as JsonArray);

        var document = new OpenApiDocument
        {
            Info = new OpenApiInfo
            {
                Title = GetString(info, "name") ?? "Converted Postman Collection",
                Description = GetString(info, "description"),
                Version = "1.0.0"
            },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal),
                Parameters = new Dictionary<string, IOpenApiParameter>(StringComparer.Ordinal),
                Responses = new Dictionary<string, IOpenApiResponse>(StringComparer.Ordinal),
                RequestBodies = new Dictionary<string, IOpenApiRequestBody>(StringComparer.Ordinal),
                Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal),
                SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal),
                Links = new Dictionary<string, IOpenApiLink>(StringComparer.Ordinal),
                Callbacks = new Dictionary<string, IOpenApiCallback>(StringComparer.Ordinal),
                Examples = new Dictionary<string, IOpenApiExample>(StringComparer.Ordinal)
            },
            Servers = new List<OpenApiServer>()
        };

        AddServers(document, root, variables);
        AddCollectionSecurityScheme(document, root);

        _operationIds.Clear();

        foreach (JsonNode? child in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (child is JsonObject childObject)
                ProcessItem(childObject, document, variables, [], root["auth"], cancellationToken);
        }

        return document;
    }

    public async ValueTask<string> ConvertToJson(string postmanJson, CancellationToken cancellationToken = default)
    {
        OpenApiDocument document = await Convert(postmanJson, cancellationToken)
            .ConfigureAwait(false);
        return ToJson(document);
    }

    public async ValueTask<OpenApiDocument> ConvertUrl(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        HttpClient httpClient = await _httpClientCache.Get(nameof(PostmanConverter), cancellationToken)
                                                      .ConfigureAwait(false);
        string postmanJson = await httpClient.GetStringAsync(url, cancellationToken)
                                             .ConfigureAwait(false);
        return await Convert(postmanJson, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ConvertUrlToJson(string url, CancellationToken cancellationToken = default)
    {
        OpenApiDocument document = await ConvertUrl(url, cancellationToken)
            .ConfigureAwait(false);
        return ToJson(document);
    }

    public async ValueTask<OpenApiDocument> ConvertFile(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string postmanJson = await File.ReadAllTextAsync(filePath, cancellationToken)
                                       .ConfigureAwait(false);
        return await Convert(postmanJson, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ConvertFileToJson(string filePath, CancellationToken cancellationToken = default)
    {
        OpenApiDocument document = await ConvertFile(filePath, cancellationToken)
            .ConfigureAwait(false);
        return ToJson(document);
    }

    public async ValueTask SaveOpenApiFile(string postmanFilePath, string openApiFilePath, CancellationToken cancellationToken = default)
    {
        string json = await ConvertFileToJson(postmanFilePath, cancellationToken)
            .ConfigureAwait(false);
        await Save(openApiFilePath, json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SaveOpenApiUrl(string url, string openApiFilePath, CancellationToken cancellationToken = default)
    {
        string json = await ConvertUrlToJson(url, cancellationToken)
            .ConfigureAwait(false);
        await Save(openApiFilePath, json, cancellationToken)
            .ConfigureAwait(false);
    }

    public string ToJson(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stringWriter = new StringWriter(new StringBuilder(4096));
        var writer = new OpenApiJsonWriter(stringWriter);
        document.SerializeAsV3(writer);

        return stringWriter.ToString();
    }

    private static async ValueTask Save(string openApiFilePath, string json, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openApiFilePath);

        string? directory = Path.GetDirectoryName(openApiFilePath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(openApiFilePath, json, cancellationToken)
                  .ConfigureAwait(false);
    }

    private static JsonObject NormalizeCollectionRoot(JsonObject root)
    {
        if (root["collection"] is JsonObject wrappedCollection)
            return wrappedCollection;

        return root;
    }

    private void ProcessItem(JsonObject item, OpenApiDocument document, IReadOnlyDictionary<string, PostmanVariable> variables, List<string> parentFolders,
        JsonNode? inheritedAuth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        JsonNode? effectiveAuth = item["auth"] ?? inheritedAuth;
        string? name = GetString(item, "name");

        if (item["item"] is JsonArray children)
        {
            List<string> nextFolders = [.. parentFolders];

            if (!string.IsNullOrWhiteSpace(name))
                nextFolders.Add(name);

            foreach (JsonNode? child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (child is JsonObject childObject)
                    ProcessItem(childObject, document, variables, nextFolders, effectiveAuth, cancellationToken);
            }

            return;
        }

        JsonObject? request = item["request"] as JsonObject;

        if (request == null)
            return;

        AddOperation(item, request, document, variables, parentFolders, effectiveAuth);
    }

    private void AddOperation(JsonObject item, JsonObject request, OpenApiDocument document, IReadOnlyDictionary<string, PostmanVariable> variables,
        IReadOnlyList<string> parentFolders, JsonNode? inheritedAuth)
    {
        string methodText = GetString(request, "method")
                            ?.Trim()
                            .ToUpperInvariant() ?? "GET";
        HttpMethod operationType = ToOperationType(methodText);
        string path = BuildPath(request["url"], variables);

        if (string.IsNullOrWhiteSpace(path))
            return;

        document.Paths.TryGetValue(path, out IOpenApiPathItem? existingPathItem);
        OpenApiPathItem pathItem = existingPathItem as OpenApiPathItem ?? new OpenApiPathItem();

        var operation = new OpenApiOperation
        {
            Summary = GetString(item, "name"),
            Description = GetString(request, "description") ?? GetString(item, "description"),
            OperationId = BuildOperationId(methodText, path, GetString(item, "name")),
            Parameters = new List<IOpenApiParameter>(),
            Responses = new OpenApiResponses()
        };

        AddPathParameters(operation, path, variables);
        AddQueryParameters(operation, request["url"] as JsonObject, variables);

        string? requestContentType = GetHeaderValue(request["header"] as JsonArray, "Content-Type");
        string? accept = GetHeaderValue(request["header"] as JsonArray, "Accept");

        AddHeaderParameters(operation, request["header"] as JsonArray, requestContentType, accept);
        AddRequestBody(operation, request["body"] as JsonObject, requestContentType);
        AddResponses(operation, item["response"] as JsonArray, accept);
        ApplySecurity(operation, request["auth"] ?? inheritedAuth, request["header"] as JsonArray, document);

        pathItem.AddOperation(operationType, operation);
        document.Paths[path] = pathItem;
    }

    private static void AddServers(OpenApiDocument document, JsonObject root, IReadOnlyDictionary<string, PostmanVariable> variables)
    {
        if (variables.TryGetValue("fastly_url", out PostmanVariable? fastlyUrl) && !string.IsNullOrWhiteSpace(fastlyUrl.Value))
        {
            document.Servers.Add(new OpenApiServer { Url = fastlyUrl.Value! });
            return;
        }

        if (root["item"] is not JsonArray items)
            return;

        foreach (JsonNode? child in items)
        {
            if (TryGetFirstServerUrl(child as JsonObject, out string? url))
            {
                document.Servers.Add(new OpenApiServer { Url = url! });
                return;
            }
        }
    }

    private static bool TryGetFirstServerUrl(JsonObject? item, out string? serverUrl)
    {
        serverUrl = null;

        if (item == null)
            return false;

        if (item["request"] is JsonObject request && request["url"] is JsonObject urlObject)
        {
            string? protocol = GetString(urlObject, "protocol");
            string? host = JoinArray(urlObject["host"] as JsonArray, ".");

            if (!string.IsNullOrWhiteSpace(protocol) && !string.IsNullOrWhiteSpace(host))
            {
                serverUrl = $"{protocol}://{host}";
                return true;
            }
        }

        if (item["item"] is not JsonArray children)
            return false;

        foreach (JsonNode? child in children)
        {
            if (TryGetFirstServerUrl(child as JsonObject, out serverUrl))
                return true;
        }

        return false;
    }

    private static void AddCollectionSecurityScheme(OpenApiDocument document, JsonObject root)
    {
        JsonObject? auth = root["auth"] as JsonObject;

        if (!TryReadApiKeySecurity(auth, out string? headerName, out string? description))
            return;

        document.Components.SecuritySchemes["apiKeyAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = headerName!,
            In = ParameterLocation.Header,
            Description = description
        };
    }

    private static void ApplySecurity(OpenApiOperation operation, JsonNode? authNode, JsonArray? headers, OpenApiDocument document)
    {
        if (TryReadApiKeySecurity(authNode as JsonObject, out _, out _))
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("apiKeyAuth")] = []
            });

            return;
        }

        string? authorizationHeader = GetHeaderValue(headers, "Authorization");

        if (!string.IsNullOrWhiteSpace(authorizationHeader))
        {
            document.Components.SecuritySchemes["authorizationHeader"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = "Authorization",
                In = ParameterLocation.Header,
                Description = "Authorization header inferred from the Postman collection."
            };

            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("authorizationHeader")] = []
            });
        }
    }

    private static bool TryReadApiKeySecurity(JsonObject? auth, out string? headerName, out string? description)
    {
        headerName = null;
        description = null;

        if (auth == null || !string.Equals(GetString(auth, "type"), "apikey", StringComparison.OrdinalIgnoreCase))
            return false;

        if (auth["apikey"] is not JsonArray apiKeyArray)
            return false;

        foreach (JsonNode? child in apiKeyArray)
        {
            if (child is not JsonObject apiKeyPart)
                continue;

            string? key = GetString(apiKeyPart, "key");

            if (string.Equals(key, "key", StringComparison.OrdinalIgnoreCase))
            {
                headerName = GetString(apiKeyPart, "value");
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(headerName))
            return false;

        description = $"API key authentication using the '{headerName}' header.";
        return true;
    }

    private static void AddPathParameters(OpenApiOperation operation, string path, IReadOnlyDictionary<string, PostmanVariable> variables)
    {
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!segment.StartsWith('{') || !segment.EndsWith('}'))
                continue;

            string variableName = segment[1..^1];

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = variableName,
                In = ParameterLocation.Path,
                Required = true,
                Description = variables.TryGetValue(variableName, out PostmanVariable? variable) ? variable.Description : null,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
        }
    }

    private static void AddQueryParameters(OpenApiOperation operation, JsonObject? urlObject, IReadOnlyDictionary<string, PostmanVariable> variables)
    {
        if (urlObject?["query"] is not JsonArray queries)
            return;

        foreach (JsonNode? child in queries)
        {
            if (child is not JsonObject query)
                continue;

            string? key = GetString(query, "key");

            if (string.IsNullOrWhiteSpace(key))
                continue;

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = key,
                In = ParameterLocation.Query,
                Required = false,
                Description = GetString(query, "description") ?? GetVariableDescription(GetString(query, "value"), variables),
                Schema = InferScalarSchema(GetString(query, "value"))
            });
        }
    }

    private static void AddHeaderParameters(OpenApiOperation operation, JsonArray? headers, string? requestContentType, string? accept)
    {
        if (headers == null)
            return;

        foreach (JsonNode? child in headers)
        {
            if (child is not JsonObject header)
                continue;

            string? name = GetString(header, "key");

            if (string.IsNullOrWhiteSpace(name) || _ignoredHeaderNames.Contains(name))
                continue;

            operation.Parameters.Add(new OpenApiParameter
            {
                Name = name,
                In = ParameterLocation.Header,
                Required = false,
                Description = GetString(header, "description"),
                Schema = InferScalarSchema(GetString(header, "value"))
            });
        }
    }

    private static void AddRequestBody(OpenApiOperation operation, JsonObject? body, string? requestContentType)
    {
        if (body == null)
            return;

        string? mode = GetString(body, "mode");

        if (string.IsNullOrWhiteSpace(mode))
            return;

        string contentType = string.IsNullOrWhiteSpace(requestContentType) ? GetDefaultContentType(mode) : requestContentType!;

        switch (mode.ToLowerInvariant())
        {
            case "raw":
            {
                string? raw = GetString(body, "raw");

                if (string.IsNullOrWhiteSpace(raw))
                    return;

                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
                    {
                        [contentType] = new OpenApiMediaType
                        {
                            Schema = InferBodySchema(raw, contentType)
                        }
                    }
                };

                return;
            }
            case "urlencoded":
            {
                var schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                };

                if (body["urlencoded"] is JsonArray urlencoded)
                {
                    foreach (JsonNode? child in urlencoded)
                    {
                        if (child is not JsonObject entry)
                            continue;

                        string? key = GetString(entry, "key");

                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        schema.Properties[NormalizeSchemaPropertyName(key)] = InferScalarSchema(GetString(entry, "value"));
                    }
                }

                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
                    {
                        [contentType] = new OpenApiMediaType
                        {
                            Schema = schema
                        }
                    }
                };

                return;
            }
        }
    }

    private static void AddResponses(OpenApiOperation operation, JsonArray? responses, string? acceptHeader)
    {
        if (responses == null || responses.Count == 0)
        {
            operation.Responses["200"] = new OpenApiResponse { Description = "Success" };
            return;
        }

        foreach (JsonNode? child in responses)
        {
            if (child is not JsonObject response)
                continue;

            string code = response["code"]
                ?.ToString() ?? "default";
            string description = GetString(response, "status") ?? GetString(response, "name") ?? "Response";

            var openApiResponse = new OpenApiResponse
            {
                Description = description
            };

            string? body = response["body"]
                ?.ToString();

            if (!string.IsNullOrWhiteSpace(body))
            {
                string contentType = GetHeaderValue(response["header"] as JsonArray, "Content-Type") ??
                                     NormalizeContentType(acceptHeader) ?? InferContentType(body);

                openApiResponse.Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
                {
                    [contentType] = new OpenApiMediaType
                    {
                        Schema = InferBodySchema(body, contentType)
                    }
                };
            }

            operation.Responses[code] = openApiResponse;
        }
    }

    private string BuildOperationId(string method, string path, string? name)
    {
        string seed = !string.IsNullOrWhiteSpace(name) ? name! : $"{method} {path}";

        string candidate = SanitizeIdentifier(seed);

        if (string.IsNullOrWhiteSpace(candidate))
            candidate = $"{method.ToLowerInvariant()}Operation";

        string unique = candidate;
        var suffix = 2;

        while (!_operationIds.Add(unique))
        {
            unique = $"{candidate}{suffix}";
            suffix++;
        }

        return unique;
    }

    private static string BuildPath(JsonNode? urlNode, IReadOnlyDictionary<string, PostmanVariable> variables)
    {
        if (urlNode is JsonObject urlObject)
        {
            if (urlObject["path"] is JsonArray pathArray && pathArray.Count > 0)
                return "/" + string.Join("/", pathArray.Select(static n => NormalizePathSegment(n?.ToString())));

            string? raw = GetString(urlObject, "raw");

            if (!string.IsNullOrWhiteSpace(raw))
                return BuildPathFromRaw(raw);
        }

        if (urlNode is JsonValue value)
        {
            string? raw = value.ToString();

            if (!string.IsNullOrWhiteSpace(raw))
                return BuildPathFromRaw(raw);
        }

        return "/";
    }

    private static string BuildPathFromRaw(string raw)
    {
        string withoutQuery = raw.Split('?', 2)[0];
        string normalized = ReplacePostmanVariables(withoutQuery);

        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? absoluteUri))
            return string.IsNullOrWhiteSpace(absoluteUri.AbsolutePath) ? "/" : absoluteUri.AbsolutePath;

        int firstSlash = normalized.IndexOf('/');

        if (firstSlash < 0)
            return "/";

        return normalized[firstSlash..];
    }

    private static string NormalizePathSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return string.Empty;

        string trimmed = segment.Trim();

        if (trimmed.StartsWith(':'))
            return "{" + trimmed[1..] + "}";

        return ReplacePostmanVariables(trimmed);
    }

    private static string ReplacePostmanVariables(string value)
    {
        return _postmanVariableRegex.Replace(value, static match => "{" + match.Groups[1]
                                                                               .Value.Trim() + "}");
    }

    private static string? GetHeaderValue(JsonArray? headers, string headerName)
    {
        if (headers == null)
            return null;

        foreach (JsonNode? child in headers)
        {
            if (child is not JsonObject header)
                continue;

            string? name = GetString(header, "key");

            if (string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase))
                return GetString(header, "value");
        }

        return null;
    }

    private static string? GetVariableDescription(string? value, IReadOnlyDictionary<string, PostmanVariable> variables)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        Match match = _postmanVariableRegex.Match(value);

        if (!match.Success)
            return null;

        return variables.TryGetValue(match.Groups[1]
                                          .Value.Trim(), out PostmanVariable? variable)
            ? variable.Description
            : null;
    }

    private static Dictionary<string, PostmanVariable> ReadVariables(JsonArray? array)
    {
        var result = new Dictionary<string, PostmanVariable>(StringComparer.OrdinalIgnoreCase);

        if (array == null)
            return result;

        foreach (JsonNode? child in array)
        {
            if (child is not JsonObject variable)
                continue;

            string? key = GetString(variable, "key");

            if (string.IsNullOrWhiteSpace(key))
                continue;

            result[key] = new PostmanVariable(GetString(variable, "value"), GetString(variable, "description"));
        }

        return result;
    }

    private static string? JoinArray(JsonArray? array, string separator)
    {
        if (array == null || array.Count == 0)
            return null;

        return string.Join(separator, array.Select(static node => node?.ToString())
                                           .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static HttpMethod ToOperationType(string method)
    {
        return method switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            "TRACE" => HttpMethod.Trace,
            _ => HttpMethod.Get
        };
    }

    private static IOpenApiSchema InferBodySchema(string body, string? contentType)
    {
        string effectiveContentType = NormalizeContentType(contentType) ?? InferContentType(body);

        if (effectiveContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                JsonNode? jsonNode = JsonNode.Parse(body);

                if (jsonNode != null)
                    return InferSchema(jsonNode);
            }
            catch (JsonException)
            {
            }
        }

        return new OpenApiSchema { Type = JsonSchemaType.String };
    }

    private static string InferContentType(string body)
    {
        try
        {
            JsonNode.Parse(body);
            return "application/json";
        }
        catch (JsonException)
        {
            return "text/plain";
        }
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        string first = contentType.Split(',', 2)[0]
                                  .Trim();
        return first.Split(';', 2)[0]
                    .Trim();
    }

    private static string GetDefaultContentType(string mode)
    {
        return mode switch
        {
            "urlencoded" => "application/x-www-form-urlencoded",
            _ => "application/json"
        };
    }

    private static OpenApiSchema InferScalarSchema(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new OpenApiSchema { Type = JsonSchemaType.String };

        if (_postmanVariableRegex.IsMatch(value))
            return new OpenApiSchema { Type = JsonSchemaType.String };

        if (bool.TryParse(value, out _))
            return new OpenApiSchema { Type = JsonSchemaType.Boolean };

        if (long.TryParse(value, out _))
            return new OpenApiSchema { Type = JsonSchemaType.Integer };

        if (double.TryParse(value, out _))
            return new OpenApiSchema { Type = JsonSchemaType.Number };

        return new OpenApiSchema { Type = JsonSchemaType.String };
    }

    private static IOpenApiSchema InferSchema(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => InferObjectSchema(obj),
            JsonArray array => InferArraySchema(array),
            JsonValue value => InferValueSchema(value),
            _ => new OpenApiSchema { Type = JsonSchemaType.String }
        };
    }

    private static IOpenApiSchema InferObjectSchema(JsonObject obj)
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        };

        foreach ((string key, JsonNode? value) in obj)
        {
            string normalizedKey = NormalizeSchemaPropertyName(key);
            schema.Properties[normalizedKey] = value == null ? new OpenApiSchema { Type = JsonSchemaType.String } : InferSchema(value);

            if (value != null)
            {
                schema.Required ??= new HashSet<string>();
                schema.Required.Add(normalizedKey);
            }
        }

        return schema;
    }

    private static IOpenApiSchema InferArraySchema(JsonArray array)
    {
        IOpenApiSchema itemSchema = array.Count > 0 && array[0] != null ? InferSchema(array[0]!) : new OpenApiSchema { Type = JsonSchemaType.String };

        return new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Items = itemSchema
        };
    }

    private static IOpenApiSchema InferValueSchema(JsonValue value)
    {
        if (value.TryGetValue(out bool boolValue))
            return new OpenApiSchema { Type = JsonSchemaType.Boolean };

        if (value.TryGetValue(out int intValue))
            return new OpenApiSchema { Type = JsonSchemaType.Integer };

        if (value.TryGetValue(out long longValue))
            return new OpenApiSchema { Type = JsonSchemaType.Integer };

        if (value.TryGetValue(out decimal decimalValue))
            return new OpenApiSchema { Type = JsonSchemaType.Number };

        if (value.TryGetValue(out double doubleValue))
            return new OpenApiSchema { Type = JsonSchemaType.Number };

        if (value.TryGetValue(out string? stringValue))
            return InferScalarSchema(stringValue);

        return new OpenApiSchema { Type = JsonSchemaType.String };
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var capitalizeNext = false;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        string result = builder.ToString();

        if (string.IsNullOrWhiteSpace(result))
            return "operation";

        return char.IsDigit(result[0]) ? $"operation{result}" : result;
    }

    private static string? GetString(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out JsonNode? node) || node == null)
            return null;

        return node.GetValueKind() == JsonValueKind.Null ? null : node.ToString();
    }

    private static string NormalizeSchemaPropertyName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key;

        string normalized = key.Trim().TrimEnd('\\', '"');
        return string.IsNullOrWhiteSpace(normalized) ? key : normalized;
    }

    private sealed record PostmanVariable(string? Value, string? Description);
}