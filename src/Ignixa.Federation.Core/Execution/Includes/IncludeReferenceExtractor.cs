using System.Text;
using System.Text.Json.Nodes;
using Ignixa.Abstractions;
using Ignixa.FhirPath.Evaluation;
using Ignixa.FhirPath.Expressions;
using Ignixa.FhirPath.Parser;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Specification.Generated;

namespace Ignixa.Federation.Core.Execution.Includes;

/// <summary>
/// Reads the reference literals a forward include follows from one match (design spec §7.6): the include parameter's
/// packaged R4 expression, evaluated over the match's own JSON, and the <c>Reference.reference</c> primitive of each
/// result. Classifying and routing a literal is the caller's job.
/// </summary>
/// <remarks>
/// References resolve to type-only stubs without a network call, so an expression's <c>resolve() is X</c> filter works.
/// A result with no <c>reference</c> property (identifier-only or display-only) yields nothing. A caller parses each hop's
/// expression once with <see cref="ParseExpression"/> and reuses it for every match it reads with that hop.
/// </remarks>
internal static class IncludeReferenceExtractor
{
    private static readonly Lazy<R4CoreSchemaProvider> Schema = new(() => new R4CoreSchemaProvider());

    // Strict, so a shard's invalid UTF-8 is a contract violation rather than silently replaced characters.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Parses the FHIRPath expression <paramref name="hop"/> evaluates.</summary>
    /// <param name="hop">The include's hop, whose expression is the parameter's packaged R4 expression.</param>
    /// <returns>The parsed expression, for <see cref="Extract"/>.</returns>
    public static Expression ParseExpression(ReferenceHop hop) => new FhirPathParser(false).Parse(hop.Expression);

    /// <summary>Gets the reference literals <paramref name="expression"/> reaches from <paramref name="match"/>, in expression order.</summary>
    /// <param name="match">A match whose type is the source type of the hop <paramref name="expression"/> was parsed from.</param>
    /// <param name="expression">The hop's expression, from <see cref="ParseExpression"/>.</param>
    /// <returns>
    /// The literals, verbatim from the JSON (not trimmed); duplicates are kept. A literal the reference resolver cannot read
    /// is not of the type a <c>resolve() is X</c> filter asks for.
    /// </returns>
    /// <exception cref="ShardFailureException">
    /// The match's body is not a valid resource of its key's type, a contract violation of its shard: invalid UTF-8,
    /// invalid JSON, any failure reading the body while the expression is evaluated (such as a duplicate property at any
    /// depth or conflicting <c>_</c>-prefixed properties), a resource of another type than its key, or a
    /// <c>Reference.reference</c> that is not a single JSON string.
    /// </exception>
    public static IReadOnlyList<string> Extract(ShardEntry match, Expression expression)
    {
        try
        {
            return Read(match, expression);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Ignixa parses lazily: the root parse reads little more than resourceType, and the rest of the body is read
            // while the expression is evaluated and the results are read, reporting bad shapes with several exception
            // types. The body is the shard's data, so every failure from parse to the last literal is its contract violation.
            throw new ShardFailureException(match.Key.Shard, ShardFailureReason.ContractViolation, exception);
        }
    }

    private static List<string> Read(ShardEntry match, Expression expression)
    {
        R4CoreSchemaProvider schema = Schema.Value;
        IElement resource = ResourceJsonNode.Parse(StrictUtf8.GetString(match.Resource.Span)).ToElement(schema);

        // The key came from the bundle entry; a body of another type would silently match no expression.
        if (resource.InstanceType != match.Key.Type.Value)
        {
            throw new InvalidDataException("The body's resourceType is not its key's type.");
        }

        var resolver = new LightweightReferenceToElementResolver(
            new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance),
            schema);
        EvaluationContext context = new FhirEvaluationContext()
            .WithElementResolver(literal => TryResolve(resolver, literal))
            .WithSchema(schema)
            .WithResource(resource)
            .WithRootResource(resource);

        // Enumerated here, inside the caller's try, because evaluation is where most of the body is read.
        var literals = new List<string>();
        foreach (IElement reference in new FhirPathEvaluator().Evaluate(resource, expression, context))
        {
            if (LiteralOf(reference) is { } literal)
            {
                literals.Add(literal);
            }
        }

        return literals;
    }

    /// <summary>
    /// Resolves <paramref name="literal"/> to a type-only stub, or <see langword="null"/> when the resolver cannot read it.
    /// </summary>
    /// <remarks>
    /// The resolver builds JSON from the literal, so a quote, control character or bad escape in it throws. Such a literal is
    /// not of any resource type, just as the classifier ignores an invalid literal, so it behaves the same under a
    /// <c>resolve() is X</c> include as under an untyped one.
    /// </remarks>
    private static IElement? TryResolve(LightweightReferenceToElementResolver resolver, string literal)
    {
        try
        {
            return resolver.Resolve(literal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>reference</c> primitive of an expression result from its JSON, untrimmed (spec §7.7 keeps the literal
    /// verbatim; <see cref="IElement.Value"/> trims it).
    /// </summary>
    /// <returns>The literal, or <see langword="null"/> when the result has no <c>reference</c> property or is not a JSON object.</returns>
    /// <exception cref="InvalidDataException">The <c>reference</c> property is not a single JSON string.</exception>
    private static string? LiteralOf(IElement reference)
    {
        // A result that is not a JSON object (a canonical, or a Reference sent as a string) has no reference property to read.
        if (reference.Meta<JsonNode>() is not JsonObject json)
        {
            return null;
        }

        // The node compares names case-insensitively, but Ignixa reads only the exact "reference" name; the parse already
        // rejected a second property differing only in case.
        foreach ((string name, JsonNode? value) in json)
        {
            if (string.Equals(name, "reference", StringComparison.Ordinal))
            {
                return value is JsonValue literal && literal.TryGetValue(out string? text)
                    ? text
                    : throw new InvalidDataException("A Reference.reference is not a single JSON string.");
            }
        }

        return null;
    }
}
