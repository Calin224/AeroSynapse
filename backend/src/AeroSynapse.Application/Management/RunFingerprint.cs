using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Application.Management;

// Versioned, language-independent encoding: keys sorted ordinally, numbers as IEEE-754 bits.
// Run identity is metadata, including the runId repeated inside KPI snapshots.
public static class RunFingerprint
{
    public const string Version = "sha256-tree-v1";
    public static string Input(SimulationRequest request) => Hash(JsonSerializer.SerializeToElement(request, ContractJson.Options), "input");
    public static string Result(SimulationResponse result) => Hash(JsonSerializer.SerializeToElement(result, ContractJson.Options), "result");
    public static string Engine(SimulationRequest request) => request.Journey is not null ? JourneyConventions.Engine : request.PassengerGeneration?.Flow is not null
        ? "flow-1.0" : request.Engine ?? "legacy-1.0";

    public static string Hash(JsonElement value, string kind)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append($"aerosynapse:{Version}:{kind}\n");
        Write(value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));
        void String(string text)
        {
            text = Regex.Replace(text, @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\.(\d{1,6})Z$", match =>
                match.Groups[1].Value + (match.Groups[2].Value.TrimEnd('0') is { Length: > 0 } fraction ? "." + fraction : "") + "Z");
            Append("s" + Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture) + ":" + text);
        }
        void Write(JsonElement item)
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    Append("{");
                    foreach (var property in item.EnumerateObject().Where(x => x.Name != "runId" && !(kind == "input" && x.Name is "scenarioId" or "scenarioRevision")).OrderBy(x => x.Name, StringComparer.Ordinal))
                    { String(property.Name); Write(property.Value); }
                    Append("}"); break;
                case JsonValueKind.Array:
                    Append("["); foreach (var child in item.EnumerateArray()) Write(child); Append("]"); break;
                case JsonValueKind.String: String(item.GetString()!); break;
                case JsonValueKind.Number:
                    var number = item.GetDouble();
                    if (!double.IsFinite(number)) throw new JsonException("Fingerprint numbers must be finite.");
                    Append("n" + BitConverter.DoubleToUInt64Bits(number == 0 ? 0 : number).ToString("x16", CultureInfo.InvariantCulture) + ";"); break;
                case JsonValueKind.True: Append("t"); break;
                case JsonValueKind.False: Append("f"); break;
                case JsonValueKind.Null: Append("z"); break;
                default: throw new JsonException("Unsupported fingerprint value.");
            }
        }
    }
}
