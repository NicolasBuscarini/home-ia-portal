using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Portal;

namespace GarageWatch.Infrastructure.Integrations;

/// <summary>
/// Read-only ActivityWatch adapter. It issues GET requests only, aggregates by application and
/// discards window titles/URLs at the boundary so nothing sensitive leaves the portal.
/// </summary>
public sealed class ActivityWatchClient(
    IHttpClientFactory httpClientFactory,
    ActivityWatchOptions options,
    TimeProvider timeProvider) : IActivityWatchModule
{
    private const string ModuleId = "activitywatch";
    private const string SchemaVersion = "1.0";
    private const int MaxBuckets = 5;
    private const int MaxEventsPerBucket = 2_000;
    private const int MaxEvents = 5_000;
    private const int MaxApplications = 10;
    private const int MaxAppLength = 64;

    public async Task<ActivityWatchSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var start = now.AddHours(-options.LookbackHours);

        try
        {
            using var client = httpClientFactory.CreateClient(ActivityWatchOptions.HttpClientName);
            var buckets = await GetBucketsAsync(client, cancellationToken);
            if (buckets is null) return Unreachable(now, start, "activitywatch_invalid_response");

            var usage = new Dictionary<string, (double Seconds, int Count)>(StringComparer.Ordinal);
            var totalSeconds = 0d;
            var truncated = false;
            var processedEvents = 0;
            var bucketIds = buckets
                .Where(bucket => string.Equals(bucket.Value?.Type, "currentwindow", StringComparison.Ordinal))
                .Select(bucket => bucket.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(MaxBuckets)
                .ToArray();

            foreach (var bucketId in bucketIds)
            {
                var events = await GetEventsAsync(client, bucketId, start, now, cancellationToken);
                if (events is null) return Unreachable(now, start, "activitywatch_invalid_response");
                foreach (var item in events)
                {
                    if (processedEvents >= MaxEvents)
                    {
                        truncated = true;
                        break;
                    }
                    processedEvents++;
                    if (!DateTimeOffset.TryParse(item.Timestamp, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var timestamp))
                        continue;
                    if (!double.IsFinite(item.Duration) || item.Duration <= 0) continue;

                    var eventStart = timestamp < start ? start : timestamp;
                    var eventEnd = timestamp.AddSeconds(Math.Min(item.Duration, 86_400));
                    if (eventEnd > now) eventEnd = now;
                    var seconds = (eventEnd - eventStart).TotalSeconds;
                    if (seconds <= 0) continue;

                    var app = NormalizeApp(item);
                    var current = usage.GetValueOrDefault(app);
                    usage[app] = (current.Seconds + seconds, current.Count + 1);
                    totalSeconds += seconds;
                }
                if (truncated) break;
            }

            var applications = usage
                .OrderByDescending(entry => entry.Value.Seconds)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(MaxApplications)
                .Select(entry => new ActivityWatchApplicationUsage(entry.Key,
                    Math.Round(entry.Value.Seconds, 1), entry.Value.Count))
                .ToArray();
            if (usage.Count > MaxApplications) truncated = true;

            return new ActivityWatchSummary(SchemaVersion, ModuleId, true, now, start, now,
                Math.Round(totalSeconds, 1), truncated, applications, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable(now, start, "activitywatch_timeout");
        }
        catch (ActivityWatchResponseException exception)
        {
            var code = exception.StatusCode is null
                ? "activitywatch_invalid_response"
                : "activitywatch_error";
            return Unreachable(now, start, code);
        }
        catch (HttpRequestException)
        {
            return Unreachable(now, start, "activitywatch_unreachable");
        }
        catch (JsonException)
        {
            return Unreachable(now, start, "activitywatch_invalid_response");
        }
    }

    private async Task<Dictionary<string, BucketDto?>?> GetBucketsAsync(HttpClient client,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest("api/0/buckets/");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ActivityWatchResponseException(response.StatusCode);
        return await response.Content.ReadFromJsonAsync<Dictionary<string, BucketDto?>>(cancellationToken)
               ?? throw new ActivityWatchResponseException(null);
    }

    private async Task<EventDto[]?> GetEventsAsync(HttpClient client, string bucketId, DateTimeOffset start,
        DateTimeOffset end, CancellationToken cancellationToken)
    {
        var path = $"api/0/buckets/{Uri.EscapeDataString(bucketId)}/events" +
                   $"?start={Uri.EscapeDataString(start.ToString("O", CultureInfo.InvariantCulture))}" +
                   $"&end={Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture))}" +
                   $"&limit={MaxEventsPerBucket}";
        using var request = CreateRequest(path);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ActivityWatchResponseException(response.StatusCode);
        return await response.Content.ReadFromJsonAsync<EventDto[]>(cancellationToken)
               ?? throw new ActivityWatchResponseException(null);
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (options.ApiToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        return request;
    }

    private static string NormalizeApp(EventDto item)
    {
        if (item.Data is null || !item.Data.TryGetValue("app", out var element) ||
            element.ValueKind != JsonValueKind.String)
            return "unknown";
        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var cleaned = new string(raw.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (cleaned.Length == 0) return "unknown";
        return cleaned.Length <= MaxAppLength ? cleaned : cleaned[..MaxAppLength];
    }

    private static ActivityWatchSummary Unreachable(DateTimeOffset now, DateTimeOffset start, string errorCode) =>
        new(SchemaVersion, ModuleId, false, now, start, now, 0, false, [], errorCode);

    private sealed record BucketDto(string? Type);

    private sealed record EventDto(string? Timestamp, double Duration, Dictionary<string, JsonElement>? Data);

    private sealed class ActivityWatchResponseException(System.Net.HttpStatusCode? statusCode)
        : Exception("activitywatch_response_error")
    {
        public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
    }
}
