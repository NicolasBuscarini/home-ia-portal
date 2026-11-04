using System.Buffers.Binary;

namespace GarageWatch.Application.Biometrics;

/// <summary>Canonical little-endian float encoding used for encrypted face embeddings.</summary>
public static class BiometricEmbeddingCodec
{
    public static byte[] Serialize(IReadOnlyList<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);
        return bytes;
    }

    public static float[]? Deserialize(ReadOnlySpan<byte> bytes, int dimensions)
    {
        if (dimensions is < 1 or > 4096 || bytes.Length != dimensions * sizeof(float)) return null;
        var values = new float[dimensions];
        for (var index = 0; index < dimensions; index++)
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(index * sizeof(float), sizeof(float)));
        return values;
    }
}
