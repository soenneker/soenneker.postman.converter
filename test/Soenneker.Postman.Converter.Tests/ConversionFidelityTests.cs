using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.OpenApi;
using Soenneker.Postman.Converter.Abstract;
using Soenneker.Postman.Converter.Options;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Postman.Converter.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class ConversionFidelityTests : HostedUnitTest
{
    private readonly IPostmanConverter _converter;

    public ConversionFidelityTests(Host host) : base(host)
    {
        _converter = Resolve<IPostmanConverter>(true);
    }

    [Test]
    public async Task LinkedIn_collection_preserves_every_source_request_and_describes_its_limitations(CancellationToken cancellationToken)
    {
        string source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linkedin-campaign-management.postman.json"), cancellationToken);
        JsonObject document = await Convert(source, cancellationToken);
        JsonObject[] operations = Operations(document).ToArray();
        JsonObject[] variants = operations.SelectMany(op => ((JsonArray)op["x-postman-variants"]!).OfType<JsonObject>()).ToArray();
        variants.Length.Should().Be(72);
        foreach (JsonObject item in Requests((JsonArray)JsonNode.Parse(source)!["item"]!))
            variants.Should().Contain(variant => JsonNode.DeepEquals(variant["request"], item["request"]) && String(variant["name"]) == String(item["name"]));
        operations.Should().OnlyContain(op => ((JsonObject)op["responses"]!).ContainsKey("default"));
        operations.Should().OnlyContain(op => !((JsonObject)op["responses"]!).ContainsKey("200"));
        operations.Should().OnlyContain(op => op["tags"] is JsonArray);
        operations.SelectMany(op => Parameters(op)).Should().NotContain(p => String(p["name"]) == "Authorization");
        ((JsonObject)document["components"]!["securitySchemes"]!).Should().Contain(pair => String(pair.Value!["type"]) == "oauth2");
        ((JsonArray)document["x-postman-warnings"]!).Count.Should().BeGreaterThan(0);
        foreach (var path in (JsonObject)document["paths"]!)
        {
            string[] names = Regex.Matches(path.Key, @"\{([^{}]+)\}").Select(match => match.Groups[1].Value).Distinct().ToArray();
            foreach (JsonObject op in ((JsonObject)path.Value!).Select(pair => pair.Value).OfType<JsonObject>())
                Parameters(op).Where(p => String(p["in"]) == "path").Select(p => String(p["name"])).Should().BeEquivalentTo(names);
        }
        JsonObject compound = operations.First(op => String(op["summary"]) == "Create Ad Account User");
        Parameters(compound).Where(p => String(p["in"]) == "path").Select(p => String(p["name"])).Should().BeEquivalentTo("sponsoredaccount_id", "person_id");
        operations.Should().Contain(op => Parameters(op).Any(p => String(p["name"]) == "q"));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "campaign-management.openapi.json"), document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    [Test]
    public async Task Raw_urls_preserve_encoded_compound_paths_ports_query_values_and_repeated_parameters(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Compound","request":{"url":"https://api.example.com:8443/v1/(account:urn%3Ali%3A{{account}},user:{{user}})?q=search&ids=001&ids=002&filter=a%2Bb+c#ignored"}}
            """), cancellationToken);
        JsonObject operation = Operation(document, "/v1/(account:urn%3Ali%3A{account},user:{user})");
        String(operation["servers"]![0]!["url"]).Should().Be("https://api.example.com:8443");
        JsonObject ids = Parameter(operation, "ids");
        String(ids["schema"]!["type"]).Should().Be("array");
        (ids["explode"]?.GetValue<bool>() ?? true).Should().BeTrue();
        ids["examples"]!["example1"]!["value"]!.ToJsonString().Should().Be("[\"001\",\"002\"]");
        String(Parameter(operation, "filter")["examples"]!["example1"]!["value"]).Should().Be("a+b c");
    }

    [Test]
    public async Task Structured_urls_and_scoped_variables_preserve_server_prefixes_and_path_descriptions(CancellationToken cancellationToken)
    {
        const string source = """
            {"info":{"name":"Scoped","description":{"content":"Collection docs"},"version":{"major":2,"minor":3,"patch":4}},
             "variable":[{"key":"baseUrl","value":"https://wrong.example.com"}],
             "item":[{"name":"Folder","description":{"content":"Folder docs"},"variable":[{"key":"id","value":"folder-id"}],"item":[
               {"name":"Fetch","request":{"url":{"raw":"{{baseUrl}}/things/:id?off=1","host":["{{baseUrl}}"],"path":["things",":id"],"variable":[{"key":"id","value":"007","description":{"content":"Identifier"}}],"query":[{"key":"off","value":"1","disabled":true}]}}}
             ]}]}
            """;
        JsonObject document = await Convert(source, cancellationToken, new PostmanConversionOptions
        {
            Variables = new Dictionary<string, string?> { ["baseUrl"] = "{{origin}}/rest", ["origin"] = "https://api.example.com" }
        });
        JsonObject operation = Operation(document, "/things/{id}");
        String(operation["servers"]![0]!["url"]).Should().Be("https://api.example.com/rest");
        String(Parameter(operation, "id")["description"]).Should().Be("Identifier");
        String(Parameter(operation, "id")["examples"]!["example1"]!["value"]).Should().Be("007");
        Parameters(operation).Should().NotContain(p => String(p["name"]) == "off");
        String(document["info"]!["version"]).Should().Be("2.3.4");
        String(document["info"]!["description"]).Should().Be("Collection docs");
        String(document["tags"]![0]!["description"]).Should().Be("Folder docs");
    }

    [Test]
    public async Task Json_schemas_preserve_strings_property_names_nulls_and_all_array_elements_without_inventing_required_fields(CancellationToken cancellationToken)
    {
        const string body = """{"code":"001","truth":"true","integer":4,"decimal":1.25,"flag":false," odd\\\" ":"value","nullOnly":null,"items":[{"a":1,"optional":null},{"b":"2","optional":"yes"}],"mixed":[1,"two",false,null],"empty":[]}""";
        JsonObject document = await Convert(Collection(RequestWithBody("raw", JsonValue.Create(body))), cancellationToken);
        JsonObject media = Media(Operation(document, "/test", "post"));
        JsonNode schema = media["schema"]!;
        foreach (string field in new[] { "code", "truth" }) String(schema["properties"]![field]!["type"]).Should().Be("string");
        String(schema["properties"]!["integer"]!["type"]).Should().Be("integer");
        String(schema["properties"]!["flag"]!["type"]).Should().Be("boolean");
        String(schema["properties"]!["decimal"]!["type"]).Should().Be("number");
        ((JsonObject)schema["properties"]!).Select(pair => pair.Key).Should().BeEquivalentTo(((JsonObject)JsonNode.Parse(body)!).Select(pair => pair.Key));
        schema["required"].Should().BeNull();
        JsonNode items = schema["properties"]!["items"]!["items"]!;
        ((JsonObject)items["properties"]!).Select(pair => pair.Key).Should().BeEquivalentTo("a", "b", "optional");
        items["properties"]!["optional"]!["nullable"]!.GetValue<bool>().Should().BeTrue();
        ((JsonArray)schema["properties"]!["mixed"]!["items"]!["anyOf"]!).Count.Should().Be(3);
        JsonNode.DeepEquals(media["examples"]!["example1"]!["value"], JsonNode.Parse(body)).Should().BeTrue();
        Operation(document, "/test", "post")["requestBody"]!["required"]?.GetValue<bool>().Should().NotBe(true);
    }

    [Test]
    public async Task Duplicate_operations_merge_request_variants_responses_servers_and_examples(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Create","request":{"method":"POST","url":"https://one.example.com/items?action=create","body":{"mode":"raw","raw":"{\"name\":\"first\"}"}},"response":[{"code":201,"name":"Created","body":"{\"id\":1}"}]},
            {"name":"Batch","request":{"method":"POST","url":"https://two.example.com/items?action=batch","body":{"mode":"raw","raw":"[{\"name\":\"second\"}]"}},"response":[{"code":202,"name":"Accepted","body":"{\"job\":\"x\"}"}]}
            """), cancellationToken);
        JsonObject operation = Operation(document, "/items", "post");
        ((JsonArray)operation["x-postman-variants"]!).Count.Should().Be(2);
        ((JsonArray)operation["servers"]!).Count.Should().Be(2);
        ((JsonObject)operation["responses"]!).Select(pair => pair.Key).Should().BeEquivalentTo("201", "202");
        ((JsonObject)Parameter(operation, "action")["examples"]!).Count.Should().Be(2);
        ((JsonArray)Media(operation)["schema"]!["anyOf"]!).Count.Should().Be(2);
        ((JsonObject)Media(operation)["examples"]!).Count.Should().Be(2);
    }

    [Test]
    public async Task Same_status_responses_merge_media_types_shapes_headers_and_named_examples(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Read","request":{"url":"https://example.com/test","header":[{"key":"Accept","value":"application/xml"}]},"response":[
             {"code":200,"name":"First","header":[{"key":"Content-Type","value":"application/problem+json; charset=utf-8"},{"key":"X-Count","value":"1"}],"body":"{\"id\":1}"},
             {"code":200,"name":"Second","header":[{"key":"Content-Type","value":"application/problem+json"},{"key":"x-count","value":"2"}],"body":"{\"name\":\"two\"}"},
             {"code":200,"name":"Plain","header":[{"key":"Content-Type","value":"text/plain"}],"body":"OK"},
             {"code":204,"status":"No Content","body":"incorrect body"}
            ]}
            """), cancellationToken);
        JsonNode responses = Operation(document, "/test")["responses"]!;
        JsonNode media = responses["200"]!["content"]!["application/problem+json"]!;
        ((JsonObject)media["examples"]!).Count.Should().Be(2);
        ((JsonObject)media["schema"]!["properties"]!).Select(pair => pair.Key).Should().BeEquivalentTo("id", "name");
        ((JsonObject)responses["200"]!["headers"]!["X-Count"]!["examples"]!).Count.Should().Be(2);
        responses["200"]!["content"]!["text/plain"].Should().NotBeNull();
        responses["204"]!["content"].Should().BeNull();
    }

    [Test]
    public async Task Multipart_forms_preserve_files_repeated_fields_encoding_and_disabled_entries(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Upload","request":{"method":"POST","url":"https://example.com/test","body":{"mode":"formdata","formdata":[
             {"key":"file","type":"file","src":["a.pdf","b.pdf"]},
             {"key":"tag","value":"001","contentType":"text/plain"},{"key":"tag","value":"002"},
             {"key":"disabled","value":"no","disabled":true}]}}}
            """), cancellationToken);
        JsonObject media = Media(Operation(document, "/test", "post"), "multipart/form-data");
        String(media["schema"]!["properties"]!["file"]!["items"]!["format"]).Should().Be("binary");
        String(media["schema"]!["properties"]!["tag"]!["type"]).Should().Be("array");
        media["schema"]!["properties"]!["disabled"].Should().BeNull();
        String(media["encoding"]!["tag"]!["contentType"]).Should().Be("text/plain");
        media["examples"]!["example1"]!["value"]!["tag"]!.ToJsonString().Should().Be("[\"001\",\"002\"]");
    }

    [Test]
    public async Task Urlencoded_binary_graphql_and_raw_text_bodies_have_correct_media_types(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Form","request":{"method":"POST","url":"https://example.com/form","body":{"mode":"urlencoded","urlencoded":[{"key":"code","value":"001"}]}}},
            {"name":"Binary","request":{"method":"PUT","url":"https://example.com/binary","body":{"mode":"file","file":{"src":"local.bin"}}}},
            {"name":"Graph","request":{"method":"POST","url":"https://example.com/graphql","body":{"mode":"graphql","graphql":{"query":"query ($id: ID!) { node(id:$id) { id } }","variables":"{\"id\":\"001\"}"}}}},
            {"name":"Text","request":{"method":"POST","url":"https://example.com/text","body":{"mode":"raw","raw":"hello","options":{"raw":{"language":"text"}}}}}
            """), cancellationToken);
        String(Media(Operation(document, "/form", "post"), "application/x-www-form-urlencoded")["schema"]!["properties"]!["code"]!["type"]).Should().Be("string");
        String(Media(Operation(document, "/binary", "put"), "application/octet-stream")["schema"]!["format"]).Should().Be("binary");
        String(Media(Operation(document, "/graphql", "post"))["examples"]!["example1"]!["value"]!["variables"]!["id"]).Should().Be("001");
        String(Media(Operation(document, "/text", "post"), "text/plain")["examples"]!["example1"]!["value"]).Should().Be("hello");
    }

    [Test]
    public async Task Authentication_inherits_overrides_and_registers_distinct_query_and_header_keys(CancellationToken cancellationToken)
    {
        const string source = """
            {"info":{"name":"Auth"},"auth":{"type":"bearer","bearer":[{"key":"token","value":"{{token}}"}]},"item":[
             {"name":"Inherited","request":{"url":"https://example.com/inherited"}},
             {"name":"Public","request":{"url":"https://example.com/public","auth":{"type":"noauth"},"header":[{"key":"Authorization","value":"Bearer stale","disabled":true}]}},
             {"name":"Query","auth":{"type":"apikey","apikey":[{"key":"key","value":"api_key"},{"key":"in","value":"query"}]},"item":[{"name":"Fetch","request":{"url":"https://example.com/query?api_key=abc"}}]},
             {"name":"Header","request":{"url":"https://example.com/header","auth":{"type":"apikey","apikey":{"key":"X-Key","in":"header"}}}},
             {"name":"Basic","request":{"url":"https://example.com/basic","auth":{"type":"basic","basic":[]}}}
            ]}
            """;
        JsonObject document = await Convert(source, cancellationToken);
        JsonObject schemes = (JsonObject)document["components"]!["securitySchemes"]!;
        schemes.Count.Should().Be(4);
        String(schemes["bearerAuth"]!["scheme"]).Should().Be("bearer");
        schemes.Should().Contain(pair => String(pair.Value!["name"]) == "api_key" && String(pair.Value["in"]) == "query");
        schemes.Should().Contain(pair => String(pair.Value!["name"]) == "X-Key" && String(pair.Value["in"]) == "header");
        ((JsonObject)Operation(document, "/public")["security"]![0]!).Count.Should().Be(0);
        Parameters(Operation(document, "/query")).Should().NotContain(p => String(p["name"]) == "api_key");
        foreach (JsonObject op in Operations(document))
            foreach (JsonObject requirement in ((JsonArray)op["security"]!).OfType<JsonObject>())
                foreach (var pair in requirement) schemes.ContainsKey(pair.Key).Should().BeTrue();
    }

    [Test]
    public async Task OAuth_v21_maps_complete_flow_and_scopes_without_emitting_credentials(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"OAuth","request":{"url":"https://example.com/test","auth":{"type":"oauth2","oauth2":[
             {"key":"grant_type","value":"client_credentials"},{"key":"accessTokenUrl","value":"https://identity.example.com/token"},
             {"key":"scope","value":"read write"},{"key":"clientSecret","value":"secret"}]}}}
            """), cancellationToken);
        JsonNode flow = document["components"]!["securitySchemes"]!["oauth2Auth"]!["flows"]!["clientCredentials"]!;
        String(flow["tokenUrl"]).Should().Be("https://identity.example.com/token");
        ((JsonObject)flow["scopes"]!).Select(pair => pair.Key).Should().BeEquivalentTo("read", "write");
        flow.ToJsonString().Should().NotContain("secret");
    }

    [Test]
    public async Task Unquoted_json_variables_keep_known_structure_and_do_not_create_invalid_examples(CancellationToken cancellationToken)
    {
        string body = """{"count":{{count}},"code":"{{code}}","enabled":true}""";
        JsonObject document = await Convert(Collection(RequestWithBody("raw", JsonValue.Create(body))), cancellationToken);
        JsonObject operation = Operation(document, "/test", "post");
        JsonObject media = Media(operation);
        String(media["schema"]!["type"]).Should().Be("object");
        media["schema"]!["properties"]!["count"]!["type"].Should().BeNull();
        String(media["schema"]!["properties"]!["code"]!["type"]).Should().Be("string");
        String(media["x-postman-raw-body"]).Should().Be(body);
        media["examples"].Should().BeNull();
    }

    [Test]
    public async Task Equivalent_path_templates_merge_without_duplicate_paths(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"First","request":{"url":"https://example.com/things/{{id}}"}},
            {"name":"Second","request":{"url":"https://example.com/things/:thingId"}}
            """), cancellationToken);
        ((JsonObject)document["paths"]!).Count.Should().Be(1);
        JsonObject operation = Operation(document, "/things/{id}");
        Parameters(operation).Select(p => String(p["name"])).Should().BeEquivalentTo("id");
        ((JsonArray)operation["x-postman-variants"]!).Count.Should().Be(2);
    }

    [Test]
    public async Task Missing_servers_and_cyclic_variables_remain_explicit_and_strict_mode_rejects_ambiguity(CancellationToken cancellationToken)
    {
        string source = Collection("""{"name":"Unknown","request":{"url":"{{baseUrl}}/test"}}""");
        JsonObject document = await Convert(source, cancellationToken, new PostmanConversionOptions
        {
            Variables = new Dictionary<string, string?> { ["baseUrl"] = "{{other}}", ["other"] = "{{baseUrl}}" }
        });
        JsonNode server = Operation(document, "/test")["servers"]![0]!;
        String(server["url"]).Should().Be("{baseUrl}");
        server["variables"]!["baseUrl"]!["default"].Should().NotBeNull();
        Func<Task> strict = () => _converter.Convert(source, new PostmanConversionOptions { FailOnWarnings = true }, cancellationToken).AsTask();
        await strict.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unresolved*");
    }

    [Test]
    public async Task String_requests_wrappers_disabled_headers_and_cookies_are_supported(CancellationToken cancellationToken)
    {
        string source = "{\"collection\":" + Collection("""
            {"name":"String","request":"https://example.com/string?q=001"},
            {"name":"Headers","request":{"url":"https://example.com/headers","header":[
             {"key":"x-off","value":"no","disabled":true},{"key":"X-Code","value":"001"},
             {"key":"Cookie","value":"session=abc; mode=dark"}]}}
            """) + "}";
        JsonObject document = await Convert(source, cancellationToken);
        String(Parameter(Operation(document, "/string"), "q")["schema"]!["type"]).Should().Be("string");
        JsonObject operation = Operation(document, "/headers");
        Parameters(operation).Should().NotContain(p => String(p["name"]) == "x-off");
        String(Parameter(operation, "session")["in"]).Should().Be("cookie");
        String(Parameter(operation, "X-Code")["schema"]!["type"]).Should().Be("string");
    }

    [Test]
    public async Task Cancellation_and_malformed_request_structures_fail_explicitly(CancellationToken cancellationToken)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> conversion = () => _converter.Convert(Collection("""{"request":"https://example.com"}"""), cancelled.Token).AsTask();
        await conversion.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> malformed = () => _converter.Convert(Collection("""{"name":"Bad"}"""), cancellationToken).AsTask();
        await malformed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing a request or item array*");
    }

    [Test]
    public async Task Content_apis_file_conversion_preserves_unmapped_request_and_converts_all_other_requests(CancellationToken cancellationToken)
    {
        string input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "linkedin-content-apis.postman.json");
        string output = Path.GetTempFileName();
        try
        {
            // Exercise the same default file entry point used by the LinkedIn runner.
            await _converter.SaveOpenApiFile(input, output, cancellationToken);
            string json = await File.ReadAllTextAsync(output, cancellationToken);
            OpenApiDocument.Parse(json, "json").Diagnostic!.Errors.Should().BeEmpty();
            var document = (JsonObject)JsonNode.Parse(json)!;
            JsonObject[] sources = Requests((JsonArray)JsonNode.Parse(await File.ReadAllTextAsync(input, cancellationToken))!["item"]!).ToArray();
            sources.Length.Should().Be(55);
            JsonObject[] mapped = Operations(document).SelectMany(op => ((JsonArray)op["x-postman-variants"]!).OfType<JsonObject>()).ToArray();
            mapped.Length.Should().Be(54);
            var unmapped = (JsonArray)document["x-postman-unmapped-requests"]!;
            unmapped.Count.Should().Be(1);
            String(unmapped[0]!["reason"]).Should().Be("missing-url");
            JsonObject missing = sources.Single(item => String(item["name"]) == "Get document content");
            JsonNode.DeepEquals(unmapped[0]!["item"], missing).Should().BeTrue();
            ((JsonArray)unmapped[0]!["folders"]!).Count.Should().BeGreaterThan(0);
            mapped.Should().NotContain(variant => String(variant["name"]) == "Get document content");
            foreach (JsonObject item in sources.Where(item => !ReferenceEquals(item, missing)))
                mapped.Should().Contain(variant => JsonNode.DeepEquals(variant["request"], item["request"]) && String(variant["id"]) == String(item["id"]));
            ((JsonArray)document["x-postman-warnings"]!).Should().Contain(warning => String(warning)!.Contains("Get document content: Request is missing a URL"));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Test]
    public async Task Empty_urls_are_preserved_without_inventing_endpoints_or_consuming_operation_ids(CancellationToken cancellationToken)
    {
        foreach (string url in new[] { "null", "\"\"", "\"  \"", "{}", "{\"raw\":\"\"}", "{\"host\":[]}", "{\"path\":[]}", "{\"query\":[{\"key\":\"q\",\"value\":\"x\"}]}" })
        {
            var missing = new JsonObject { ["name"] = "Read", ["request"] = new JsonObject { ["method"] = "GET", ["url"] = JsonNode.Parse(url) } };
            JsonObject document = await Convert(Collection(missing.ToJsonString() + "," + """{"name":"Read","request":{"url":"https://example.com/valid"}}"""), cancellationToken);
            ((JsonObject)document["paths"]!).Select(pair => pair.Key).Should().BeEquivalentTo("/valid");
            String(Operation(document, "/valid")["operationId"]).Should().Be("Read");
            JsonNode.DeepEquals(document["x-postman-unmapped-requests"]![0]!["item"], missing).Should().BeTrue();
        }
    }

    [Test]
    public async Task Missing_urls_still_fail_in_strict_mode(CancellationToken cancellationToken)
    {
        Func<Task> conversion = () => _converter.Convert(Collection("""{"name":"Incomplete","request":{"method":"GET"}}"""),
            new PostmanConversionOptions { FailOnWarnings = true }, cancellationToken).AsTask();
        await conversion.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Incomplete*missing a URL*");
    }

    [Test]
    public async Task Explicit_root_paths_remain_valid_and_have_no_unmapped_extension(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Root","request":{"url":{"path":"/"}}},
            {"name":"Host","request":{"method":"POST","url":{"protocol":"https","host":["example","com"],"path":[]}}}
            """), cancellationToken);
        Operation(document, "/").Should().NotBeNull();
        Operation(document, "/", "post").Should().NotBeNull();
        document["x-postman-unmapped-requests"].Should().BeNull();
    }

    [Test]
    public async Task Partial_structured_urls_keep_raw_paths_and_variable_names_are_case_sensitive(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Partial","request":{"url":{"raw":"https://example.com/actual?q=1","host":["example","com"],"protocol":"https"}}},
            {"name":"Upper","request":{"url":"{{BaseUrl}}/upper"}},
            {"name":"Lower","request":{"url":"{{baseUrl}}/lower"}}
            """), cancellationToken, new PostmanConversionOptions
        {
            Variables = new Dictionary<string, string?> { ["BaseUrl"] = "https://upper.example.com", ["baseUrl"] = "https://lower.example.com" }
        });
        Operation(document, "/actual").Should().NotBeNull();
        String(Operation(document, "/upper")["servers"]![0]!["url"]).Should().Be("https://upper.example.com");
        String(Operation(document, "/lower")["servers"]![0]!["url"]).Should().Be("https://lower.example.com");
    }

    [Test]
    public async Task Unknown_template_values_remain_unconstrained_when_merged_with_concrete_examples(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Template","request":{"method":"POST","url":"https://example.com/test","body":{"mode":"raw","raw":"{\"value\":{{value}}}"}}},
            {"name":"Concrete","request":{"method":"POST","url":"https://example.com/test","body":{"mode":"raw","raw":"{\"value\":42}"}}}
            """), cancellationToken);
        JsonObject media = Media(Operation(document, "/test", "post"));
        media["schema"]!["properties"]!["value"]!["type"].Should().BeNull();
        media["schema"]!["properties"]!["value"]!["x-postman-unresolved-value"]!.GetValue<bool>().Should().BeTrue();
        ((JsonObject)media["examples"]!).Count.Should().Be(1);
    }

    [Test]
    public async Task Invalid_json_and_unsupported_features_are_diagnosed_without_fabricating_contracts(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Invalid","request":{"method":"POST","url":"https://example.com/invalid","header":[{"key":"Content-Type","value":"application/json"}],"body":{"mode":"raw","raw":"{ invalid json"}}},
            {"name":"Unsupported","request":{"method":"POST","url":"https://example.com/unsupported","auth":{"type":"awsv4","awsv4":[]},"body":{"mode":"future","future":"data"}}}
            """), cancellationToken);
        JsonObject media = Media(Operation(document, "/invalid", "post"));
        media["schema"]!["type"].Should().BeNull();
        String(media["x-postman-raw-body"]).Should().Be("{ invalid json");
        JsonObject unsupported = Operation(document, "/unsupported", "post");
        String(unsupported["x-postman-auth"]!["type"]).Should().Be("awsv4");
        ((JsonArray)unsupported["x-postman-warnings"]!).Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Disabled_content_type_headers_do_not_override_body_language_and_raw_header_strings_work(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Body","request":{"method":"POST","url":"https://example.com/test","header":[{"key":"Content-Type","value":"text/plain","disabled":true}],"body":{"mode":"raw","raw":"{\"code\":\"01\"}","options":{"raw":{"language":"json"}}}}},
            {"name":"Headers","request":{"url":"https://example.com/headers","header":"X-Code: 001\r\nAuthorization: Basic abc\r\n"}}
            """), cancellationToken);
        Media(Operation(document, "/test", "post")).Should().NotBeNull();
        JsonObject headers = Operation(document, "/headers");
        String(Parameter(headers, "X-Code")["examples"]!["example1"]!["value"]).Should().Be("001");
        headers["security"]![0]!["basicAuth"].Should().NotBeNull();
    }

    [Test]
    public async Task Strict_mode_accepts_a_self_contained_request_with_a_saved_response(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Known","request":{"url":"https://example.com/known"},"response":[{"code":200,"status":"OK","body":"{\"ok\":true}"}]}
            """), cancellationToken, new PostmanConversionOptions { FailOnWarnings = true });
        ((JsonArray)document["x-postman-warnings"]!).Count.Should().Be(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Angle_bracket_placeholders_become_required_path_parameters(bool structured, CancellationToken cancellationToken)
    {
        const string raw = "{{baseUrl}}/leadnotifications/<webhook id>";
        JsonNode url = structured ? new JsonObject
        {
            ["raw"] = raw, ["host"] = new JsonArray("{{baseUrl}}"),
            ["path"] = new JsonArray("leadnotifications", "<webhook id>"),
            ["variable"] = new JsonArray(new JsonObject { ["key"] = "webhook id", ["value"] = "007", ["description"] = "Webhook identifier" })
        } : JsonValue.Create(raw)!;
        var request = new JsonObject { ["method"] = "DELETE", ["url"] = url };
        JsonObject document = await Convert(Collection(new JsonObject { ["name"] = "Delete webhook", ["request"] = request }.ToJsonString()),
            cancellationToken, new PostmanConversionOptions { Variables = new Dictionary<string, string?> { ["baseUrl"] = "https://api.linkedin.com/rest" } });
        JsonObject operation = Operation(document, "/leadnotifications/{webhook id}", "delete");
        JsonObject parameter = Parameter(operation, "webhook id");
        String(parameter["in"]).Should().Be("path");
        parameter["required"]!.GetValue<bool>().Should().BeTrue();
        String(parameter["schema"]!["type"]).Should().Be("string");
        if (structured)
        {
            String(parameter["description"]).Should().Be("Webhook identifier");
            String(parameter["examples"]!["example1"]!["value"]).Should().Be("007");
        }
        JsonNode.DeepEquals(operation["x-postman-variants"]![0]!["request"], request).Should().BeTrue();
        ((JsonArray)operation["x-postman-warnings"]!).Should().Contain(warning => String(warning)!.Contains("Angle-bracket placeholder"));
    }

    [Test]
    public async Task Angle_bracket_conversion_preserves_encoded_literals_existing_templates_and_query_values(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Mixed","request":{"url":"https://example.com/%3Cencoded%20literal%3E/{existing<name>}/(id:<first>,other:<second>)?q=<query>"}}
            """), cancellationToken);
        JsonObject operation = Operation(document, "/%3Cencoded%20literal%3E/{existing<name>}/(id:{first},other:{second})");
        Parameters(operation).Where(p => String(p["in"]) == "path").Select(p => String(p["name"]))
            .Should().BeEquivalentTo("existing<name>", "first", "second");
        String(Parameter(operation, "q")["examples"]!["example1"]!["value"]).Should().Be("<query>");
    }

    [Test]
    public async Task Strict_mode_reports_inferred_angle_bracket_parameters(CancellationToken cancellationToken)
    {
        Func<Task> conversion = () => _converter.Convert(Collection("""
            {"name":"Delete webhook","request":{"url":"https://example.com/leadnotifications/<webhook id>"}}
            """), new PostmanConversionOptions { FailOnWarnings = true }, cancellationToken).AsTask();
        await conversion.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Angle-bracket placeholder*webhook id*");
    }

    [Test]
    public async Task Response_status_evidence_precedes_body_inference_and_errors_remain_errors(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"name":"Identity","request":{"url":"https://example.com/identity"},"response":[{"name":"identityMe-200","body":"{\"lastRefreshedAt\":1760631246905}"}]},
            {"name":"Created","request":{"url":"https://example.com/created"},"response":[{"status":"Created","body":"{\"id\":1}"}]},
            {"name":"Explicit error","request":{"url":"https://example.com/error"},"response":[{"code":403,"name":"200 success","body":"{\"id\":1}"}]},
            {"name":"Unknown error","request":{"url":"https://example.com/unknown-error"},"response":[{"body":"{\"message\":\"Denied\",\"status\":403}"}]},
            {"name":"Unclassified","request":{"url":"https://example.com/inferred"},"response":[{"body":"{\"value\":{\"id\":1}}"}]}
            """), cancellationToken);
        String(Operation(document, "/identity")["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["lastRefreshedAt"]!["format"]).Should().Be("int64");
        Operation(document, "/created")["responses"]!["201"].Should().NotBeNull();
        ((JsonObject)Operation(document, "/error")["responses"]!).Select(pair => pair.Key).Should().BeEquivalentTo("403");
        ((JsonObject)Operation(document, "/unknown-error")["responses"]!).Select(pair => pair.Key).Should().BeEquivalentTo("default");
        JsonNode inferred = Operation(document, "/inferred")["responses"]!;
        inferred["default"].Should().NotBeNull();
        String(inferred["2XX"]!["x-postman-response-inference"]).Should().Be("unclassified-json-body");
    }

    [Test]
    public async Task Unknown_success_inference_can_be_disabled(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"request":{"url":"https://example.com/value"},"response":[{"body":"{\"value\":1}"}]}
            """), cancellationToken, new PostmanConversionOptions { InferSuccessResponsesFromBodies = false });
        ((JsonObject)Operation(document, "/value")["responses"]!).Select(pair => pair.Key).Should().BeEquivalentTo("default");
    }

    [Test]
    public async Task Documented_status_attaches_to_unclassified_bodies_but_does_not_promote_errors(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"request":{"url":"https://example.com/created","description":"A successful response returns 201 Created."},"response":[
              {"name":"Result","body":"{\"id\":1}"},
              {"name":"Error","body":"{\"error\":\"invalid\"}"}
            ]}
            """), cancellationToken, new PostmanConversionOptions { InferSuccessResponsesFromBodies = false });
        JsonNode responses = Operation(document, "/created")["responses"]!;
        responses["201"]!["content"]!["application/json"]!["schema"]!["properties"]!["id"].Should().NotBeNull();
        responses["default"]!["content"]!["application/json"]!["schema"]!["properties"]!["error"].Should().NotBeNull();
        responses["2XX"].Should().BeNull();
    }

    [Test]
    public async Task Documented_response_examples_are_recovered_without_using_request_examples(CancellationToken cancellationToken)
    {
        JsonObject document = await Convert(Collection("""
            {"request":{"url":"https://example.com/documented","description":"### Sample Request\n```json\n{\"requestOnly\":true}\n```\n### Sample Response 200\n```json\n{\"elements\":[{\"at\":1648512200000},],}\n```\n### Other\n```json\n{\"unrelated\":true}\n```"}},
            {"request":{"url":"https://example.com/deleted","description":"A successful response returns 204 No Content."}}
            """), cancellationToken);
        JsonNode schema = Operation(document, "/documented")["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        schema["properties"]!["requestOnly"].Should().BeNull();
        schema["properties"]!["unrelated"].Should().BeNull();
        String(schema["properties"]!["elements"]!["items"]!["properties"]!["at"]!["format"]).Should().Be("int64");
        Operation(document, "/deleted")["responses"]!["204"].Should().NotBeNull();
        Operation(document, "/deleted")["responses"]!["204"]!["content"].Should().BeNull();
    }

    private async Task<JsonObject> Convert(string source, CancellationToken cancellationToken, PostmanConversionOptions? options = null)
    {
        OpenApiDocument document = await _converter.Convert(source, options ?? new PostmanConversionOptions(), cancellationToken);
        string json = _converter.ToJson(document);
        var parsed = OpenApiDocument.Parse(json, "json");
        parsed.Diagnostic!.Errors.Should().BeEmpty();
        if (Environment.GetEnvironmentVariable("POSTMAN_CONVERTER_VALIDATION_DIR") is { Length: > 0 } outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            string hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, hash + ".json"), json, cancellationToken);
        }
        return (JsonObject)JsonNode.Parse(json)!;
    }

    private static string Collection(string items) => "{\"info\":{\"name\":\"Example\"},\"item\":[" + items + "]}";
    private static string RequestWithBody(string mode, JsonNode? value) => new JsonObject
    {
        ["name"] = "Body", ["request"] = new JsonObject
        {
            ["method"] = "POST", ["url"] = "https://example.com/test",
            ["body"] = new JsonObject { ["mode"] = mode, [mode] = value, ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = "json" } } }
        }
    }.ToJsonString();
    private static JsonObject Operation(JsonObject document, string path, string method = "get") => (JsonObject)document["paths"]![path]![method]!;
    private static JsonObject Media(JsonObject operation, string contentType = "application/json") => (JsonObject)operation["requestBody"]!["content"]![contentType]!;
    private static string? String(JsonNode? node) => node?.ToString();
    private static IEnumerable<JsonObject> Parameters(JsonObject operation) => (operation["parameters"] as JsonArray)?.OfType<JsonObject>() ?? [];
    private static JsonObject Parameter(JsonObject operation, string name) => Parameters(operation).Single(p => String(p["name"]) == name);
    private static IEnumerable<JsonObject> Operations(JsonObject document) => ((JsonObject)document["paths"]!).SelectMany(path => ((JsonObject)path.Value!).Select(pair => pair.Value).OfType<JsonObject>());
    private static IEnumerable<JsonObject> Requests(JsonArray items) => items.OfType<JsonObject>().SelectMany(item => item["item"] is JsonArray children ? Requests(children) : new[] { item });
}
