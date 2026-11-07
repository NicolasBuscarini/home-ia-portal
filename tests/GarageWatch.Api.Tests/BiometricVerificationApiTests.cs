using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Monitoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class BiometricVerificationApiTests
{
    private static readonly string TestKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task Verification_scores_the_same_face_and_rejects_a_different_one()
    {
        var biometrics = new VerificationBiometricClient();
        await using var factory = ConfiguredFactory(biometrics);
        using var client = factory.CreateClient();
        await LoginAsync(client, "contract-user", "Contract-Password-123!");

        using var created = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments", Body("same-face-image"));
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdText);
        using var createdBody = JsonDocument.Parse(createdText);
        var enrollmentId = createdBody.RootElement.GetProperty("publicId").GetString()!;

        using var matching = await client.PostAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/verify", VerifyBody("same-face-image"));
        var matchingText = await matching.Content.ReadAsStringAsync();
        Assert.True(matching.StatusCode == HttpStatusCode.OK, matchingText);
        Assert.DoesNotContain("image", matchingText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"values\"", matchingText, StringComparison.OrdinalIgnoreCase);
        using var matchingBody = JsonDocument.Parse(matchingText);
        Assert.True(matchingBody.RootElement.GetProperty("matches").GetBoolean());
        Assert.Equal(1, matchingBody.RootElement.GetProperty("score").GetDouble(), 6);
        Assert.Equal(0.363, matchingBody.RootElement.GetProperty("threshold").GetDouble(), 6);
        Assert.Equal("test-face-model", matchingBody.RootElement.GetProperty("modelId").GetString());

        using var different = await client.PostAsJsonAsync(
            $"/api/v1/admin/biometrics/enrollments/{enrollmentId}/verify", VerifyBody("x-other-face-image"));
        var differentText = await different.Content.ReadAsStringAsync();
        Assert.True(different.StatusCode == HttpStatusCode.OK, differentText);
        using var differentBody = JsonDocument.Parse(differentText);
        Assert.False(differentBody.RootElement.GetProperty("matches").GetBoolean());
        Assert.Equal(0, differentBody.RootElement.GetProperty("score").GetDouble(), 6);
    }

    [Fact]
    public async Task Verification_rejects_unknown_enrollment_invalid_image_and_missing_origin()
    {
        await using var factory = ConfiguredFactory(new VerificationBiometricClient());
        using var client = factory.CreateClient();
        await LoginAsync(client, "contract-user", "Contract-Password-123!");

        using var unknown = await client.PostAsJsonAsync(
            "/api/v1/admin/biometrics/enrollments/BIO000000000001/verify", VerifyBody("same-face-image"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var badBase64 = await client.PostAsJsonAsync(
            "/api/v1/admin/biometrics/enrollments/BIO000000000001/verify",
            VerifyBody("same-face-image") with { ImageBase64 = "not-base64!!" });
        Assert.Equal(HttpStatusCode.BadRequest, badBase64.StatusCode);

        using var badSize = await client.PostAsJsonAsync(
            "/api/v1/admin/biometrics/enrollments/BIO000000000001/verify",
            VerifyBody("same-face-image") with { Width = 6001 });
        Assert.Equal(HttpStatusCode.BadRequest, badSize.StatusCode);

        using var noOrigin = factory.CreateClient();
        await LoginAsync(noOrigin, "contract-user", "Contract-Password-123!");
        noOrigin.DefaultRequestHeaders.Remove("Origin");
        using var forbidden = await noOrigin.PostAsJsonAsync(
            "/api/v1/admin/biometrics/enrollments/BIO000000000001/verify", VerifyBody("same-face-image"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private static WebApplicationFactory<Program> ConfiguredFactory(VerificationBiometricClient biometrics) =>
        new ApiFactory(monitoringBiometricsSource: "remote").WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BIOMETRIC_ENCRYPTION_KEY"] = TestKey,
                    ["BIOMETRIC_ENCRYPTION_KEY_VERSION"] = "test-1"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFaceEnrollmentExtractor>();
                services.AddSingleton<IFaceEnrollmentExtractor, AlternatingExtractor>();
                services.RemoveAll<IMonitoringBiometricClient>();
                services.RemoveAll<IMonitoringBiometricCiphertextClient>();
                services.AddSingleton<IMonitoringBiometricClient>(biometrics);
                services.AddSingleton<IMonitoringBiometricCiphertextClient>(biometrics);
            });
        });

    private sealed record EnrollmentRequest(string? UserPublicId, string MediaType, int Width, int Height,
        string ImageBase64, string? ConsentVersion = BiometricPolicy.ConsentVersion);

    private static EnrollmentRequest Body(string image) => new(null, "image/jpeg", 160, 160,
        Convert.ToBase64String(Encoding.UTF8.GetBytes(image)));

    private sealed record VerifyRequest(string MediaType, int Width, int Height, string ImageBase64);

    private static VerifyRequest VerifyBody(string image) =>
        new("image/jpeg", 160, 160, Convert.ToBase64String(Encoding.UTF8.GetBytes(image)));

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    /// <summary>Even first byte maps to [1,0,0]; odd maps to [0,1,0], producing orthogonal test scores.</summary>
    private sealed class AlternatingExtractor : IFaceEnrollmentExtractor
    {
        public string ModelId => "test-face-model";
        public string ModelVersion => "contract-v1";
        public string AdapterId => "test-face-adapter";

        public ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!crop.IsValid) throw new FaceExtractionException("invalid_biometric_image");
            var even = crop.EncodedImage.Span[0] % 2 == 0;
            var values = even ? new[] { 1f, 0f, 0f } : new[] { 0f, 1f, 0f };
            return ValueTask.FromResult(new FaceEmbeddingV1(FaceEmbeddingV1.CurrentSchemaVersion,
                ModelId, ModelVersion, 3, values));
        }
    }

    private sealed class VerificationBiometricClient : IMonitoringBiometricClient,
        IMonitoringBiometricCiphertextClient
    {
        private readonly Dictionary<string, (MonitoringBiometricEnrollment Enrollment,
            MonitoringBiometricEnrollRequest Request)> _items = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<MonitoringBiometricEnrollment>> ListAsync(string userPublicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoringBiometricEnrollment>>(_items.Values
                .Select(item => item.Enrollment)
                .Where(item => item.UserPublicId == userPublicId).ToArray());

        public Task<MonitoringBiometricEnrollment> GetAsync(string publicId,
            CancellationToken cancellationToken = default) =>
            _items.TryGetValue(publicId, out var item)
                ? Task.FromResult(item.Enrollment)
                : throw new MonitoringBiometricsRejectedException("biometric_not_found");

        public Task<MonitoringBiometricEnrollmentResult> EnrollAsync(MonitoringBiometricEnrollRequest request,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var replaced = _items.Values
                .Where(item => item.Enrollment.UserPublicId == request.UserPublicId &&
                               item.Enrollment.Status == "active")
                .Select(item => item.Enrollment.PublicId).ToArray();
            foreach (var replacedId in replaced)
                _items[replacedId] = (_items[replacedId].Enrollment with
                {
                    Status = "revoked",
                    RevokedAtUtc = now,
                    RevokedReason = "replaced"
                }, _items[replacedId].Request);
            var publicId = $"BIO{_items.Count + 1:D12}";
            var enrollment = new MonitoringBiometricEnrollment(publicId, request.UserPublicId, "active", now,
                now.AddDays(BiometricPolicy.RetentionDays), null, "", request.ModelId, request.ModelVersion,
                request.Dimensions, request.KeyVersion, request.ConsentVersion, now);
            _items[publicId] = (enrollment, request);
            return Task.FromResult(new MonitoringBiometricEnrollmentResult(enrollment, replaced));
        }

        public Task RevokeAsync(string publicId, string reason, CancellationToken cancellationToken = default)
        {
            _items[publicId] = (_items[publicId].Enrollment with
            {
                Status = "revoked",
                RevokedAtUtc = DateTimeOffset.UtcNow,
                RevokedReason = reason
            }, _items[publicId].Request);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MonitoringBiometricCiphertext>> ListActiveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoringBiometricCiphertext>>(_items.Values
                .Where(item => item.Enrollment.Status == "active")
                .Select(item => new MonitoringBiometricCiphertext(item.Enrollment.PublicId,
                    item.Request.EmbeddingCiphertext, item.Request.EmbeddingNonce, item.Request.EmbeddingTag,
                    item.Request.KeyVersion, item.Request.Dimensions, item.Request.ModelId,
                    item.Request.ModelVersion, item.Request.EmbeddingSchemaVersion,
                    item.Enrollment.EnrolledAtUtc, item.Enrollment.ExpiresAtUtc))
                .ToArray());
    }
}
