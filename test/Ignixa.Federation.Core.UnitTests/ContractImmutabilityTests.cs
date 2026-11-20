using System.Reflection;
using Ignixa.Federation.Core.Configuration;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests;

public sealed class ContractImmutabilityTests
{
    // Core's records and classes validate in their constructors. A public set or init accessor would let an object
    // initializer or a with expression skip that check, so every public property is get-only. The options classes are
    // the exception: they are mutable on purpose for configuration binding, and FederationOptionsValidator checks them.
    [Fact]
    public void PublicCoreTypes_OutsideConfiguration_HaveNoPublicSetters()
    {
        string[] settable = typeof(FederationOptions).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace != typeof(FederationOptions).Namespace)
            .SelectMany(t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToArray();

        settable.ShouldBeEmpty();
    }
}
