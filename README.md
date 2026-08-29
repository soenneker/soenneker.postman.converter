[![](https://img.shields.io/nuget/v/soenneker.postman.converter.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.postman.converter/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.postman.converter/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.postman.converter/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.postman.converter.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.postman.converter/)

# Soenneker.Postman.Converter

A utility library that converts Postman schemas to OpenApi.

## Install

```bash
dotnet add package Soenneker.Postman.Converter
```

## Quick start

```csharp
using Soenneker.Postman.Converter.Registrars;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
var result = services.AddPostmanConverterAsSingleton();
```

Adds `IPostmanConverter` as a singleton service.

## What you get

- `IPostmanConverter` — A utility library that converts Postman schemas to OpenApi.
- `PostmanConverterRegistrar` — A utility library that converts Postman schemas to OpenApi.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `IPostmanConverter.Convert(postmanJson, cancellationToken)` | Converts a Postman collection JSON payload into an OpenAPI document. | A task whose result is the requested openAPI Document. |
| `IPostmanConverter.ConvertToJson(postmanJson, cancellationToken)` | Converts a Postman collection JSON payload into an OpenAPI v3 JSON string. | A task whose result is the text returned by convert To JSON. |
| `IPostmanConverter.ConvertUrl(url, cancellationToken)` | Downloads a Postman collection from a URL and converts it into an OpenAPI document. | A task whose result is the requested openAPI Document. |
| `IPostmanConverter.ConvertUrlToJson(url, cancellationToken)` | Downloads a Postman collection from a URL and converts it into an OpenAPI v3 JSON string. | A task whose result is the text returned by convert URL To JSON. |
| `IPostmanConverter.ConvertFile(filePath, cancellationToken)` | Reads a Postman collection file and converts it into an OpenAPI document. | A task whose result is the requested openAPI Document. |
| `IPostmanConverter.ConvertFileToJson(filePath, cancellationToken)` | Reads a Postman collection file and converts it into an OpenAPI v3 JSON string. | A task whose result is the text returned by convert File To JSON. |
| `IPostmanConverter.SaveOpenApiFile(postmanFilePath, openApiFilePath, cancellationToken)` | Reads a Postman collection file and saves the converted OpenAPI JSON to disk. | A task that completes when the openapi file has been saved. |
| `IPostmanConverter.SaveOpenApiUrl(url, openApiFilePath, cancellationToken)` | Downloads a Postman collection from a URL and saves the converted OpenAPI JSON to disk. | A task that completes when the openapi url has been saved. |
| `IPostmanConverter.ToJson(document)` | Serializes an OpenAPI document as v3 JSON. | Returns `string`. |
| `PostmanConverterRegistrar.AddPostmanConverterAsSingleton(services)` | Adds `IPostmanConverter` as a singleton service. | The same service collection, so additional registrations can be chained. |
| `PostmanConverterRegistrar.AddPostmanConverterAsScoped(services)` | Adds `IPostmanConverter` as a scoped service. | The same service collection, so additional registrations can be chained. |

## Practical notes

- Cancellation stops pending work; it does not undo work that has already completed.
