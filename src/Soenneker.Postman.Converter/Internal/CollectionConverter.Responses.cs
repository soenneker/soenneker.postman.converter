using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soenneker.Postman.Converter.Internal;

internal sealed partial class CollectionConverter
{
    private string ResolveResponseCode(JsonObject example, JsonObject operation, out string? inference)
    {
        inference = Text(example["x-postman-response-inference"]);
        if (Text(example["code"]) is { } explicitCode)
        {
            if (IsStatusCode(explicitCode) || explicitCode is "default" or "1XX" or "2XX" or "3XX" or "4XX" or "5XX")
                return explicitCode;
            Warn(operation, $"Saved response status '{explicitCode}' is not an HTTP status; preserved as a default response.");
            return "default";
        }

        string? status = StatusFromText(Text(example["status"]), allowReasonPhrase: true) ??
                         StatusFromText(Text(example["name"]), allowReasonPhrase: true);
        if (status != null)
        {
            inference = "response-status-or-name";
            Warn(operation, $"Response status {status} was inferred from the saved response status or name.");
            return status;
        }

        string? raw = Text(example["body"]);
        JsonNode? body;
        try { body = raw == null ? null : JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return "default"; }
        if (body is not (JsonObject or JsonArray))
            return "default";
        bool error = Regex.IsMatch((Text(example["name"]) ?? "") + " " + (Text(example["status"]) ?? ""),
            @"\b(error|fail(?:ed|ure)?|invalid|denied|unauthori[sz]ed|forbidden|not found)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (body is JsonObject obj)
        {
            error |= obj.Any(pair => pair.Key.Equals("error", StringComparison.OrdinalIgnoreCase) && pair.Value != null && pair.Value.ToJsonString() is not "false" and not "null" and not "\"\"");
            error |= obj["success"]?.ToJsonString() == "false";
            foreach (string field in new[] { "status", "statusCode" })
                if (int.TryParse(Text(obj[field]), out int code) && code >= 400 && code <= 599)
                    error = true;
            if (obj["errors"] is JsonArray errors && errors.Count > 0 || obj["errors"] is JsonObject errorsObject && errorsObject.Count > 0)
                error = true;
        }
        if (error)
            return "default";

        if (Text(example["x-postman-documented-success-status"]) is string documentedStatus)
        {
            inference = "description-status";
            Warn(operation, $"Response status {documentedStatus} was inferred from the documented success status.");
            return documentedStatus;
        }
        if (!options.InferSuccessResponsesFromBodies)
            return "default";

        inference = "unclassified-json-body";
        Warn(operation, "A saved JSON response has no HTTP status or error indicators. Its payload is exposed as an inferred 2XX response; the default response and original example are retained.");
        return "2XX";
    }

    private static bool IsStatusCode(string value) => value.Length == 3 && int.TryParse(value, out int status) && status >= 100 && status <= 599;

    private static string? StatusFromText(string? text, bool allowReasonPhrase)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string[] codes = Regex.Matches(text, @"(?<![A-Za-z0-9])[1-5][0-9]{2}(?![A-Za-z0-9])")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (codes.Length == 1)
            return codes[0];
        if (codes.Length > 1)
            return null;
        string reason = Regex.Replace(text.Trim(), @"[\s_-]", "");
        return allowReasonPhrase && Enum.TryParse(reason, ignoreCase: true, out HttpStatusCode status) && Enum.IsDefined(status)
            ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private JsonArray ReadResponseExamples(JsonObject item, JsonObject request, JsonObject operation)
    {
        var result = item["response"] is JsonArray saved ? (JsonArray)saved.DeepClone() : new JsonArray();
        string? description = Description(request["description"] ?? item["description"]);
        if (!string.IsNullOrWhiteSpace(description))
        {
            string[] documentedSuccessCodes = Regex.Matches(description, @"(?i)\bsuccessful\s+response\s+returns?[^\r\n.]{0,70}?\b(2[0-9]{2})\b")
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
            if (documentedSuccessCodes.Length == 1)
                foreach (JsonObject example in result.OfType<JsonObject>())
                    example["x-postman-documented-success-status"] = documentedSuccessCodes[0];
            // Restrict JSON extraction to explicitly labelled response sections, never request examples.
            bool responseSection = false;
            string? sectionCode = null;
            string[] lines = description.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith('#') || Regex.IsMatch(line, @"^(?:\*\*)?(?:sample\s+)?(?:success(?:ful)?\s+|error\s+)?(?:response|request)\b", RegexOptions.IgnoreCase))
                {
                    responseSection = Regex.IsMatch(line, @"\bresponse\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(line, @"\brequest\b", RegexOptions.IgnoreCase);
                    sectionCode = responseSection ? StatusFromText(line, false) : null;
                    if (responseSection && Regex.IsMatch(line, @"\berror\b", RegexOptions.IgnoreCase) && sectionCode == null)
                        sectionCode = "default";
                }
                if (!line.StartsWith("```", StringComparison.Ordinal))
                    continue;
                int start = ++i;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) i++;
                if (!responseSection || i == lines.Length)
                    continue;
                string body = string.Join('\n', lines[start..i]);
                try { JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
                catch (JsonException) { continue; }
                var example = new JsonObject { ["name"] = "Documented response", ["body"] = body, ["x-postman-response-inference"] = "description-response-example" };
                if (sectionCode != null) example["code"] = sectionCode;
                else if (documentedSuccessCodes.Length == 1) example["x-postman-documented-success-status"] = documentedSuccessCodes[0];
                result.Add(example);
                Warn(operation, "A JSON example from a documented response section was included in the response contract.");
            }
            foreach (string code in documentedSuccessCodes)
            {
                if (!result.OfType<JsonObject>().Any(example => Text(example["code"]) == code))
                    result.Add(new JsonObject { ["code"] = code, ["name"] = "Documented success response", ["x-postman-response-inference"] = "description-status" });
            }
        }
        return result;
    }
}
