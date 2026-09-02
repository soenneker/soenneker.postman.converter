using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.OpenApi;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Postman.Converter.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class PostmanConverterTests : HostedUnitTest
{
    private readonly IPostmanConverter _util;

    public PostmanConverterTests(Host host) : base(host)
    {
        _util = Resolve<IPostmanConverter>(true);
    }

    [Test]
    public async Task Concurrent_conversions_keep_operation_ids_isolated(CancellationToken cancellationToken)
    {
        const string collection = """
                                  {
                                    "info": { "name": "Example" },
                                    "item": [
                                      { "name": "List", "request": { "method": "GET", "url": "https://example.com/one" } },
                                      { "name": "List", "request": { "method": "GET", "url": "https://example.com/two" } }
                                    ]
                                  }
                                  """;

        Task<OpenApiDocument>[] conversions = Enumerable.Range(0, 20)
            .Select(_ => _util.Convert(collection, cancellationToken: cancellationToken).AsTask())
            .ToArray();

        OpenApiDocument[] documents = await Task.WhenAll(conversions);

        foreach (OpenApiDocument document in documents)
        {
            document.Paths!["/one"].Operations![HttpMethod.Get].OperationId.Should().Be("List");
            document.Paths["/two"].Operations![HttpMethod.Get].OperationId.Should().Be("List2");
        }
    }

    [Test]
    public async Task Unsupported_methods_fail_instead_of_becoming_get(CancellationToken cancellationToken)
    {
        const string collection = """
                                  {
                                    "info": { "name": "Example" },
                                    "item": [
                                      { "name": "Custom", "request": { "method": "CUSTOM", "url": "https://example.com/custom" } }
                                    ]
                                  }
                                  """;

        Func<Task> action = () => _util.Convert(collection, cancellationToken: cancellationToken).AsTask();

        await action.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*unsupported HTTP method 'CUSTOM'*");
    }
}
