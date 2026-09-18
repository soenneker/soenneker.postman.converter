[![](https://img.shields.io/nuget/v/soenneker.postman.converter.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.postman.converter/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.postman.converter.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.postman.converter/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/codeql.yml)

# Soenneker.Postman.Converter

Converts Postman v2.0/v2.1 collection JSON into OpenAPI 3.0 documents and JSON files, preserving source requests, examples, and conversion diagnostics.

## Installation

```bash
dotnet add package Soenneker.Postman.Converter
```

## Registration

```csharp
using Soenneker.Postman.Converter.Registrars;

services.AddPostmanConverterAsSingleton();
```

The converter is safe to reuse concurrently. A scoped registration is also available; its HTTP transport remains process-wide.

## Convert JSON or a file

```csharp
using Microsoft.OpenApi;
using Soenneker.Postman.Converter.Abstract;

IPostmanConverter converter =
    serviceProvider.GetRequiredService<IPostmanConverter>();

string collectionJson = await File.ReadAllTextAsync(
    "postman_collection.json",
    cancellationToken);

OpenApiDocument document =
    await converter.Convert(collectionJson, cancellationToken);

string openApiJson = converter.ToJson(document);
```

For direct file conversion and an atomic output replacement:

```csharp
await converter.SaveOpenApiFile(
    "postman_collection.json",
    "openapi.json",
    cancellationToken);
```

`ConvertFile` and `ConvertFileToJson` return the document or JSON without writing an output file.

## Convert a collection URL

```csharp
await converter.SaveOpenApiUrl(
    "https://example.com/postman_collection.json",
    "openapi.json",
    cancellationToken);
```

`ConvertUrl` and `ConvertUrlToJson` use the registered HTTP client and return the result in memory. Only pass trusted URLs when this runs in a server process, because the converter performs an HTTP GET from that process and can reach destinations available to it.

## Conversion behavior

The converter supports raw and structured URLs, collection API wrappers, string requests, nested folders, scoped variables, and Postman v2.0 object or v2.1 array authentication settings.

| Collection information | OpenAPI representation |
| --- | --- |
| Absolute URLs, ports, base URL variables, multiple hosts | Operation servers; unresolved origins remain named server variables with warnings |
| `:id`, `{{id}}`, embedded variables in compound identifiers | Required path parameters, including URL-variable descriptions and examples; encoded literal path text is preserved |
| Structured or raw query strings, headers, cookies | Parameters with string examples; repeated query keys use arrays with form/explode serialization; disabled entries are excluded |
| Raw JSON, text, XML, HTML, URL-encoded forms, multipart uploads, binary files, GraphQL | Request media types, schemas, encoding, and examples |
| Saved responses | Status codes, media types, headers, named examples, and schemas combined across every saved example |
| Basic, bearer, digest, API key, OAuth2 authentication | Inherited or overridden security requirements and distinct registered schemes; API keys support query and header locations |
| Folder hierarchy and descriptions | Tags with folder paths and descriptions |
| Multiple requests with the same method and path | One combined operation with every original request in `x-postman-variants` |

### Environment values

Collection exports often omit the environment needed to resolve their server URLs. Supply those values explicitly:

```csharp
using Soenneker.Postman.Converter.Options;

var options = new PostmanConversionOptions
{
    Variables = new Dictionary<string, string?>
    {
        ["baseUrl"] = "https://api.example.com/rest"
    }
};

OpenApiDocument document = await converter.ConvertUrl(
    collectionUrl, options, cancellationToken);

await File.WriteAllTextAsync(
    "openapi.json", converter.ToJson(document), cancellationToken);
```

`Convert`, `ConvertFile`, and `ConvertUrl` have options overloads. Existing overloads remain available. Overrides take precedence over collection and folder values; names are case-sensitive. Nested URL variables are resolved with cycle detection. URL prefixes become servers, while path placeholders remain parameters. Body and query examples retain their source values rather than being rewritten with environment credentials.

### Inference and fidelity

A collection describes example requests, not the complete endpoint contract. The converter keeps this distinction explicit:

- JSON strings stay strings, including `"001"` and `"true"`. Property names are preserved exactly. Schemas inspect every array element and combine observed shapes with `anyOf` where needed.
- Example fields and request bodies are not assumed required. Query, header, and form values remain strings because their wire values do not prove an endpoint's logical type. Path parameters are required by OpenAPI.
- Null-only values and empty arrays do not establish a type. Unquoted Postman body variables remain unconstrained. Nonstandard or invalid JSON is preserved in `x-postman-raw-body` with diagnostics; it is not relabeled as a JSON string payload.
- Missing saved responses produce a documented `default` response, not an invented successful status or response schema. `Accept` does not establish a response's actual media type.
- Configured OAuth scopes are listed on the security scheme, but are not declared mandatory for every endpoint. Incomplete OAuth configuration falls back to bearer transport with a warning. Unsupported authentication remains visible in source metadata and warnings.
- OpenAPI has one operation per method/path. When use cases differ by query selectors, headers, host, or body shape, their inputs and responses are combined. This cannot express all correlations between those choices. `x-postman-variants` retains each original request, response list, effective authentication, and folder path for inspection. Equivalent path templates use the first parameter spelling.
- Scripts are not executed and their runtime effects are not inferred. Collection events and variables are retained in `x-postman-events` and `x-postman-variables`; request events and protocol settings are retained with source variants. Folder scripts and unsupported protocol behavior generate warnings.

Document and operation `x-postman-warnings` extensions describe missing information, unsupported features, inferred OAuth flows, and merged operations. Set `FailOnWarnings = true` to reject a conversion with any such diagnostic. A warning-free conversion still cannot establish undocumented validation rules, error responses, or runtime behavior.

Source extensions and examples contain original collection data, including authentication configuration when present. Treat the generated file with the same confidentiality as the input collection.

Unsupported HTTP methods and malformed request structures fail explicitly. To establish a complete production contract, supplement the collection with authoritative endpoint documentation or observed responses; conversion alone cannot prove endpoint behavior.

### Regression coverage

The offline regression fixture is the supplied LinkedIn Campaign Management collection: 72 requests, 25 generated paths, and 42 combined operations. It has no saved responses, unresolved environment/upload URLs, and a request containing `{{baseUrl}}adAccounts` without a separator. The converter preserves these facts and reports them instead of silently guessing fixes.

Run tests with Microsoft Testing Platform:

```powershell
dotnet test --project test/Soenneker.Postman.Converter.Tests -- --treenode-filter "/*/*/*/*"
```

For an independent document and example validation pass, export regression outputs into a fresh directory and run the included Python validator. This optional check needs `openapi-spec-validator`, which also installs `openapi-schema-validator`:

```powershell
$env:POSTMAN_CONVERTER_VALIDATION_DIR = Join-Path $PWD "artifacts/validation"
dotnet test --project test/Soenneker.Postman.Converter.Tests -- --treenode-filter "/*/*/ConversionFidelityTests/*"
python -m pip install openapi-spec-validator
python test/validate_generated_openapi.py artifacts/validation
```

Tests read the collection snapshot locally; they do not execute its requests or contact LinkedIn.
