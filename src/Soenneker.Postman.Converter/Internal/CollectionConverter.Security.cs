using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Soenneker.Postman.Converter.Internal;

internal sealed partial class CollectionConverter
{
    private static JsonObject? EffectiveAuth(JsonNode? node, JsonObject? inherited)
        => node is JsonObject auth && Text(auth["type"]) is not (null or "inherit") ? auth : inherited;

    private void ApplySecurity(JsonObject operation, JsonObject? auth, JsonNode? headers, Dictionary<string, Variable> variables)
    {
        string? type = Text(auth?["type"])?.ToLowerInvariant();
        JsonObject? scheme = null;
        var scopes = new JsonArray();
        string schemeName = (type ?? "authorization") + "Auth";
        switch (type)
        {
            case "apikey":
                string? key = AuthValue(auth!, "key");
                string location = AuthValue(auth!, "in") ?? "header";
                if (string.IsNullOrWhiteSpace(key) || location is not ("header" or "query"))
                    Warn(operation, "API key authentication has a missing key or unsupported location; its configuration is retained in x-postman-auth.");
                else
                {
                    key = ResolveVariables(key, variables);
                    if (Variables.IsMatch(key))
                        Warn(operation, "The API key parameter name contains an unresolved variable.");
                    scheme = new JsonObject { ["type"] = "apiKey", ["name"] = key, ["in"] = location };
                    RemoveAuthParameter(operation, key, location);
                }
                break;
            case "bearer":
            case "basic":
            case "digest":
                scheme = new JsonObject { ["type"] = "http", ["scheme"] = type };
                break;
            case "oauth2":
                scheme = OAuthScheme(auth!, operation, variables);
                break;
            case null:
            case "noauth":
                break;
            default:
                Warn(operation, $"Authentication type '{type}' has no faithful automatic OpenAPI mapping; its configuration is retained in x-postman-auth.");
                break;
        }
        if (scheme == null && type is null or "noauth")
        {
            string? authorization = HeaderValue(headers, "Authorization");
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                string prefix = authorization.Split(' ', 2)[0].ToLowerInvariant();
                scheme = prefix is "bearer" or "basic" or "digest"
                    ? new JsonObject { ["type"] = "http", ["scheme"] = prefix }
                    : new JsonObject { ["type"] = "apiKey", ["name"] = "Authorization", ["in"] = "header" };
                schemeName = prefix is "bearer" or "basic" or "digest" ? prefix + "Auth" : "authorizationAuth";
            }
        }
        if (scheme == null)
        {
            operation["security"] = new JsonArray(new JsonObject());
            if (auth != null && type != "noauth")
                operation["x-postman-auth"] = auth.DeepClone();
            return;
        }
        string? existing = _schemes.FirstOrDefault(pair => JsonNode.DeepEquals(pair.Value, scheme)).Key;
        if (existing != null)
            schemeName = existing;
        else
        {
            string seed = schemeName;
            for (int i = 2; _schemes.ContainsKey(schemeName); i++) schemeName = seed + i;
            _schemes[schemeName] = scheme;
        }
        operation["security"] = new JsonArray(new JsonObject { [schemeName] = scopes });
    }

    private JsonObject OAuthScheme(JsonObject auth, JsonObject operation, Dictionary<string, Variable> variables)
    {
        string? grant = AuthValue(auth, "grant_type");
        string? authUrl = AuthValue(auth, "authUrl");
        string? tokenUrl = AuthValue(auth, "accessTokenUrl");
        if (authUrl != null) authUrl = ResolveVariables(authUrl, variables);
        if (tokenUrl != null) tokenUrl = ResolveVariables(tokenUrl, variables);
        string? flowName = grant switch
        {
            "authorization_code" or "authorization_code_with_pkce" => "authorizationCode",
            "implicit" => "implicit", "password_credentials" or "password" => "password", "client_credentials" => "clientCredentials",
            null when !string.IsNullOrWhiteSpace(authUrl) && !string.IsNullOrWhiteSpace(tokenUrl) => "authorizationCode",
            _ => null
        };
        bool needsAuth = flowName is "authorizationCode" or "implicit";
        bool needsToken = flowName is "authorizationCode" or "password" or "clientCredentials";
        if (flowName == null || needsAuth && !UsableOAuthUrl(authUrl) || needsToken && !UsableOAuthUrl(tokenUrl))
        {
            Warn(operation, "OAuth2 flow configuration is incomplete or unresolved. Only bearer-token transport can be described; configuration is retained in x-postman-auth.");
            operation["x-postman-auth"] = auth.DeepClone();
            return new JsonObject { ["type"] = "http", ["scheme"] = "bearer" };
        }
        if (grant == null)
            Warn(operation, "OAuth2 grant type is absent; authorizationCode was inferred from the authorization and token URLs.");
        string scopeValue = AuthValue(auth, "scope") ?? "";
        scopeValue = ResolveVariables(scopeValue, variables);
        var scopes = new JsonObject();
        if (Variables.IsMatch(scopeValue))
            Warn(operation, "OAuth2 scopes contain unresolved variables and cannot be enumerated.");
        else
        {
            if (scopeValue.Contains(','))
                Warn(operation, "Comma-separated OAuth2 scopes were interpreted as individual scopes; confirm the authorization server's scope format.");
            foreach (string scope in scopeValue.Split([' ', ',', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                scopes[scope] = "Scope listed in the Postman authentication configuration.";
            }
        }
        var flow = new JsonObject { ["scopes"] = scopes };
        // Scopes requested by a Postman token helper do not establish the minimum scopes of each endpoint.
        if (scopes.Count > 0)
            Warn(operation, "OAuth2 configured scopes are listed on the scheme; per-endpoint required scopes are unknown.");
        if (needsAuth) flow["authorizationUrl"] = authUrl;
        if (needsToken) flow["tokenUrl"] = tokenUrl;
        string? refreshUrl = AuthValue(auth, "refreshTokenUrl");
        if (!string.IsNullOrWhiteSpace(refreshUrl))
        {
            refreshUrl = ResolveVariables(refreshUrl, variables);
            if (UsableOAuthUrl(refreshUrl)) flow["refreshUrl"] = refreshUrl;
            else Warn(operation, "OAuth2 refresh URL is unresolved and was omitted.");
        }
        if (AuthValue(auth, "addTokenTo") is { } tokenLocation && tokenLocation != "header")
            Warn(operation, "OAuth2 token placement is not the standard Authorization header; inspect the Postman configuration before using a generated client.");
        return new JsonObject { ["type"] = "oauth2", ["flows"] = new JsonObject { [flowName] = flow } };
    }

    private static bool UsableOAuthUrl(string? value) => !string.IsNullOrWhiteSpace(value) && !Variables.IsMatch(value) &&
        Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _);

    private static string? AuthValue(JsonObject auth, string key)
    {
        JsonNode? configuration = auth[Text(auth["type"]) ?? ""];
        if (configuration is JsonObject obj)
            return Text(obj[key]);
        return configuration is JsonArray array
            ? array.OfType<JsonObject>().Where(item => Text(item["key"]) == key).Select(item => Text(item["value"])).FirstOrDefault() : null;
    }

    private static void RemoveAuthParameter(JsonObject operation, string name, string location)
    {
        var parameters = (JsonArray)operation["parameters"]!;
        for (int i = parameters.Count - 1; i >= 0; i--)
            if (Text(parameters[i]?["in"]) == location && string.Equals(Text(parameters[i]?["name"]), name,
                    location == "header" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                parameters.RemoveAt(i);
    }
}
