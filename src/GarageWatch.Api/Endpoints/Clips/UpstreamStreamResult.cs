namespace GarageWatch.Api.Endpoints.Clips;

/// <summary>
/// Streams a validated file from the monitoring service without buffering it in the portal.
/// Range/status headers are forwarded so media seeking keeps working through the authenticated portal.
/// </summary>
public sealed class UpstreamStreamResult(HttpResponseMessage response) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        try
        {
            var target = httpContext.Response;
            target.StatusCode = (int)response.StatusCode;
            var content = response.Content.Headers;
            if (content.ContentType is not null) target.ContentType = content.ContentType.ToString();
            if (content.ContentLength is not null) target.ContentLength = content.ContentLength;
            if (content.ContentRange is not null)
                target.Headers.ContentRange = content.ContentRange.ToString();
            if (response.Headers.AcceptRanges.Count > 0)
                target.Headers.AcceptRanges = response.Headers.AcceptRanges.ToString();
            if (content.LastModified is not null) target.Headers.LastModified = content.LastModified.Value.ToString("R");
            if (response.Headers.ETag is not null) target.Headers.ETag = response.Headers.ETag.ToString();
            await using var stream = await response.Content.ReadAsStreamAsync(httpContext.RequestAborted);
            await stream.CopyToAsync(target.Body, httpContext.RequestAborted);
        }
        finally
        {
            response.Dispose();
        }
    }
}
