using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Soenneker.Postman.Converter.Options;

namespace Soenneker.Postman.Converter.Internal;

// Build the wire representation first so source extensions and JSON examples keep their exact types.
// Each conversion owns its state; the public facade reads the result into the OpenAPI object model.
internal sealed partial class CollectionConverter(PostmanConversionOptions options, CancellationToken cancellationToken)
{
    private static readonly Regex Variables = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex Templates = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private readonly JsonObject _paths = new();
    private readonly JsonObject _schemes = new();
    private readonly JsonArray _tags = [];
    private readonly JsonArray _warnings = [];
    private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonicalPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tagNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _variableOverrides = new(options.Variables, StringComparer.Ordinal);

    internal JsonObject Convert(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new InvalidOperationException("Postman collection JSON root must be an object.");
        root = root["collection"] as JsonObject ?? root;
        JsonObject info = root["info"] as JsonObject ?? throw new InvalidOperationException("Postman collection is missing the 'info' object.");
        JsonArray items = root["item"] as JsonArray ?? throw new InvalidOperationException("Postman collection is missing the 'item' array.");
        var document = new JsonObject
        {
            ["openapi"] = "3.0.4",
            ["info"] = new JsonObject { ["title"] = Text(info["name"]) ?? "Converted Postman Collection", ["version"] = ReadVersion(info["version"]) },
            ["paths"] = _paths,
            ["components"] = new JsonObject { ["securitySchemes"] = _schemes },
            ["tags"] = _tags,
            ["x-postman-warnings"] = _warnings
        };
        SetDescription((JsonObject)document["info"]!, info["description"]);
        if (root["event"] != null)
            document["x-postman-events"] = root["event"]!.DeepClone();
        if (root["variable"] != null)
            document["x-postman-variables"] = root["variable"]!.DeepClone();
        WarnScripts(root, "collection");
        Visit(items, ReadVariables(root["variable"]), root["auth"] as JsonObject, [], null);
        return document;
    }

