using GarageWatch.Application.Analysis;
using GarageWatch.Application.Identity;

namespace GarageWatch.Application.Biometrics;

/// <summary>Result of a gesture-confirmed 1:1 comparison; it never includes embeddings or image data.</summary>
public sealed record BiometricVerificationResult(bool Matches, double Score, double Threshold,
    string ModelId, string ModelVersion, string EmbeddingSchemaVersion, int Dimensions);

/// <summary>The comparison cannot run because a model, store or key required by it is unavailable.</summary>
public sealed class BiometricVerificationUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Compares a fresh photo with one active enrollment before linking it to a roster person. Extraction,
/// decryption and scoring stay in memory; the image and both embeddings are discarded after the call.
/// </summary>
public sealed class BiometricVerificationService(IEnumerable<IFaceEnrollmentExtractor> extractors,
    IIdentityEmbeddingSource embeddings, IIdentityMatchPolicySource policies)
{
    public async Task<BiometricVerificationResult> VerifyAsync(string enrollmentPublicId, FaceCropV1 crop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crop);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentPublicId);
        if (!crop.IsValid) throw new FaceExtractionException("invalid_biometric_image");

        var extractor = extractors.FirstOrDefault()
            ?? throw new BiometricVerificationUnavailableException("face_extractor_not_configured");
        var embedding = await extractor.ExtractAsync(crop, cancellationToken);
        var enrollment = await embeddings.LoadEnrollmentAsync(enrollmentPublicId, cancellationToken)
            ?? throw new KeyNotFoundException("biometric_enrollment_not_found");

        float[]? enrollmentValues = enrollment.Values;
        try
        {
            if (!string.Equals(embedding.ModelId, enrollment.ModelId, StringComparison.Ordinal) ||
                !string.Equals(embedding.ModelVersion, enrollment.ModelVersion, StringComparison.Ordinal) ||
                !string.Equals(embedding.SchemaVersion, enrollment.SchemaVersion, StringComparison.Ordinal) ||
                embedding.Dimensions != enrollment.Dimensions ||
                enrollmentValues.Length != enrollment.Dimensions)
                throw new BiometricVerificationUnavailableException("biometric_enrollment_model_mismatch");

            var score = IdentitySimilarity.Cosine(embedding.Values, enrollmentValues);
            if (!double.IsFinite(score))
                throw new BiometricVerificationUnavailableException("biometric_verification_failed");
            var policy = await policies.GetActiveAsync(cancellationToken);
            return new BiometricVerificationResult(score >= policy.Threshold, score, policy.Threshold,
                enrollment.ModelId, enrollment.ModelVersion, enrollment.SchemaVersion, enrollment.Dimensions);
        }
        finally
        {
            Array.Clear(enrollmentValues);
            if (embedding.Values is float[] extracted) Array.Clear(extracted);
        }
    }
}
