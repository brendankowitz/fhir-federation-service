using System.Diagnostics.CodeAnalysis;

// ScriptedHandler holds no unmanaged resources. Each test hands it to the FederationClient under test, which owns and
// disposes it, and CA2000 cannot see that ownership transfer through the helpers in this namespace.
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Scope = "namespaceanddescendants",
    Target = "~N:Ignixa.Federation.SampleClient.UnitTests",
    Justification = "Test handlers are owned and disposed by the FederationClient under test.")]