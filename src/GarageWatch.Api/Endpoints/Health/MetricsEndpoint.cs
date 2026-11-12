using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Maintenance;

namespace GarageWatch.Api.Endpoints.Health;

public sealed class MetricsEndpoint() : BaseEndpoint("Health", "GetMetrics", "Expose Prometheus metrics",
    "Returns the in-process Prometheus text exposition for the EventStore and backup counters. The payload is aggregate-only and carries no event, actor, resource or backup file values.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/metrics", (PlatformEventMetrics metrics, PortalBackupMetrics backups) =>
                Results.Text(metrics.Render() + backups.Render(), PlatformEventMetrics.ContentType))
            .AllowAnonymous().Produces<string>(StatusCodes.Status200OK, "text/plain");
}
