using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soenneker.Postman.Converter.Internal;

internal sealed partial class CollectionConverter
{
    private (string Path, JsonObject? Server) ReadUrl(JsonNode? node, Dictionary<string, Variable> variables, JsonObject operation)
    {
        JsonObject? obj = node as JsonObject;
        string? raw = obj == null ? Text(node) : Text(obj["raw"]);
        string? host = obj == null ? null : JoinUrlPart(obj["host"], ".");
        string? protocol = obj == null ? null : Text(obj["protocol"]);
        string? structuredPath = obj == null ? null : JoinUrlPart(obj["path"], "/");
        if (string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(host) && structuredPath == null)
            throw new InvalidOperationException($"Postman request '{Text(operation["summary"])}' is missing a URL.");

        string url = raw ?? "";
        if (!string.IsNullOrEmpty(host) && (structuredPath != null || string.IsNullOrWhiteSpace(raw)))
            url = (string.IsNullOrEmpty(protocol) ? "" : protocol + "://") + host +
                  (Text(obj!["port"]) is { Length: > 0 } port ? ":" + port : "") +
                  (structuredPath == null ? "" : "/" + structuredPath.TrimStart('/'));
        int suffix = url.IndexOfAny(['?', '#']);
        if (suffix >= 0)
            url = url[..suffix];

        string server = "";
        string path;
        Match prefixVariable = Regex.Match(url, @"^\{\{\s*([^{}]+?)\s*\}\}(?=/|$)");
        if (prefixVariable.Success)
        {
            server = ResolveVariables(prefixVariable.Value, variables);
            path = url[prefixVariable.Length..];
        }
        else
        {
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0 || url.StartsWith("//", StringComparison.Ordinal))
            {
                int authorityStart = scheme >= 0 ? scheme + 3 : 2;
                int slash = url.IndexOf('/', authorityStart);
                server = slash < 0 ? url : url[..slash];
                path = slash < 0 ? "/" : url[slash..];
                server = ResolveVariables(server, variables);
            }
            else if (!string.IsNullOrEmpty(host))
            {
                server = ResolveVariables(host, variables);
                path = structuredPath ?? "/";
                Warn(operation, "The URL host has no protocol; supply a complete base URL to establish the server scheme.");
            }
            else
                path = url;
        }
        if (structuredPath != null)
            path = structuredPath;
        path = "/" + path.TrimStart('/');
        path = Variables.Replace(path, match => "{" + match.Groups[1].Value.Trim() + "}");
        path = Regex.Replace(path, @"(^|/):([^/]+)", "$1{$2}");
        JsonObject? serverObject = null;
        if (server.Length > 0)
        {
            if (Regex.IsMatch(server, @"^\{\{[^{}]+\}\}[^/:.{}]+"))
                Warn(operation, "A base URL variable is concatenated with literal text without a slash. The source URL is preserved; verify its environment value and separator.");
            // Keep unresolved origins as server variables instead of inventing a production host.
            server = Variables.Replace(server, match => "{" + match.Groups[1].Value.Trim() + "}");
            serverObject = new JsonObject { ["url"] = server.TrimEnd('/') };
            var serverVariables = new JsonObject();
            foreach (Match match in Templates.Matches(server))
            {
                string key = match.Groups[1].Value;
                if (serverVariables.ContainsKey(key))
                    continue;
                serverVariables[key] = new JsonObject { ["default"] = "", ["description"] = $"Unresolved Postman variable '{key}'. Supply its environment value before calling this API." };
                Warn(operation, $"Server variable '{key}' is unresolved. Supply it through PostmanConversionOptions.Variables.");
            }
            if (serverVariables.Count > 0)
                serverObject["variables"] = serverVariables;
        }
        else
            Warn(operation, "The request has a relative URL; the collection does not establish a server origin.");
        return (path, serverObject);
    }

    private string ResolveVariables(string input, Dictionary<string, Variable> variables)
    {
        return Resolve(input, new HashSet<string>(StringComparer.Ordinal));

        string Resolve(string value, HashSet<string> resolving) => Variables.Replace(value, match =>
        {
            string key = match.Groups[1].Value.Trim();
            string? replacement = _variableOverrides.TryGetValue(key, out string? supplied) ? supplied : variables.GetValueOrDefault(key)?.Value;
            if (string.IsNullOrEmpty(replacement) || !resolving.Add(key))
                return match.Value;
            string result = Resolve(replacement, resolving);
            resolving.Remove(key);
            return result;
        });
    }

    private static string? JoinUrlPart(JsonNode? node, string separator) => node is JsonArray array
        ? string.Join(separator, array.Select(value => value is JsonObject obj ? Text(obj["value"]) : Text(value))) : Text(node);

    private void AddParameters(JsonObject operation, JsonObject request, string path, Dictionary<string, Variable> variables,
        Dictionary<string, string> parameterNames)
    {
        var parameters = (JsonArray)operation["parameters"]!;
        JsonObject? url = request["url"] as JsonObject;
        Dictionary<string, Variable> pathVariables = ReadVariables(url?["variable"]);
        foreach (string key in Templates.Matches(path).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal))
        {
            string original = parameterNames.GetValueOrDefault(key) ?? key;
            Variable? variable = pathVariables.GetValueOrDefault(original) ?? variables.GetValueOrDefault(original);
            var parameter = StringParameter(key, "path", variable?.Description);
            parameter["required"] = true;
            string? example = _variableOverrides.GetValueOrDefault(original) ?? variable?.Value;
            if (!string.IsNullOrEmpty(example))
                AddExample(parameter, JsonValue.Create(example));
            parameters.Add(parameter);
        }

        JsonArray queries = url?["query"] as JsonArray ?? ParseRawQuery(url == null ? Text(request["url"]) : Text(url["raw"]));
        foreach (JsonObject query in queries.OfType<JsonObject>().Where(value => !Disabled(value)))
        {
            if (Text(query["key"]) is not { Length: > 0 } key)
                continue;
            string? value = Text(query["value"]);
            var parameter = StringParameter(key, "query", Description(query["description"]));
            if (Text(parameter["description"]) == null && value != null)
            {
                Match match = Variables.Match(value);
                if (match.Success && variables.GetValueOrDefault(match.Groups[1].Value.Trim())?.Description is { } description)
                    parameter["description"] = description;
            }
            AddExample(parameter, JsonValue.Create(value ?? ""));
            AddWireParameter(parameters, parameter, repeated: true);
        }
        foreach (JsonObject header in Headers(request["header"]))
        {
            string? key = Text(header["key"]);
            if (string.IsNullOrWhiteSpace(key) || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Accept", StringComparison.OrdinalIgnoreCase) || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;
            string value = Text(header["value"]) ?? "";
            if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string cookie in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = cookie.Split('=', 2);
                    var parameter = StringParameter(parts[0], "cookie", Description(header["description"]));
                    AddExample(parameter, JsonValue.Create(parts.Length == 2 ? parts[1] : ""));
                    AddWireParameter(parameters, parameter, repeated: false);
                }
                continue;
            }
            var headerParameter = StringParameter(key, "header", Description(header["description"]));
            AddExample(headerParameter, JsonValue.Create(value));
            AddWireParameter(parameters, headerParameter, repeated: true);
        }
    }

    private static JsonObject StringParameter(string name, string location, string? description)
    {
        var result = new JsonObject { ["name"] = name, ["in"] = location, ["required"] = false, ["schema"] = new JsonObject { ["type"] = "string" } };
        if (description != null)
            result["description"] = description;
        return result;
    }

    private static void AddWireParameter(JsonArray parameters, JsonObject parameter, bool repeated)
    {
        JsonObject? existing = parameters.OfType<JsonObject>().FirstOrDefault(item => ParameterKey(item) == ParameterKey(parameter));
        if (existing == null)
        {
            parameters.Add(parameter);
            return;
        }
        if (repeated)
        {
            if (Text(existing["schema"]?["type"]) != "array")
            {
                existing["schema"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
                existing["style"] = Text(existing["in"]) == "header" ? "simple" : "form";
                existing["explode"] = Text(existing["in"]) != "header";
                JsonNode? first = existing["examples"]?["example1"]?["value"]?.DeepClone();
                existing["examples"] = new JsonObject { ["example1"] = new JsonObject { ["value"] = new JsonArray(first) } };
            }
            ((JsonArray)existing["examples"]!["example1"]!["value"]!).Add(parameter["examples"]!["example1"]!["value"]?.DeepClone());
        }
        else
            MergeExamples(existing, parameter);
        MergeDescription(existing, parameter);
    }

    private static JsonArray ParseRawQuery(string? raw)
    {
        var result = new JsonArray();
        if (raw == null || raw.IndexOf('?') < 0)
            return result;
        string query = raw[(raw.IndexOf('?') + 1)..].Split('#', 2)[0];
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            result.Add(new JsonObject { ["key"] = DecodeQuery(pair[0]), ["value"] = pair.Length == 2 ? DecodeQuery(pair[1]) : "" });
        }
        return result;
    }
    private static string DecodeQuery(string value) => Uri.UnescapeDataString(value.Replace("+", " "));

    private static IEnumerable<JsonObject> Headers(JsonNode? node)
    {
        if (node is JsonArray array)
            return array.OfType<JsonObject>().Where(value => !Disabled(value));
        if (Text(node) is not { } raw)
            return [];
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .Select(parts => new JsonObject { ["key"] = parts[0].Trim(), ["value"] = parts[1].Trim() });
    }
    private static string? HeaderValue(JsonNode? headers, string name) => Headers(headers)
        .Where(header => string.Equals(Text(header["key"]), name, StringComparison.OrdinalIgnoreCase))
        .Select(header => Text(header["value"])).FirstOrDefault();
}
