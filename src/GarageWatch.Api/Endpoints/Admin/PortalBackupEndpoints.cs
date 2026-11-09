using GarageWatch.Api.Errors;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Maintenance;
using GarageWatch.Application.PlatformEvents;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class CreatePortalBackupEndpoint() : BaseEndpoint("Maintenance", "CreatePortalBackup",
    "Create a verified portal database backup",
    "Copies the portal SQLite database with VACUUM INTO, verifies integrity and returns size with SHA-256; the portal database never stores image, video or biometric embedding, and the action is audited.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/maintenance/backups", async (HttpContext context,
            IPortalBackupService backups, IPlatformEventRecorder recorder, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            PortalBackupArtifact artifact;
            try
            {
                artifact = await backups.CreateAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InvalidOperationException)
            {
                return Results.Json(new ApiErrorResponse("portal_backup_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            await recorder.RecordRequiredAsync(new PlatformEventDraft(
                "platform.backup.created", PlatformEventOutcomes.Success,
                Resource: new PlatformEventReferenceV1("portal_backup", artifact.FileName)), cancellationToken);

            return Results.Created($"/api/v1/admin/maintenance/backups/{artifact.FileName}", artifact);
        })
        .RequireAuthorization(PermissionPolicy.Name("admin.configuration.manage"))
        .Produces<PortalBackupArtifact>(StatusCodes.Status201Created)
        .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
        .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class ListPortalBackupsEndpoint() : BaseEndpoint("Maintenance", "ListPortalBackups",
    "List portal database backups",
    "Returns the newest verified backup artifacts with size and SHA-256 read from their manifests; it never opens or serves the database content.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/maintenance/backups", async (IPortalBackupService backups,
                CancellationToken cancellationToken) =>
            {
                var artifacts = await backups.ListAsync(cancellationToken);
                return Results.Ok(artifacts);
            })
            .RequireAuthorization(PermissionPolicy.Name("admin.configuration.manage"))
            .Produces<PortalBackupArtifact[]>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
