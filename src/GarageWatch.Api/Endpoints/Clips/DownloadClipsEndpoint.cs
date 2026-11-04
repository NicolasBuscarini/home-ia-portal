using System.IO.Compression;
using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class DownloadClipsEndpoint() : BaseEndpoint("Clips", "DownloadClips",
    "Download selected clips",
    "Streams an authorized ZIP archive of up to 20 derived MP4 clips; each clip keeps the current clips.read scope.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/download", async (string? ids, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, IMonitoringClipsClient clips,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var administrator = await users.IsInRoleAsync(user, "Administrator");
            var requested = (ids ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (requested.Length is 0 or > 20 ||
                requested.Any(id => !PublicIdFormat.IsValid(id, PublicIdPrefixes.ClipPrefix)))
                return Results.BadRequest(new ApiErrorResponse("invalid_clip_selection"));

            var resolved = new List<(string Id, string? Path, HttpResponseMessage? Upstream)>();
            foreach (var id in requested)
            {
                var result = await library.GetAsync(user.Id, administrator, id, cancellationToken);
                if (result.Details is null || (result.MediaPath is null && !result.RemoteMedia))
                {
                    DisposeUpstreams(resolved);
                    return Results.NotFound();
                }
                if (result.RemoteMedia)
                {
                    var upstream = await clips.GetMediaAsync(id, null, cancellationToken);
                    if (upstream is null || !upstream.IsSuccessStatusCode)
                    {
                        upstream?.Dispose();
                        DisposeUpstreams(resolved);
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    }
                    resolved.Add((id, null, upstream));
                    continue;
                }
                if (!await ClipLibraryService.HasValidHashAsync(result.MediaPath!,
                        result.Details.MediaSha256!, cancellationToken))
                {
                    DisposeUpstreams(resolved);
                    return Results.NotFound();
                }
                resolved.Add((id, result.MediaPath, null));
            }

            var archivePath = Path.Combine(Path.GetTempPath(),
                $"home-ia-clips-{Guid.NewGuid():N}.zip");
            try
            {
                await using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81_920, useAsync: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(archivePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    foreach (var item in resolved)
                    {
                        var entry = zip.CreateEntry($"{item.Id}.mp4", CompressionLevel.NoCompression);
                        await using var target = entry.Open();
                        if (item.Upstream is not null)
                        {
                            await using var source = await item.Upstream.Content
                                .ReadAsStreamAsync(cancellationToken);
                            await source.CopyToAsync(target, cancellationToken);
                            continue;
                        }
                        await using var local = File.OpenRead(item.Path!);
                        await local.CopyToAsync(target, cancellationToken);
                    }
                }
            }
            catch
            {
                File.Delete(archivePath);
                DisposeUpstreams(resolved);
                throw;
            }
            DisposeUpstreams(resolved);
            return new ClipZipFileResult(archivePath,
                $"clips-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        }).RequireAuthorization()
            .Produces<byte[]>(StatusCodes.Status200OK, "application/zip")
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

    private static void DisposeUpstreams(
        IEnumerable<(string Id, string? Path, HttpResponseMessage? Upstream)> entries)
    {
        foreach (var entry in entries) entry.Upstream?.Dispose();
    }

    public sealed class ClipZipFileResult(string archivePath, string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                httpContext.Response.ContentType = "application/zip";
                httpContext.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"{fileName}\"";
                await using var stream = File.OpenRead(archivePath);
                await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
            }
            finally
            {
                try { File.Delete(archivePath); }
                catch (IOException) { }
            }
        }
    }
}
