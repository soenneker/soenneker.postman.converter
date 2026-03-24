using Soenneker.Postman.Converter.Abstract;
using Soenneker.Tests.FixturedUnit;
using System.Net.Http;
using System.IO;
using System.Threading.Tasks;
using Microsoft.OpenApi;
using Soenneker.Facts.Manual;
using Xunit;

namespace Soenneker.Postman.Converter.Tests;

[Collection("Collection")]
public sealed class PostmanConverterTests : FixturedUnitTest
{
    private const string _fastlyPostmanPath = @"C:\cloudflare\fastly postman.json";
    private readonly IPostmanConverter _util;

    public PostmanConverterTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
        _util = Resolve<IPostmanConverter>(true);
    }

    [Fact]
    public void Default()
    {
    }

    [ManualFact]
    public async Task ConvertFile_should_convert_fastly_collection()
    {
        Assert.True(File.Exists(_fastlyPostmanPath), $"Expected Fastly Postman collection to exist at '{_fastlyPostmanPath}'.");

        OpenApiDocument result = await _util.ConvertFile(_fastlyPostmanPath, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(result.Info);
        Assert.Equal("Fastly API", result.Info.Title);
        Assert.NotNull(result.Paths);
        Assert.NotEmpty(result.Paths);
        Assert.True(result.Paths.TryGetValue("/customer/{customer_id}/billing_address", out IOpenApiPathItem? pathItem));
        Assert.NotNull(pathItem);
        Assert.NotNull(result.Servers);
        Assert.Contains(result.Servers, server => server.Url == "https://api.fastly.com");
        Assert.NotNull(pathItem!.Operations);
        Assert.True(pathItem.Operations.TryGetValue(HttpMethod.Get, out OpenApiOperation? operation));
        Assert.NotNull(operation);
        Assert.Equal("Get a billing address", operation!.Summary);
        Assert.NotNull(operation.Responses);
        Assert.Contains("200", operation.Responses.Keys);
    }

    [ManualFact]
    public async Task SaveOpenApiFile_should_write_openapi_json_for_fastly_collection()
    {
        Assert.True(File.Exists(_fastlyPostmanPath), $"Expected Fastly Postman collection to exist at '{_fastlyPostmanPath}'.");

        string outputPath = Path.Combine(Path.GetTempPath(), $"fastly-openapi-{Path.GetRandomFileName()}.json");

        try
        {
            await _util.SaveOpenApiFile(_fastlyPostmanPath, outputPath, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath));

            string json = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);

            Assert.Contains("\"openapi\"", json);
            Assert.Contains("\"Fastly API\"", json);
            Assert.Contains("\"/customer/{customer_id}/billing_address\"", json);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }
}