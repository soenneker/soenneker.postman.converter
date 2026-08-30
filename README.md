[![](https://img.shields.io/nuget/v/soenneker.postman.converter.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.postman.converter/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.postman.converter.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.postman.converter/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/codeql.yml)

# Soenneker.Postman.Converter

Converts Postman collection JSON into an OpenAPI v3 document or JSON file.

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

The converter maps nested folders to tags, collection variables to server/path values, request headers and bodies to operation inputs, saved examples to response schemas, and supported Postman authentication metadata to OpenAPI security schemes. Unsupported HTTP methods fail explicitly instead of being emitted as a different operation.

Postman scripts, test assertions, and runtime behavior are not executable OpenAPI concepts and are not carried into the output. Review the generated document before using it for client generation or publishing.
