using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// The R4 <c>search-param-type</c> codes a shard can report. Recognising a type says nothing about whether the federation
/// supports it.
/// </summary>
public enum SearchParameterType
{
    /// <summary>The <c>number</c> code.</summary>
    Number,

    /// <summary>The <c>date</c> code.</summary>
    Date,

    /// <summary>The <c>string</c> code.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names the R4 search-param-type code 'string'.")]
    String,

    /// <summary>The <c>token</c> code.</summary>
    Token,

    /// <summary>The <c>reference</c> code.</summary>
    Reference,

    /// <summary>The <c>composite</c> code.</summary>
    Composite,

    /// <summary>The <c>quantity</c> code.</summary>
    Quantity,

    /// <summary>The <c>uri</c> code.</summary>
    Uri,

    /// <summary>The <c>special</c> code.</summary>
    Special,
}
