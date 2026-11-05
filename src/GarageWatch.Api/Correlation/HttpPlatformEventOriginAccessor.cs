using System.Diagnostics;
using System.Security.Claims;
using GarageWatch.Application.PlatformEvents;

namespace GarageWatch.Api.Correlation;

public sealed class HttpPlatformEventOriginAccessor(
    IHttpContextAccessor httpContextAccessor,
    ICorrelationContext correlation) : IPlatformEventOriginAccessor
{
    public PlatformEventOrigin? Current
    {
        get
        {
            var context = httpContextAccessor.HttpContext;
            if (context is null) return null;

            var actorUserId = Guid.TryParse(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : (Guid?)null;
            var activity = Activity.Current;
            return new PlatformEventOrigin(
                actorUserId,
                PlatformEventSession.Resolve(context),
                correlation.CorrelationId,
                context.TraceIdentifier,
                activity?.TraceId.ToHexString(),
                activity?.SpanId.ToHexString(),
                new PlatformEventClientV1(
                    Truncate(context.Request.Headers.UserAgent.ToString(), PlatformEventContract.MaximumUserAgentLength),
                    null,
                    null,
                    null,
                    context.Connection.RemoteIpAddress?.ToString()));
        }
    }

    private static string? Truncate(string? value, int maximum) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= maximum ? value : value[..maximum];
}
