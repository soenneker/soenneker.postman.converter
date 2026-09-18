using System.Collections.Generic;

namespace Soenneker.Postman.Converter.Options;

/// <summary>Controls conversion of information that is not self-contained in a collection.</summary>
public sealed class PostmanConversionOptions
{
    /// <summary>
    /// Environment or runtime variable values, with case-sensitive names. These override collection and folder variables.
    /// URL prefixes are resolved into servers; path placeholders remain parameters. Values are not substituted into body examples.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Variables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Throws when conversion encounters an ambiguity or unsupported feature recorded in x-postman-warnings,
    /// including a request with no URL. Otherwise URL-less source items are preserved in x-postman-unmapped-requests.
    /// </summary>
    public bool FailOnWarnings { get; init; }

    /// <summary>
    /// Treats an unclassified saved JSON response body as an inferred 2XX response when no error indicators are present.
    /// The original default response is retained, and a warning records the assumption. Disable to require status evidence.
    /// Explicit status codes, response names, and documented status information take precedence.
    /// </summary>
    public bool InferSuccessResponsesFromBodies { get; init; } = true;
}
