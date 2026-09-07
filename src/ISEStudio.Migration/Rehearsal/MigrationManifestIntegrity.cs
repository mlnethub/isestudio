using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISEStudio.Migration.Rehearsal;

public static class MigrationManifestIntegrity
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MigrationRehearsalManifest Finalize(MigrationRehearsalManifest manifest)
    {
        var completedAt = manifest.CompletedAt;
        var steps = manifest.Steps.ToArray();
        var reportIndex = Array.FindIndex(steps, step => step.Name == "report-serialization");
        if (reportIndex < 0)
        {
            throw new InvalidDataException("Manifest is missing the report-serialization step.");
        }

        steps[reportIndex] = steps[reportIndex] with { Checksum = string.Empty };
        var reportPayload = manifest with { CompletedAt = completedAt, Steps = steps, ManifestChecksum = null };
        var reportChecksum = Checksum(SerializeWithoutManifestChecksum(reportPayload));
        steps[reportIndex] = steps[reportIndex] with { Checksum = reportChecksum };

        var manifestPayload = manifest with { CompletedAt = completedAt, Steps = steps, ManifestChecksum = null };
        var manifestChecksum = Checksum(SerializeWithoutManifestChecksum(manifestPayload));
        return manifestPayload with { ManifestChecksum = manifestChecksum };
    }

    public static string Serialize(MigrationRehearsalManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    public static void Validate(string serializedManifest)
    {
        MigrationRehearsalManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MigrationRehearsalManifest>(serializedManifest, Options)
                ?? throw new InvalidDataException("Manifest is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Manifest is not valid JSON.", ex);
        }

        if (string.IsNullOrWhiteSpace(manifest.ManifestChecksum))
        {
            throw new InvalidDataException("Manifest checksum is missing.");
        }

        var reportIndex = manifest.Steps.ToList().FindIndex(step => step.Name == "report-serialization");
        if (reportIndex < 0)
        {
            throw new InvalidDataException("Manifest is missing the report-serialization step.");
        }

        var reportSteps = manifest.Steps.ToArray();
        var expectedReportChecksum = reportSteps[reportIndex].Checksum;
        reportSteps[reportIndex] = reportSteps[reportIndex] with { Checksum = string.Empty };
        var reportPayload = manifest with { Steps = reportSteps, ManifestChecksum = null };
        if (!string.Equals(expectedReportChecksum, Checksum(SerializeWithoutManifestChecksum(reportPayload)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Report-serialization checksum does not match the final manifest payload.");
        }

        var payload = manifest with { ManifestChecksum = null };
        if (!string.Equals(manifest.ManifestChecksum, Checksum(SerializeWithoutManifestChecksum(payload)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest checksum does not match the final JSON payload.");
        }
    }

    private static string SerializeWithoutManifestChecksum(MigrationRehearsalManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    private static string Checksum(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}