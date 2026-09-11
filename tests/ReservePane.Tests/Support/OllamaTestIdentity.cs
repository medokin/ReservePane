using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet.Security;

namespace ReservePane.Tests.Support;

internal static class OllamaTestIdentity
{
    public static string Write(TemporaryDirectory directory)
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        using var key = new ED25519Key(seed);
        byte[] publicBlob = new KeyHostAlgorithm("ssh-ed25519", key).Data;
        using var privatePart = new MemoryStream();
        WriteInteger(privatePart, 1234);
        WriteInteger(privatePart, 1234);
        WriteBytes(privatePart, Encoding.ASCII.GetBytes("ssh-ed25519"));
        WriteBytes(privatePart, key.PublicKey);
        WriteBytes(privatePart, [.. seed, .. key.PublicKey]);
        WriteBytes(privatePart, []);
        for (byte padding = 1; privatePart.Length % 8 != 0; padding++)
        {
            privatePart.WriteByte(padding);
        }

        using var encoded = new MemoryStream();
        encoded.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        WriteBytes(encoded, Encoding.ASCII.GetBytes("none"));
        WriteBytes(encoded, Encoding.ASCII.GetBytes("none"));
        WriteBytes(encoded, []);
        WriteInteger(encoded, 1);
        WriteBytes(encoded, publicBlob);
        WriteBytes(encoded, privatePart.ToArray());
        return directory.WriteFile("ollama-test-identity",
            PemEncoding.WriteString("OPENSSH PRIVATE KEY", encoded.ToArray()));
    }

    private static void WriteBytes(Stream stream, byte[] value)
    {
        WriteInteger(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    private static void WriteInteger(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
