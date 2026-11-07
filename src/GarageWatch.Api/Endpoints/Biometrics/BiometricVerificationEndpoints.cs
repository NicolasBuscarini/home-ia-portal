using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Biometrics;

/// <summary>
/// Compares a fresh photo with one encrypted enrollment so an administrator can confirm whose face it is
/// before linking it to a roster person. The photo, embeddings and score are not persisted.
/// </summary>
public sealed class VerifyBiometricEnrollmentEndpoint() : BaseEndpoint("Biometrics", "VerifyBiometricEnrollment",
    "Verify a face against an enrollment",
    "Extracts one face from an uploaded image and compares it 1:1 with an active encrypted enrollment; returns only a score, the active threshold and model metadata, and stores nothing.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/biometrics/enrollments/{enrollmentId}/verify",
            async (string enrollmentId, VerifyBiometricEnrollmentRequest request, HttpContext context,
                ClaimsPrincipal principal, UserManager<ApplicationUser> users,
                BiometricVerificationService verification, CancellationToken cancellationToken) =>
            {
                if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var actor = await users.GetUserAsync(principal);
                if (actor is null) return Results.Unauthorized();
                FaceCropV1 crop;
                try
                {
                    crop = ToCrop(request);
                }
                catch (FormatException)
                {
                    return Results.BadRequest(new ApiErrorResponse("invalid_biometric_image"));
                }
                if (!crop.IsValid) return Results.BadRequest(new ApiErrorResponse("invalid_biometric_image"));
                try
                {
                    var result = await verification.VerifyAsync(enrollmentId, crop, cancellationToken);
                    return Results.Ok(new BiometricVerificationResponse(result.Matches, result.Score,
                        result.Threshold, result.ModelId, result.ModelVersion, result.EmbeddingSchemaVersion,
                        result.Dimensions));
                }
                catch (FaceExtractionException exception)
                {
                    return Results.BadRequest(new ApiErrorResponse(exception.Code));
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new ApiErrorResponse("biometric_enrollment_not_found"));
                }
                catch (BiometricVerificationUnavailableException exception)
                {
                    return Results.Json(new ApiErrorResponse(exception.Code),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (IdentityMatchingUnavailableException exception)
                {
                    return Results.Json(new ApiErrorResponse(exception.Message),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }).RequireAuthorization(PermissionPolicy.Name("biometrics.manage"))
            .Produces<BiometricVerificationResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);

    private static FaceCropV1 ToCrop(VerifyBiometricEnrollmentRequest request)
    {
        var mediaType = request.MediaType?.Trim();
        if (mediaType is not ("image/jpeg" or "image/png")) throw new FormatException();
        if (string.IsNullOrWhiteSpace(request.ImageBase64)) throw new FormatException();
        var encoded = Convert.FromBase64String(request.ImageBase64);
        return new FaceCropV1(mediaType, request.Width ?? 0, request.Height ?? 0, encoded);
    }
}
