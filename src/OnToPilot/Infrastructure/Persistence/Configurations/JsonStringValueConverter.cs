using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace OnToPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Round-trips a <see cref="JsonDocument"/> through a database-stored
/// <see cref="string"/>. Keeps the public API ergonomic (consumers see a
/// <see cref="JsonDocument"/>) while letting the database provider treat the
/// column as plain TEXT — so SQLite (unit tests) and PostgreSQL (production)
/// can share the same model without provider-specific column-type assertions.
/// On PostgreSQL the migration additionally rewrites the column type to
/// <c>jsonb</c>, see <c>OnModelCreating</c> in
/// <see cref="OnToPilotDbContext"/>.
/// </summary>
public sealed class JsonStringValueConverter : ValueConverter<JsonDocument?, string?>
{
    /// <summary>Singleton instance to avoid allocating one converter per property.</summary>
    public static readonly JsonStringValueConverter Instance = new();

    private JsonStringValueConverter()
        : base(
            convertToProviderExpression: v => v == null ? null : v.RootElement.GetRawText(),
            convertFromProviderExpression: v => v == null ? null : JsonDocument.Parse(v, default))
    {
    }
}