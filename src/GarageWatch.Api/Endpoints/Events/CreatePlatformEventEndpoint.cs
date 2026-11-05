using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Endpoints;
using GarageWatch.Application.PlatformEvents;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Events;

public sealed record PlatformEventCreatedResponse(string PublicId);

public sealed class CreatePlatformEventEndpoint() : BaseEndpoint("EventStore", "CreatePlatformEvent",
    "Append a platform event",
    "Validates and appends one immutable audit event. Browser sessions may only emit UI/navigation events; service tokens are limited to events.write and to their own source.")
{
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/events", async (HttpContext context, IPlatformEventWriter writer,
            PlatformEventServiceTokenValidator tokens, UserManager<ApplicationUser> users,
            ICorrelationContext correlation, CancellationToken cancellationToken) =>
        {
            var read = await ReadAsync(context.Request, cancellationToken);
            if (read.Error is not null)
                return read.Error == "payload_too_large"
                    ? Results.Json(new ApiErrorResponse(read.Error), statusCode: StatusCodes.Status413PayloadTooLarge)
                    : Results.Json(new ApiErrorResponse(read.Error), statusCode: StatusCodes.Status400BadRequest);

            var value = read.Value!;
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(authorization))
            {
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    !tokens.TryValidate(authorization["Bearer ".Length..].Trim(), out var service))
                    return Results.Unauthorized();
                if (!string.Equals(value.Source, service, StringComparison.Ordinal) ||
                    !PlatformEventIngestionPolicy.IsServiceSource(service) ||
                    !string.Equals(value.Emitter, PlatformEventEmitters.Service, StringComparison.Ordinal))
                    return Forbidden();
            }
            else
            {
                if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
                if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (string.IsNullOrEmpty(value.EventType) ||
                    !PlatformEventIngestionPolicy.IsBrowserEventType(value.EventType))
                    return Forbidden();
                if (value.Actor is not null ||
                    !string.Equals(value.Emitter, PlatformEventEmitters.Browser, StringComparison.Ordinal) ||
                    !string.Equals(value.Source, PlatformEventSources.Shell, StringComparison.Ordinal) ||
                    value.Http is not null)
                    return Forbidden();

                var userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed)
                    ? parsed
                    : Guid.Empty;
                var user = userId == Guid.Empty ? null : await users.FindByIdAsync(userId.ToString());
                if (user is null || !user.IsActive || string.IsNullOrEmpty(user.PublicId))
                    return Results.Unauthorized();
                value = value with
                {
                    Actor = new PlatformEventActorV1(user.PublicId, null, null),
                    SessionId = ResolveSessionId(context)
                };
            }

            var userAgent = context.Request.Headers.UserAgent.ToString();
            if (userAgent.Length > PlatformEventContract.MaximumUserAgentLength)
                userAgent = userAgent[..PlatformEventContract.MaximumUserAgentLength];
            var client = value.Client ?? new PlatformEventClientV1(null, null, null, null, null);
            value = value with
            {
                CorrelationId = correlation.CorrelationId,
                RequestId = context.TraceIdentifier,
                TraceId = Activity.Current?.TraceId.ToHexString(),
                SpanId = Activity.Current?.SpanId.ToHexString(),
                Client = client with
                {
                    UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
                    Ip = context.Connection.RemoteIpAddress?.ToString()
                }
            };

            var result = await writer.WriteAsync(value, cancellationToken);
            return result.Status switch
            {
                PlatformEventWriteStatus.Created => Results.Json(
                    new PlatformEventCreatedResponse(result.PublicId!),
                    statusCode: StatusCodes.Status201Created),
                PlatformEventWriteStatus.Duplicate => Results.Json(
                    new PlatformEventCreatedResponse(result.PublicId!),
                    statusCode: StatusCodes.Status200OK),
                _ => Results.Json(new ApiErrorResponse(result.Error ?? "invalid_event"),
                    statusCode: StatusCodes.Status400BadRequest)
            };
        }).AllowAnonymous().RequireRateLimiting("events-ingest")
            .Accepts<PlatformEventV1>("application/json")
            .Produces<PlatformEventCreatedResponse>(StatusCodes.Status201Created)
            .Produces<PlatformEventCreatedResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status429TooManyRequests);

    private static IResult Forbidden() =>
        Results.Json(new ApiErrorResponse("forbidden_event_context"), statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Login issues an opaque session claim. Cookies issued before that claim existed fall back to
    /// a one-way fingerprint of the authentication cookie so old sessions remain attributable
    /// without ever storing the cookie or its secret.
    /// </summary>
    private static string? ResolveSessionId(HttpContext context)
    {
        var claim = context.User.FindFirstValue("session_id");
        if (!string.IsNullOrEmpty(claim)) return claim;
        var cookie = context.Request.Cookies["garagewatch.session"];
        if (string.IsNullOrEmpty(cookie)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)))[..32].ToLowerInvariant();
    }

    private static async Task<(PlatformEventV1? Value, string? Error)> ReadAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes) return (null, "payload_too_large");
        if (!request.HasJsonContentType()) return (null, "invalid_event");

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaxBodyBytes) return (null, "payload_too_large");
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0) return (null, "invalid_event");
        try
        {
            var value = JsonSerializer.Deserialize<PlatformEventV1>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                JsonOptions);
            if (value?.Metadata is null) return (value, null);

            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in value.Metadata)
            {
                if (!TryConvertMetadataValue(pair.Value, out var converted))
                    return (null, PlatformEventValidationErrors.InvalidMetadata);
                metadata[pair.Key] = converted;
            }

            return (value with { Metadata = metadata }, null);
        }
        catch (JsonException)
        {
            return (null, "invalid_event");
        }
    }

    private static bool TryConvertMetadataValue(object? value, out object? converted)
    {
        if (value is not JsonElement element)
        {
            converted = value;
            return value is null or string or bool or int or long or double or decimal;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                converted = element.GetString();
                return true;
            case JsonValueKind.True:
                converted = true;
                return true;
            case JsonValueKind.False:
                converted = false;
                return true;
            case JsonValueKind.Null:
                converted = null;
                return true;
            case JsonValueKind.Number when element.TryGetInt64(out var integer):
                converted = integer;
                return true;
            case JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number):
                converted = number;
                return true;
            default:
                converted = null;
                return false;
        }
    }
}
