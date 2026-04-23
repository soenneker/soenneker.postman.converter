using Soenneker.Postman.Converter.Abstract;
using Soenneker.Tests.HostedUnit;
using System.Net.Http;
using System.IO;
using System.Threading.Tasks;
using Microsoft.OpenApi;
using AwesomeAssertions;

namespace Soenneker.Postman.Converter.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class PostmanConverterTests : HostedUnitTest
{
    private const string _fastlyPostmanPath = @"C:\cloudflare\fastly postman.json";
    private readonly IPostmanConverter _util;

    public PostmanConverterTests(Host host) : base(host)
    {
        _util = Resolve<IPostmanConverter>(true);
    }

    [Test]
    public void Default()
    {
    }

    [Skip("Manual")]
    [Test]
    public async Task ConvertFile_should_convert_fastly_collection()
    {
        File.Exists(_fastlyPostmanPath).Should().BeTrue($"Expected Fastly Postman collection to exist at '{_fastlyPostmanPath}'.");

        OpenApiDocument result = await _util.ConvertFile(_fastlyPostmanPath, System.Threading.CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Info);
        result.Info.Title.Should().Be("Fastly API");
        Assert.NotNull(result.Paths);
        result.Paths.Should().NotBeEmpty();
        result.Paths.TryGetValue("/customer/{customer_id}/billing_address", out IOpenApiPathItem? pathItem).Should().BeTrue();
        Assert.NotNull(pathItem);
        Assert.NotNull(result.Servers);
        result.Servers.Should().Contain(server => server.Url == "https://api.fastly.com");
        Assert.NotNull(pathItem!.Operations);
        pathItem.Operations.TryGetValue(HttpMethod.Get, out OpenApiOperation? operation).Should().BeTrue();
        Assert.NotNull(operation);
        operation!.Summary.Should().Be("Get a billing address");
        Assert.NotNull(operation.Responses);
        operation.Responses.Keys.Should().Contain("200");
    }

    [Skip("Manual")]
    [Test]
    public async Task SaveOpenApiFile_should_write_openapi_json_for_fastly_collection()
    {
        File.Exists(_fastlyPostmanPath).Should().BeTrue($"Expected Fastly Postman collection to exist at '{_fastlyPostmanPath}'.");

        string outputPath = Path.Combine(Path.GetTempPath(), $"fastly-openapi-{Path.GetRandomFileName()}.json");

        try
        {
            await _util.SaveOpenApiFile(_fastlyPostmanPath, outputPath, System.Threading.CancellationToken.None);

            File.Exists(outputPath).Should().BeTrue();

            string json = await File.ReadAllTextAsync(outputPath, System.Threading.CancellationToken.None);

            json.Should().Contain("\"openapi\"");
            json.Should().Contain("\"Fastly API\"");
            json.Should().Contain("\"/customer/{customer_id}/billing_address\"");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }
}

