#:project ../src/Ignixa.Federation.Core/Ignixa.Federation.Core.csproj
#:project ../samples/Ignixa.Federation.SampleClient/Ignixa.Federation.SampleClient.csproj
#:property PublishAot=false

using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using Ignixa.Federation.Core.Configuration;

var output = args[0];
var source = $"https://github.com/brendankowitz/fhir-federation-service/blob/{args[1]}";
var core = "src/Ignixa.Federation.Core";
static Dictionary<string, XElement> LoadComments(Assembly assembly) =>
    XDocument.Load(Path.ChangeExtension(assembly.Location, ".xml"))
    .Descendants("member").ToDictionary(e => (string)e.Attribute("name")!);
var comments = LoadComments(typeof(FederationOptions).Assembly);

string Render(XNode node) => node switch
{
    XText text => text.Value,
    XElement { Name.LocalName: "see" } e => $"`{(string?)e.Attribute("langword") ?? ((string)e.Attribute("cref")!)[2..]}`",
    XElement { Name.LocalName: "paramref" } e => $"`{e.Attribute("name")!.Value}`",
    XElement { Name.LocalName: "c" } e => $"`{e.Value}`",
    XElement e => string.Concat(e.Nodes().Select(Render)),
    _ => ""
};
string Summary(XElement member) => string.Join(" ", Render(member.Element("summary")!)
    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|", StringComparison.Ordinal);

using var config = File.CreateText(Path.Combine(output, "configuration.md"));
config.WriteLine($"""
    # Configuration reference

    These are defaults of new **Core options objects**, not a dump of a running host.
    See [binding and validation](binding.md) for which settings are bound.
    Array index `0` below stands for any configured shard.
    [Options and validators]({source}/{core}/Configuration/FederationOptionsValidator.cs) · [registry checks]({source}/{core}/Shards/ShardRegistry.cs).

    | Key | Type | Default | Meaning |
    | --- | --- | --- | --- |
    """);
WriteOptions(typeof(FederationOptions), new FederationOptions(), FederationOptions.SectionName);

void WriteOptions(Type type, object defaults, string prefix)
{
    foreach (var property in type.GetProperties())
    {
        var key = $"{prefix}:{property.Name}";
        var t = property.PropertyType;
        var value = property.GetValue(defaults);
        var list = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IList<>);
        var element = list ? t.GetGenericArguments()[0] : t;
        var nested = element.Namespace == typeof(FederationOptions).Namespace && element.IsClass;
        var display = value switch
        {
            null => "unset",
            IEnumerable when list => "[]",
            _ when nested => "section",
            _ => value.ToString()
        };
        config.WriteLine($"| `{key}` | {element.Name}{(list ? "[]" : "")} | `{display}` | {Summary(comments[$"P:{type.FullName}.{property.Name}"])} |");
        if (nested)
            WriteOptions(element, (list ? null : value) ?? Activator.CreateInstance(element)!, list ? $"{key}:0" : key);
    }
}

using var contracts = File.CreateText(Path.Combine(output, "contracts.md"));
contracts.WriteLine("# Shard API reference\n\nThese are .NET integration contracts, **not HTTP routes**. Signatures and summaries come from the Core assembly's XML documentation.");
WriteTypes(contracts, [typeof(Ignixa.Federation.Core.Shards.IShardClient), typeof(Ignixa.Federation.Core.Shards.ShardRegistry)], comments, $"{core}/Shards");

var sample = Assembly.Load("Ignixa.Federation.SampleClient");
string[] clientTypes = ["FederationClient", "FederationClientOptions", "ShardReadOptions", "SearchPage", "FederatedEntry", "EntryMode", "SearchResultSet", "OperationIssue", "FederationRequestException", "SearchExpiredException", "LinkRejectedException"];
using var client = File.CreateText(Path.Combine(output, "client-api.md"));
client.WriteLine("# Sample client API reference\n\nThese are internal types in the [sample source](" + source + "/samples/Ignixa.Federation.SampleClient/Ignixa.Federation.SampleClient.csproj), not a published SDK. See [using the CLI and sample code](sample-client.md) for complete examples. This reference lists members with XML summaries; consult each source link for the full declaration.");
WriteTypes(client, clientTypes.Select(name => sample.GetType($"{sample.GetName().Name}.{name}", throwOnError: true)!), LoadComments(sample), "samples/Ignixa.Federation.SampleClient");

void WriteTypes(TextWriter writer, IEnumerable<Type> types, Dictionary<string, XElement> xml, string folder)
{
    foreach (var type in types)
    {
        var name = type.FullName!;
        var publicMembers = type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        writer.WriteLine($"\n## {type.Name}\n\n[Source]({source}/{folder}/{type.Name}.cs)\n\n{Summary(xml[$"T:{name}"])}");
        foreach (var (id, member) in xml.Where(kv => kv.Key[2..].StartsWith(name + ".", StringComparison.Ordinal)))
        {
            var signature = id[(name.Length + 3)..];
            if (publicMembers.Contains(signature.Split('(')[0]) && member.Element("summary") is not null)
                writer.WriteLine($"\n- `{signature}` — {Summary(member)}");
        }
    }
}