    private void Visit(JsonArray items, Dictionary<string, Variable> variables, JsonObject? inheritedAuth, List<string> folders, string? folderDescription)
    {
        foreach (JsonNode? node in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not JsonObject item)
                throw new InvalidOperationException("Postman items must be objects.");
            string name = Text(item["name"]) ?? "Request";
            var scopedVariables = new Dictionary<string, Variable>(variables, StringComparer.Ordinal);
            foreach (var pair in ReadVariables(item["variable"]))
                scopedVariables[pair.Key] = pair.Value;
            JsonObject? auth = EffectiveAuth(item["auth"], inheritedAuth);
            WarnScripts(item, string.Join(" / ", folders.Append(name)));
            if (item["item"] is JsonArray children)
            {
                Visit(children, scopedVariables, auth, [.. folders, name], Description(item["description"]));
                continue;
            }
            JsonObject request = item["request"] switch
            {
                JsonObject obj => obj,
                JsonValue value => new JsonObject { ["method"] = "GET", ["url"] = Text(value) },
                _ => throw new InvalidOperationException($"Postman item '{name}' is missing a request or item array.")
            };
            AddOperation(item, request, scopedVariables, EffectiveAuth(request["auth"], auth), folders, folderDescription);
        }
    }

    private void AddOperation(JsonObject item, JsonObject request, Dictionary<string, Variable> variables, JsonObject? auth, List<string> folders,
        string? folderDescription)
    {
        string method = (Text(request["method"]) ?? "GET").Trim().ToLowerInvariant();
        if (method is not ("get" or "post" or "put" or "patch" or "delete" or "head" or "options" or "trace"))
            throw new InvalidOperationException($"Postman request uses unsupported HTTP method '{method.ToUpperInvariant()}'.");
        string name = Text(item["name"]) ?? method;
        var operation = new JsonObject
        {
            ["summary"] = name,
            ["operationId"] = UniqueIdentifier(name),
            ["parameters"] = new JsonArray(),
            ["responses"] = new JsonObject()
        };
        SetDescription(operation, request["description"] ?? item["description"]);
        var url = ReadUrl(request["url"], variables, operation);
        string path = url.Path;
        // OpenAPI forbids paths that differ only in template names. Keep the first spelling and remap parameters.
        string shape = Templates.Replace(path, "{}");
        _canonicalPaths.TryGetValue(shape, out string? canonicalPath);
        Dictionary<string, string> parameterNames = new(StringComparer.Ordinal);
        if (canonicalPath != null && canonicalPath != path)
        {
            string[] canonicalNames = Templates.Matches(canonicalPath).Select(match => match.Groups[1].Value).ToArray();
            string[] originalNames = Templates.Matches(path).Select(match => match.Groups[1].Value).ToArray();
            for (int i = 0; i < originalNames.Length; i++)
                parameterNames[canonicalNames[i]] = originalNames[i];
            path = canonicalPath;
            Warn(operation, "Equivalent path templates were combined; original variable names remain in x-postman-variants.");
        }
        _canonicalPaths.TryAdd(shape, path);
        if (url.Server != null)
            operation["servers"] = new JsonArray(url.Server);
        AddParameters(operation, request, path, variables, parameterNames);
        AddBody(operation, request["body"] as JsonObject, HeaderValue(request["header"], "Content-Type"));
        AddResponses(operation, item["response"] as JsonArray, method);
        ApplySecurity(operation, auth, request["header"], variables);
        if (folders.Count > 0)
        {
            string tag = string.Join(" / ", folders);
            if (_tagNames.Add(tag))
            {
                var tagObject = new JsonObject { ["name"] = tag };
                if (folderDescription != null)
                    tagObject["description"] = folderDescription;
                _tags.Add(tagObject);
            }
            operation["tags"] = new JsonArray(JsonValue.Create(tag));
        }
        // Preserve the original requests and saved responses, including correlations a merged OpenAPI operation cannot express.
        var source = new JsonObject
        {
            ["name"] = name, ["id"] = item["id"]?.DeepClone(), ["request"] = request.DeepClone(),
            ["response"] = item["response"]?.DeepClone(), ["folders"] = new JsonArray(folders.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        };
        if (auth != null)
            source["effectiveAuth"] = auth.DeepClone();
        if (item["event"] != null)
            source["event"] = item["event"]!.DeepClone();
        if (item["protocolProfileBehavior"] != null)
            source["protocolProfileBehavior"] = item["protocolProfileBehavior"]!.DeepClone();
        operation["x-postman-variants"] = new JsonArray(source);
        JsonObject pathItem = _paths[path] as JsonObject ?? new JsonObject();
        if (pathItem[method] is JsonObject existing)
            MergeOperation(existing, operation);
        else
            pathItem[method] = operation;
        if (_paths[path] == null)
            _paths[path] = pathItem;
    }

    private void MergeOperation(JsonObject target, JsonObject source)
    {
        Warn(target, "Multiple Postman requests share this method and path. Inputs and responses are combined; x-postman-variants retains each source request and its correlations.");
        var parameters = (JsonArray)target["parameters"]!;
        foreach (JsonObject incoming in ((JsonArray)source["parameters"]!).OfType<JsonObject>())
        {
            JsonObject? existing = parameters.OfType<JsonObject>().FirstOrDefault(p => ParameterKey(p) == ParameterKey(incoming));
            if (existing == null)
                parameters.Add(incoming.DeepClone());
            else
            {
                existing["schema"] = MergeSchema((JsonObject)existing["schema"]!, (JsonObject)incoming["schema"]!);
                MergeExamples(existing, incoming);
                MergeDescription(existing, incoming);
            }
        }
        if (source["requestBody"] is JsonObject body)
        {
            if (target["requestBody"] is JsonObject existingBody)
                MergeContent((JsonObject)existingBody["content"]!, (JsonObject)body["content"]!);
            else
                target["requestBody"] = body.DeepClone();
        }
        var responses = (JsonObject)target["responses"]!;
        foreach (var pair in (JsonObject)source["responses"]!)
        {
            if (responses[pair.Key] is JsonObject existing)
                MergeResponse(existing, (JsonObject)pair.Value!);
            else
                responses[pair.Key] = pair.Value!.DeepClone();
        }
        foreach (string arrayName in new[] { "tags", "servers", "security", "x-postman-warnings", "x-postman-variants" })
        {
            if (source[arrayName] is not JsonArray additions)
                continue;
            var combined = target[arrayName] as JsonArray;
            if (combined == null)
                target[arrayName] = combined = [];
            foreach (JsonNode? addition in additions)
                if (arrayName == "x-postman-variants" || !combined.Any(node => JsonNode.DeepEquals(node, addition)))
                    combined.Add(addition?.DeepClone());
        }
        MergeDescription(target, source);
    }

    private void Warn(JsonObject? operation, string message)
    {
        string contextual = operation == null ? message : $"{Text(operation["summary"])}: {message}";
        if (options.FailOnWarnings)
            throw new InvalidOperationException(contextual);
        if (!_warnings.Any(node => Text(node) == contextual))
            _warnings.Add(contextual);
        if (operation != null)
        {
            if (operation["x-postman-warnings"] is not JsonArray warnings)
                operation["x-postman-warnings"] = warnings = [];
            if (!warnings.Any(node => Text(node) == message))
                warnings.Add(message);
        }
    }

    private void WarnScripts(JsonObject item, string context)
    {
        if (item["event"] is JsonArray { Count: > 0 })
            Warn(null, $"{context}: Postman scripts are not executed; runtime changes to requests and assertions cannot be inferred.");
        if (item["protocolProfileBehavior"] is JsonObject { Count: > 0 })
            Warn(null, $"{context}: Postman protocol profile behavior has no OpenAPI equivalent.");
    }

    private string UniqueIdentifier(string name)
    {
        string candidate = Regex.Replace(name, @"[^\p{L}\p{Nd}]+(.)?", match => match.Groups[1].Value.ToUpperInvariant());
        if (candidate.Length == 0 || char.IsDigit(candidate[0]))
            candidate = "operation" + candidate;
        string unique = candidate;
        for (int i = 2; !_operationIds.Add(unique); i++)
            unique = candidate + i;
        return unique;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value ? value.ToString() : null;
    private static bool Disabled(JsonNode? node) => node is JsonObject obj && Text(obj["disabled"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    private static string? Description(JsonNode? node) => node is JsonObject obj ? Text(obj["content"]) : Text(node);
    private static void SetDescription(JsonObject target, JsonNode? source)
    {
        if (Description(source) is { } description)
            target["description"] = description;
    }
    private static string ReadVersion(JsonNode? node) => Text(node) ?? (node is JsonObject version
        ? $"{Text(version["major"]) ?? "0"}.{Text(version["minor"]) ?? "0"}.{Text(version["patch"]) ?? "0"}{(Text(version["identifier"]) is { Length: > 0 } id ? "-" + id : "")}" : "1.0.0");
    private static void MergeDescription(JsonObject target, JsonObject source)
    {
        string? incoming = Text(source["description"]);
        string? existing = Text(target["description"]);
        if (!string.IsNullOrEmpty(incoming) && existing != incoming)
            target["description"] = string.IsNullOrEmpty(existing) ? incoming : existing + "\n\n" + incoming;
    }
    private static string ParameterKey(JsonObject parameter) => Text(parameter["in"]) + ":" +
        (Text(parameter["in"]) == "header" ? Text(parameter["name"])?.ToUpperInvariant() : Text(parameter["name"]));
    private static Dictionary<string, Variable> ReadVariables(JsonNode? node)
    {
        var result = new Dictionary<string, Variable>(StringComparer.Ordinal);
        if (node is JsonArray array)
            foreach (JsonObject variable in array.OfType<JsonObject>().Where(value => !Disabled(value)))
                if (Text(variable["key"] ?? variable["id"]) is { Length: > 0 } key)
                    result[key] = new Variable(Text(variable["value"]), Description(variable["description"]));
        return result;
    }
    private sealed record Variable(string? Value, string? Description);
}
