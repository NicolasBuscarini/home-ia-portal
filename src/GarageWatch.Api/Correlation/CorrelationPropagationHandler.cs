namespace GarageWatch.Api.Correlation;

/// <summary>
/// Propagates the request correlation id to the Monitoring service so portal and service events for
/// the same action share a correlation. Existing headers are replaced, never duplicated.
/// </summary>
public sealed class CorrelationPropagationHandler(ICorrelationContext correlation) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(correlation.CorrelationId))
        {
            request.Headers.Remove(CorrelationMiddleware.HeaderName);
            request.Headers.TryAddWithoutValidation(CorrelationMiddleware.HeaderName,
                correlation.CorrelationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
