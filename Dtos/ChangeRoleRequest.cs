using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

/// <summary>
/// Promoting a moderator, or demoting an administrator.
/// </summary>
/// <remarks>
/// This is also how a second administrator comes to exist, which is the answer to
/// the archive having exactly one: promote a moderator, and the single point of
/// failure is gone. No separate mechanism is needed because the role is a parameter
/// everywhere it appears.
/// </remarks>
public class ChangeRoleRequest
{
    // Required does not reject a missing value on an enum — it binds to zero.
    // EnumDataType does, because zero is not a defined member of UserRole.
    [EnumDataType(typeof(UserRole), ErrorMessage = "A valid role is required.")]
    public UserRole Role { get; set; }
}
