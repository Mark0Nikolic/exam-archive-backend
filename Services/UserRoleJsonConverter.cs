using System.Text.Json;
using System.Text.Json.Serialization;
using ExamArchive.Models;

namespace ExamArchive.Services;

// Reads and writes UserRole as its number. Registered ahead of the global
// JsonStringEnumConverter, which would otherwise claim this enum too; the rest keep
// that treatment on purpose.
public sealed class UserRoleJsonConverter : JsonConverter<UserRole>
{
    public override UserRole Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException(
                "A role must be sent as a number, for example 2 for Admin. "
                + "Role names are not accepted.");
        }

        // Not validated against the defined members here: an out-of-range number
        // becomes an undefined UserRole, which the EnumDataType attribute on the
        // request DTOs then rejects with a message naming the field.
        return (UserRole)reader.GetInt32();
    }

    public override void Write(
        Utf8JsonWriter writer,
        UserRole value,
        JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)value);
}
