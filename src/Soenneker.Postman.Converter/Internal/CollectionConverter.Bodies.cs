using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soenneker.Postman.Converter.Internal;

internal sealed partial class CollectionConverter
{
    private void AddBody(JsonObject operation, JsonObject? body, string? headerContentType)
    {
        if (body == null || Disabled(body))
            return;
        string? mode = Text(body["mode"]);
        string? contentType = MediaType(headerContentType);
        JsonObject media;
        switch (mode)
        {
            case "raw":
                string raw = Text(body["raw"]) ?? "";
                contentType ??= Text(body["options"]?["raw"]?["language"]) switch
                {
                    "json" => "application/json", "xml" => "application/xml", "html" => "text/html",
                    "javascript" => "application/javascript", "text" => "text/plain", _ => InferMediaType(raw)
                };
                media = BodyMedia(raw, contentType, operation);
                break;
            case "urlencoded":
            case "formdata":
                contentType ??= mode == "formdata" ? "multipart/form-data" : "application/x-www-form-urlencoded";
                media = FormMedia(body[mode] as JsonArray, mode == "formdata");
                break;
            case "file":
                contentType ??= "application/octet-stream";
                media = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } };
                break;
            case "graphql":
                contentType ??= "application/json";
                string query = Text(body["graphql"]?["query"]) ?? "";
                var graphql = new JsonObject { ["query"] = query };
                if (body["graphql"]?["variables"] is JsonNode graphVariables)
                {
                    if (graphVariables is JsonObject)
                        graphql["variables"] = graphVariables.DeepClone();
                    else if (!string.IsNullOrWhiteSpace(Text(graphVariables)))
                    {
                        try { graphql["variables"] = JsonNode.Parse(Text(graphVariables)!); }
                        catch (JsonException) { Warn(operation, "GraphQL variables are not valid JSON; their source is retained in x-postman-variants."); }
                    }
                }
                media = BodyMedia(graphql.ToJsonString(), contentType, operation);
                break;
            case null:
                Warn(operation, "Request body has no mode and cannot be mapped; its source is retained in x-postman-variants.");
                return;
            default:
                Warn(operation, $"Unsupported request body mode '{mode}'; its source is retained in x-postman-variants.");
                return;
        }
        var requestBody = new JsonObject { ["content"] = new JsonObject { [contentType] = media } };
        SetDescription(requestBody, body["description"]);
        operation["requestBody"] = requestBody;
    }

    private JsonObject BodyMedia(string raw, string contentType, JsonObject operation, string? exampleName = null)
    {
        var media = new JsonObject();
        if (IsJsonMediaType(contentType))
        {
            try
            {
                JsonNode? value = JsonNode.Parse(raw);
                media["schema"] = InferSchema(value);
                AddExample(media, value, exampleName);
            }
            catch (JsonException)
            {
                // Postman raw JSON may contain comments, trailing commas and unquoted runtime variables.
                // Never turn a JSON request into a string schema just because its template is not JSON yet.
                string marker = "__postman_unresolved_value__";
                while (raw.Contains(marker, StringComparison.Ordinal)) marker += "_";
                string normalized = ReplaceUnquotedVariables(raw, marker);
                try
                {
                    JsonNode? value = JsonNode.Parse(normalized, documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
                    });
                    media["schema"] = InferSchema(value, marker);
                    if (normalized == raw)
                        AddExample(media, value, exampleName);
                    Warn(operation, "JSON body contains template variables or nonstandard JSON syntax. Its raw text is preserved; unresolved values have unconstrained schemas.");
                }
                catch (JsonException)
                {
                    media["schema"] = new JsonObject { ["x-postman-invalid-json"] = true };
                    Warn(operation, "Body is declared as JSON but cannot be parsed, even as a Postman template. Its raw text is preserved without inventing a schema.");
                }
                media["x-postman-raw-body"] = raw;
            }
        }
        else
        {
            media["schema"] = new JsonObject { ["type"] = "string" };
            AddExample(media, JsonValue.Create(raw), exampleName);
        }
        return media;
    }

    private static string ReplaceUnquotedVariables(string raw, string marker)
    {
        var builder = new StringBuilder(raw.Length);
        bool quoted = false;
        bool escaped = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char value = raw[i];
            if (!quoted && value == '{' && i + 1 < raw.Length && raw[i + 1] == '{')
            {
                int end = raw.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    builder.Append('"').Append(marker).Append('"');
                    i = end + 1;
                    continue;
                }
            }
            builder.Append(value);
            if (escaped) { escaped = false; continue; }
            if (quoted && value == '\\') { escaped = true; continue; }
            if (value == '"') quoted = !quoted;
        }
        return builder.ToString();
    }

    private static JsonObject FormMedia(JsonArray? fields, bool multipart)
    {
        var properties = new JsonObject();
        var example = new JsonObject();
        var encoding = new JsonObject();
        foreach (JsonObject field in fields?.OfType<JsonObject>().Where(value => !Disabled(value)) ?? [])
        {
            if (Text(field["key"]) is not { Length: > 0 } key)
                continue;
            bool file = Text(field["type"]) == "file";
            var schema = new JsonObject { ["type"] = "string" };
            if (file)
                schema["format"] = "binary";
            SetDescription(schema, field["description"]);
            if (file && field["src"] is JsonArray)
                schema = new JsonObject { ["type"] = "array", ["items"] = schema };
            if (properties[key] is JsonObject existing)
            {
                if (Text(existing["type"]) == "array")
                    existing["items"] = MergeSchema((JsonObject)existing["items"]!, schema);
                else
                    properties[key] = new JsonObject { ["type"] = "array", ["items"] = MergeSchema(existing, schema) };
                if (!file)
                {
                    if (example[key] is not JsonArray repeated)
                        example[key] = repeated = new JsonArray(example[key]?.DeepClone());
                    repeated.Add(Text(field["value"]) ?? "");
                }
            }
            else
            {
                properties[key] = schema;
                if (!file)
                    example[key] = Text(field["value"]) ?? "";
            }
            var fieldEncoding = new JsonObject();
            if (multipart && MediaType(Text(field["contentType"])) is { } fieldType)
                fieldEncoding["contentType"] = fieldType;
            if (!multipart)
            {
                fieldEncoding["style"] = "form";
                fieldEncoding["explode"] = true;
            }
            if (fieldEncoding.Count > 0)
                encoding[key] = fieldEncoding;
        }
        var media = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object", ["properties"] = properties } };
        if (example.Count > 0)
            AddExample(media, example);
        if (encoding.Count > 0)
            media["encoding"] = encoding;
        return media;
    }

    private void AddResponses(JsonObject operation, JsonArray? examples, string method)
    {
        var responses = (JsonObject)operation["responses"]!;
        foreach (JsonObject example in examples?.OfType<JsonObject>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            string code = Text(example["code"]) ?? "default";
            if (code != "default" && (!int.TryParse(code, out int status) || status < 100 || status > 599))
            {
                Warn(operation, $"Saved response status '{code}' is not an HTTP status; preserved as a default response.");
                code = "default";
            }
            var response = new JsonObject { ["description"] = Text(example["status"]) ?? Text(example["name"]) ?? "Saved response" };
            var headers = new JsonObject();
            foreach (JsonObject header in Headers(example["header"]))
            {
                if (Text(header["key"]) is not { Length: > 0 } key || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;
                var responseHeader = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } };
                SetDescription(responseHeader, header["description"]);
                AddExample(responseHeader, JsonValue.Create(Text(header["value"]) ?? ""));
                string existingKey = headers.Select(pair => pair.Key).FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
                if (headers[existingKey] is JsonObject existingHeader)
                    MergeExamples(existingHeader, responseHeader);
                else
                    headers[existingKey] = responseHeader;
            }
            if (headers.Count > 0)
                response["headers"] = headers;
            string? raw = Text(example["body"]);
            if (!string.IsNullOrEmpty(raw))
            {
                if (method == "head" || code is "204" or "304" || code.StartsWith('1'))
                    Warn(operation, $"Saved {code} response contains a body forbidden by HTTP semantics; retained only in x-postman-variants.");
                else
                {
                    string type = MediaType(HeaderValue(example["header"], "Content-Type")) ?? InferMediaType(raw);
                    response["content"] = new JsonObject { [type] = BodyMedia(raw, type, operation, Text(example["name"])) };
                }
            }
            if (responses[code] is JsonObject existing)
                MergeResponse(existing, response);
            else
                responses[code] = response;
        }
        if (responses.Count == 0)
        {
            responses["default"] = new JsonObject { ["description"] = "No response examples were saved in the Postman collection; status codes and response schemas are unknown." };
            Warn(operation, "No saved responses: status codes and response schemas cannot be inferred.");
        }
    }

    private static string? MediaType(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Split(';', 2)[0].Trim();
    private static bool IsJsonMediaType(string type) => type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    private static string InferMediaType(string raw)
    {
        try { JsonNode.Parse(raw); return "application/json"; }
        catch (JsonException)
        {
            try
            {
                JsonNode.Parse(ReplaceUnquotedVariables(raw, "__postman_variable__"), documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
                });
                return "application/json";
            }
            catch (JsonException)
            {
                string trimmed = raw.TrimStart();
                if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                    return "text/html";
                return trimmed.StartsWith('<') ? "application/xml" : "text/plain";
            }
        }
    }

    private JsonObject InferSchema(JsonNode? node, string? unresolvedMarker = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node == null)
            return new JsonObject { ["x-postman-observed-null"] = true };
        if (unresolvedMarker != null && Text(node) == unresolvedMarker)
            return new JsonObject { ["x-postman-unresolved-value"] = true };
        if (node is JsonObject obj)
        {
            var properties = new JsonObject();
            foreach (var pair in obj)
                properties[pair.Key] = InferSchema(pair.Value, unresolvedMarker);
            return new JsonObject { ["type"] = "object", ["properties"] = properties };
        }
        if (node is JsonArray array)
        {
            JsonObject? items = null;
            foreach (JsonNode? value in array)
                items = items == null ? InferSchema(value, unresolvedMarker) : MergeSchema(items, InferSchema(value, unresolvedMarker));
            return new JsonObject { ["type"] = "array", ["items"] = items ?? new JsonObject() };
        }
        string type = node.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => System.Numerics.BigInteger.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out _) ? "integer" : "number",
            _ => "string"
        };
        return new JsonObject { ["type"] = type };
    }

    private static JsonObject MergeSchema(JsonObject left, JsonObject right)
    {
        if (JsonNode.DeepEquals(left, right))
            return (JsonObject)left.DeepClone();
        if (left.ContainsKey("x-postman-unresolved-value") || left.ContainsKey("x-postman-invalid-json"))
            return (JsonObject)left.DeepClone();
        if (right.ContainsKey("x-postman-unresolved-value") || right.ContainsKey("x-postman-invalid-json"))
            return (JsonObject)right.DeepClone();
        bool leftNull = left.Count == 1 && left.ContainsKey("x-postman-observed-null");
        bool rightNull = right.Count == 1 && right.ContainsKey("x-postman-observed-null");
        if (leftNull || rightNull)
        {
            var nullable = (JsonObject)(leftNull ? right : left).DeepClone();
            if (nullable["type"] != null)
                nullable["nullable"] = true;
            else if (nullable["anyOf"] is JsonArray alternatives)
                foreach (JsonObject alternative in alternatives.OfType<JsonObject>())
                    alternative["nullable"] = true;
            return nullable;
        }
        // An unconstrained sample (such as an empty array) contributes no type information.
        if (left.Count == 0) return (JsonObject)right.DeepClone();
        if (right.Count == 0) return (JsonObject)left.DeepClone();
        string? type = Text(left["type"]);
        if (type != null && type == Text(right["type"]))
        {
            var merged = (JsonObject)left.DeepClone();
            if (Text(right["nullable"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                merged["nullable"] = true;
            if (type == "object")
            {
                var properties = (JsonObject)merged["properties"]!;
                foreach (var pair in (JsonObject)right["properties"]!)
                    properties[pair.Key] = properties[pair.Key] is JsonObject existing ? MergeSchema(existing, (JsonObject)pair.Value!) : pair.Value!.DeepClone();
            }
            else if (type == "array")
                merged["items"] = MergeSchema((JsonObject)left["items"]!, (JsonObject)right["items"]!);
            return merged;
        }
        var choices = new JsonArray();
        IEnumerable<JsonObject> Alternatives(JsonObject schema) => schema["anyOf"] is JsonArray array ? array.OfType<JsonObject>() : [schema];
        foreach (JsonObject alternative in Alternatives(left).Concat(Alternatives(right)))
        {
            JsonObject? sameType = choices.OfType<JsonObject>().FirstOrDefault(choice => Text(choice["type"]) != null && Text(choice["type"]) == Text(alternative["type"]));
            if (sameType != null)
                choices[choices.IndexOf(sameType)] = MergeSchema(sameType, alternative);
            else if (!choices.Any(choice => JsonNode.DeepEquals(choice, alternative)))
                choices.Add(alternative.DeepClone());
        }
        return new JsonObject { ["anyOf"] = choices };
    }

    private static void AddExample(JsonObject target, JsonNode? value, string? name = null)
    {
        if (target["examples"] is not JsonObject examples)
            target["examples"] = examples = new JsonObject();
        string key = name == null ? "example1" : System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9._-]", "_");
        if (key.Length == 0) key = "example1";
        string candidate = key;
        for (int i = 2; examples.ContainsKey(candidate); i++) candidate = key + "_" + i;
        var example = new JsonObject { ["value"] = value?.DeepClone() };
        if (name != null) example["summary"] = name;
        examples[candidate] = example;
    }

    private static void MergeExamples(JsonObject target, JsonObject source)
    {
        if (source["examples"] is JsonObject examples)
            foreach (var pair in examples)
                AddExample(target, pair.Value?["value"], Text(pair.Value?["summary"]) ?? pair.Key);
    }

    private static void MergeContent(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (target[pair.Key] is JsonObject existing)
            {
                var incoming = (JsonObject)pair.Value!;
                existing["schema"] = MergeSchema((JsonObject)existing["schema"]!, (JsonObject)incoming["schema"]!);
                MergeExamples(existing, incoming);
                if (incoming["encoding"] is JsonObject encoding)
                {
                    if (existing["encoding"] is not JsonObject existingEncoding)
                        existing["encoding"] = existingEncoding = new JsonObject();
                    foreach (var field in encoding)
                        if (!existingEncoding.ContainsKey(field.Key)) existingEncoding[field.Key] = field.Value?.DeepClone();
                }
            }
            else
                target[pair.Key] = pair.Value!.DeepClone();
        }
    }

    private static void MergeResponse(JsonObject target, JsonObject source)
    {
        MergeDescription(target, source);
        if (source["content"] is JsonObject content)
        {
            if (target["content"] is JsonObject existing) MergeContent(existing, content);
            else target["content"] = content.DeepClone();
        }
        if (source["headers"] is JsonObject headers)
        {
            if (target["headers"] is not JsonObject existingHeaders)
                target["headers"] = existingHeaders = new JsonObject();
            foreach (var pair in headers)
            {
                string key = existingHeaders.Select(header => header.Key).FirstOrDefault(name => name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) ?? pair.Key;
                if (existingHeaders[key] is JsonObject existing) MergeExamples(existing, (JsonObject)pair.Value!);
                else existingHeaders[key] = pair.Value!.DeepClone();
            }
        }
    }
}
