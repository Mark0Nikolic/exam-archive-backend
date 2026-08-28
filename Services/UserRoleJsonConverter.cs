using System.Text.Json;
using System.Text.Json.Serialization;
using ExamArchive.Models;

namespace ExamArchive.Services;

/// <summary>
/// Reads and writes <see cref="UserRole"/> as its number.
/// </summary>
/// <remarks>
/// Registered ahead of the global <c>JsonStringEnumConverter</c>, which would
/// otherwise claim every enum and turn this one back into a name. The rest keep
/// that treatment on purpose: a paper's status reads as "Pending" in a response
/// and in the database, and only the role is numeric.
/// <para>
/// Reading refuses a JSON string outright rather than parsing it. Accepting both
/// would mean the wire format is whatever a caller felt like sending, and the
/// first client to send "Admin" would keep working right up until somebody
/// renamed the member.
/// </para>
/// </remarks>
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

        // Not validated against the defined members here. A number outside the enum
        // becomes an undefined UserRole, which the EnumDataType attribute on the
        // request DTOs then rejects with a message naming the field — better than a
        // deserialization failure that names only the request body.
        return (UserRole)reader.GetInt32();
    }

    public override void Write(
        Utf8JsonWriter writer,
        UserRole value,
        JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)value);
}
