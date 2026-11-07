namespace GarageWatch.Application.Identity;

/// <summary>
/// Cosine similarity over decrypted, same-model embeddings. It performs no I/O, never logs values and
/// returns <see cref="double.NaN"/> for empty, mismatched or zero-norm vectors.
/// </summary>
public static class IdentitySimilarity
{
    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Count != right.Count || left.Count == 0) return double.NaN;
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            dot += a * b;
            leftNorm += a * a;
            rightNorm += b * b;
        }
        if (leftNorm == 0 || rightNorm == 0) return double.NaN;
        var score = dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        return Math.Clamp(score, -1, 1);
    }
}
