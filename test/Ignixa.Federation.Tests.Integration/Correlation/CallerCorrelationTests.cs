using System.Globalization;
using System.Text.Json;
using Ignixa.Federation.Api.Correlation;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Correlation;

public sealed class CallerCorrelationTests
{
    private static readonly JsonElement Cases = LoadCases();

    public static TheoryData<string> CaseNames => [.. Cases.EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesThePinnedCorrelationVector(string name)
    {
        JsonElement vector = Cases.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        string? keyHex = vector.GetProperty("keyHex").GetString();
        byte[]? key = keyHex is null ? null : Convert.FromHexString(keyHex);
        JsonElement kindInput = vector.GetProperty("kind");
        CallerSubjectKind kind = kindInput.ValueKind == JsonValueKind.Number
            ? (CallerSubjectKind)kindInput.GetByte()
            : kindInput.GetString() switch
            {
                "sub" => CallerSubjectKind.Sub,
                "oid" => CallerSubjectKind.Oid,
                _ => throw new InvalidDataException("Unknown subject kind in the pinned fixture."),
            };
        string? issuer = DecodeCodeUnits(vector.GetProperty("issuerUtf16Hex"));
        string? subject = DecodeCodeUnits(vector.GetProperty("subjectUtf16Hex"));

        bool derived = CallerCorrelation.TryDerive(key, kind, issuer, subject, out string? value, out string? reason);

        JsonElement expected = vector.GetProperty("expected");
        if (expected.TryGetProperty("refusal", out JsonElement refusal))
        {
            derived.ShouldBeFalse();
            value.ShouldBeNull();
            reason.ShouldBe(refusal.GetString());
        }
        else
        {
            derived.ShouldBeTrue();
            reason.ShouldBeNull();
            // The full digest commits to every byte of the independent frame without exposing it in the API.
            value.ShouldBe(expected.GetProperty("value").GetString());
        }
    }

    private static JsonElement LoadCases()
    {
        using Stream stream = typeof(CallerCorrelationTests).Assembly.GetManifestResourceStream("d60-vectors-v1.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("cases").Clone();
    }

    private static string? DecodeCodeUnits(JsonElement input)
    {
        string? hex = input.GetString();
        // Parse code units directly: a Unicode decoder could replace the malformed test inputs.
        return hex is null ? null : string.Create(hex.Length / 4, hex, static (chars, text) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)ushort.Parse(text.AsSpan(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        });
    }
}
