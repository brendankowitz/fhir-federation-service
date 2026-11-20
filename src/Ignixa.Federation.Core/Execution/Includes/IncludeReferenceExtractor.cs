using System.Text;
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
/// A result with no <c>reference</c> primitive (identifier-only or display-only) yields nothing. A caller parses each hop's
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
    /// <returns>The literals, verbatim; duplicates are kept.</returns>
    /// <exception cref="ShardFailureException">
    /// The match's body cannot be read as a resource (invalid UTF-8, invalid or malformed JSON, or any other parse
    /// failure), or is a resource of another type than its key: a contract violation of its shard.
    /// </exception>
    public static IReadOnlyList<string> Extract(ShardEntry match, Expression expression)
    {
        R4CoreSchemaProvider schema = Schema.Value;
        IElement resource = Parse(match, schema);
        var resolver = new LightweightReferenceToElementResolver(
            new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance),
            schema);
        EvaluationContext context = new FhirEvaluationContext()
            .WithElementResolver(resolver.Resolve)
            .WithSchema(schema)
            .WithResource(resource)
            .WithRootResource(resource);

        var literals = new List<string>();
        foreach (IElement reference in new FhirPathEvaluator().Evaluate(resource, expression, context))
        {
            if (reference.Children("reference") is [{ Value: string literal }, ..])
            {
                literals.Add(literal);
            }
        }

        return literals;
    }

    private static IElement Parse(ShardEntry match, R4CoreSchemaProvider schema)
    {
        IElement resource;
        try
        {
            resource = ResourceJsonNode.Parse(StrictUtf8.GetString(match.Resource.Span)).ToElement(schema);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The body is the shard's data, and the parser reports bad shapes with several exception types (a non-string
            // resourceType, a JSON null, a duplicate property), so every parse failure is the shard's contract violation.
            throw new ShardFailureException(match.Key.Shard, ShardFailureReason.ContractViolation, exception);
        }

        // The key came from the bundle entry; a body of another type would silently match no expression.
        return resource.InstanceType == match.Key.Type.Value
            ? resource
            : throw new ShardFailureException(match.Key.Shard, ShardFailureReason.ContractViolation, null);
    }
}
