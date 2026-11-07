using System.Globalization;
using GarageWatch.Application.PlatformEvents;
using Microsoft.AspNetCore.Http;

namespace GarageWatch.Api.Endpoints.Admin;

internal static class PlatformEventQueryParameters
{
    public static bool TryCreate(IQueryCollection values, DateTimeOffset nowUtc,
        out PlatformEventQuery query)
    {
        query = null!;
        if (!TryDate(values["fromUtc"], out var from) || !TryDate(values["toUtc"], out var to))
            return false;
        to ??= nowUtc;
        from ??= to.Value - PlatformEventQuery.DefaultWindow;
        if (to <= from || to - from > PlatformEventQuery.MaximumWindow) return false;

        if (!TryInt(values["pageSize"], PlatformEventQuery.DefaultPageSize, out var pageSize)
            || pageSize is < 1 or > PlatformEventQuery.MaximumPageSize)
            return false;

        PlatformEventCursor? cursor = null;
        var rawCursor = Text(values, "cursor");
        if (rawCursor is not null)
        {
            if (!PlatformEventCursor.TryDecode(rawCursor, out var decoded)) return false;
            cursor = decoded;
        }

        var actor = Text(values, "actor");
        var excludeActors = values["excludeActor"]
            .SelectMany(value => (value ?? string.Empty).Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (excludeActors.Length > 20 || excludeActors.Any(value => value.Length > 128)) return false;
        var eventType = Text(values, "eventType");
        var category = Text(values, "category");
        var outcome = Text(values, "outcome");
        var source = Text(values, "source");
        var correlation = Text(values, "correlationId");
        var resourceType = Text(values, "resourceType");
        var resourceId = Text(values, "resourceId");
        var search = Text(values, "q");
        var browser = Text(values, "browser");
        var os = Text(values, "os");
        var device = Text(values, "device");

        if (actor is { Length: > 128 } || correlation is { Length: > 128 }
            || resourceType is { Length: > 40 } || resourceId is { Length: > 64 }
            || search is { Length: > 128 } || browser is { Length: > 64 }
            || os is { Length: > 64 } || device is { Length: > 64 })
            return false;
        if (eventType is not null && (!PlatformEventTypeCatalog.IsValidEventType(eventType)
            || eventType.Length > 64))
            return false;
        if (category is not null && !PlatformEventCategories.All.Contains(category)) return false;
        if (outcome is not null && !PlatformEventOutcomes.All.Contains(outcome)) return false;
        if (source is not null && !PlatformEventSources.IsValid(source)) return false;
        if (correlation is not null && correlation.Length < PlatformEventContract.MinimumCorrelationIdLength)
            return false;

        query = new PlatformEventQuery(
            from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), actor, excludeActors, eventType,
            category, outcome, source, correlation, resourceType, resourceId, search, browser, os,
            device, pageSize, cursor);
        return true;
    }

    private static bool TryDate(string? raw, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryInt(string? raw, int fallback, out int value)
    {
        value = fallback;
        return string.IsNullOrWhiteSpace(raw)
            || int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string? Text(IQueryCollection values, string key)
    {
        var raw = values[key].ToString().Trim();
        return raw.Length == 0 ? null : raw;
    }
}
