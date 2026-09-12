using System.ComponentModel.DataAnnotations;
using ExamArchive.Models;

namespace ExamArchive.Dtos;

public class ChangeRoleRequest
{
    // Required does not reject a missing value on an enum — it binds to zero.
    [EnumDataType(typeof(UserRole), ErrorMessage = "A valid role is required.")]
    public UserRole Role { get; set; }
}
