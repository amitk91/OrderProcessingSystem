using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace OrderProcessing.Infrastructure.Persistence;

/// <summary>
/// Value converters bridging domain types to the types SQLite can actually store
/// (specification section 5.1).
/// </summary>
internal static class SqliteValueConverters
{
    /// <summary>
    /// GUIDs as canonical lower-case text. Stored as TEXT rather than BLOB so the
    /// database is legible when inspected directly, which matters more here than the
    /// few bytes saved.
    /// </summary>
    public static readonly ValueConverter<Guid, string> Guid =
        new(value => value.ToString("D"), value => System.Guid.Parse(value));

    public static readonly ValueConverter<Guid?, string?> NullableGuid =
        new(
            value => value == null ? null : value.Value.ToString("D"),
            value => value == null ? null : System.Guid.Parse(value));

    /// <summary>
    /// Timestamps as ISO-8601 with an explicit offset, normalised to UTC on write.
    /// </summary>
    /// <remarks>
    /// The round-trip format ("O") sorts correctly as text, so ordering by a timestamp
    /// column behaves as expected. Normalising to UTC on the way in prevents two rows
    /// written from different machine locales sorting inconsistently.
    /// </remarks>
    public static readonly ValueConverter<DateTimeOffset, string> DateTimeOffset =
        new(
            value => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            value => System.DateTimeOffset.Parse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind));

    public static readonly ValueConverter<DateTimeOffset?, string?> NullableDateTimeOffset =
        new(
            value => value == null
                ? null
                : value.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            value => value == null
                ? null
                : System.DateTimeOffset.Parse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind));
}
