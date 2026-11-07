using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Monitoring;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipAnalysisResultEndpoint() : BaseEndpoint("Clip analysis", "GetClipAnalysisResult",
    "Get clip analysis result",
    "Returns the stored detection summary only after the current clips.read camera grant is checked.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/{publicId}/analysis-result", async (string publicId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, IMonitoringAnalysisClient analysis,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var admin = await users.IsInRoleAsync(user, "Administrator");
            var clip = await library.GetAsync(user.Id, admin, publicId, cancellationToken);
            if (clip.Details is null) return Results.NotFound();
            MonitoringAnalysisResult? result;
            try
            {
                result = await analysis.GetResultAsync(publicId, cancellationToken);
            }
            catch (MonitoringAnalysisUnavailableException)
            {
                return Results.Json(new ApiErrorResponse("analysis_result_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (result is null) return Results.NotFound(new ApiErrorResponse("analysis_result_not_found"));
            return Results.Ok(new ClipAnalysisResultResponse(result.ClipPublicId, result.AnalysisVersion,
                result.ModelVersion, result.FramesAnalyzed, result.ProcessedAtUtc,
                result.Objects.Select(item => new ClipAnalysisObjectResponse(item.ClassName, item.ClassId,
                    item.Score, item.FrameIndex)).ToArray()));
        }).RequireAuthorization().Produces<ClipAnalysisResultResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
