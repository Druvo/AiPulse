using System.Security.Claims;

namespace AiPulse.Services.Productivity;

public static class ProductivityUser
{
    /// <summary>The signed-in user's AppUser.Id from the NameIdentifier claim, or null when signed out.</summary>
    public static int? IdOf(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
