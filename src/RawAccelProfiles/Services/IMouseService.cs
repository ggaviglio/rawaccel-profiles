using System.Threading.Tasks;
using RawAccelProfiles.Models;

namespace RawAccelProfiles.Services;

public interface IMouseService
{
    Task ApplyProfileAsync(MouseProfile profile);

    /// <summary>
    /// Clears the cached last-applied hardware state, so the next <see cref="ApplyProfileAsync"/>
    /// call is never skipped by the dedup check even if its computed values match what was cached
    /// before -- needed after something writes to the registry outside this service (e.g.
    /// reverting to the Windows default curve), since otherwise the dedup check would see no
    /// change from its own perspective and leave the reverted state stuck in place.
    /// </summary>
    void InvalidateAppliedState();
}
