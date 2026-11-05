using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// Reads the single active policy version. When nothing has been promoted yet it returns the
/// documented baseline, preserving the behavior before calibration existed.
/// </summary>
public sealed class IdentityMatchPolicySource(IamDbContext database) : IIdentityMatchPolicySource
{
    public async Task<IdentityMatchPolicy> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = await database.IdentityMatchPolicyVersions.AsNoTracking()
            .Where(version => version.Status == (int)IdentityMatchPolicyStatus.Active)
            .Select(version => new { version.Threshold, version.Margin, version.Version })
            .SingleOrDefaultAsync(cancellationToken);
        return active is null
            ? IdentityMatchPolicy.Baseline
            : new IdentityMatchPolicy(active.Threshold, active.Margin,
                $"identity-match.policy.v{active.Version}");
    }
}
