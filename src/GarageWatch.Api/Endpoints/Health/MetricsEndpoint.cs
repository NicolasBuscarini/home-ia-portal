using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.Health;

public sealed class MetricsEndpoint() : BaseEndpoint("Health", "GetMetrics", "Expose Prometheus metrics",
    "Returns the in-process Prometheus text exposition for the EventStore counters. The payload is aggregate-only and carries no event, actor or resource values.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/metrics", (PlatformEventMetrics metrics) =>
                Results.Text(metrics.Render(), PlatformEventMetrics.ContentType))
            .AllowAnonymous().Produces<string>(StatusCodes.Status200OK, "text/plain");
}
