namespace Maldex.Core.Domain;

public class AppUser
{
    public Guid Id { get; set; }
    public string EntraObjectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
}
