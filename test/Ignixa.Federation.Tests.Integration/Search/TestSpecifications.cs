using Ignixa.Abstractions;
using Ignixa.Federation.Core.Search;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.Parsing;
using Ignixa.Specification.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ignixa.Federation.Tests.Integration.Search;

internal static class TestSpecifications
{
    public static IFederationSearchSpecification R4 { get; } = Create(FhirVersion.R4);

    public static IFederationSearchSpecification Create(FhirVersion version)
    {
        IFhirSchemaProvider schema = version.GetSchemaProvider();
        var definitions = new SearchParameterDefinitionManager(schema, NullLogger<SearchParameterDefinitionManager>.Instance);
        SearchOptionsBuilder CreateCompiler()
        {
            var references = new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance);
            var parser = new ExpressionParser(() => definitions, new SearchParameterExpressionParser(references, schema), schema);
            return new SearchOptionsBuilder(parser, definitions);
        }

        return new FederationSearchSpecification(version, schema, definitions, CreateCompiler);
    }
}
