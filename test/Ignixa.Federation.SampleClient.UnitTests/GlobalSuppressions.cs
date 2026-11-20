using System.Diagnostics.CodeAnalysis;

// ScriptedHandler holds no unmanaged resources. Each test hands it to the FederationClient under test, which owns and
// disposes it, and CA2000 cannot see that ownership transfer through the helpers in this namespace. The
// HttpResponseMessage instances ScriptedHandler returns are disposed by the code that sends the request (ScopedEndpoint),
// not by the handler.
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Scope = "namespaceanddescendants",
    Target = "~N:Ignixa.Federation.SampleClient.UnitTests",
    Justification = "Test handlers are normally owned and disposed by the FederationClient under test, and the responses they return by the code that sends the request. Handlers created in tests that expect FederationClient construction to throw are not disposed, which is acceptable in tests.")]
