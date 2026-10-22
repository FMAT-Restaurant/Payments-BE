using System.Text.Json;

namespace PaymentsModule.Models;

// Enum values travel on the wire with their JSON names (COMPLETADO, EFECTIVO, ...), not their C# names.
public static class WireEnum
{
    public static bool TryParse<T>(string? value, out T result)
        where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value.Trim()));
            return Enum.IsDefined(result);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
