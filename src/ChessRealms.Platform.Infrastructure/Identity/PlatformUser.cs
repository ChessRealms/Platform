using Microsoft.AspNetCore.Identity;

namespace ChessRealms.Platform.Infrastructure.Identity;

public sealed class PlatformUser : IdentityUser<Guid>
{
    public PlatformUser()
    {
        Id = Guid.NewGuid();
    }
}
