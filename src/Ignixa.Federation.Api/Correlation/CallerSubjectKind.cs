namespace Ignixa.Federation.Api.Correlation;

/// <summary>Which validated subject identity the host selected in the issuer's namespace.</summary>
internal enum CallerSubjectKind : byte
{
    Sub = 0x01,
    Oid = 0x02,
}
