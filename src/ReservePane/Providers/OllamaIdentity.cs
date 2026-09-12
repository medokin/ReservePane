using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace ReservePane.Providers;

internal sealed class OllamaIdentity : IDisposable
{
    private const int MaximumKeyBytes = 65_536;
    private readonly PrivateKeyFile _key;
    private readonly byte[] _publicKey;

    private OllamaIdentity(PrivateKeyFile key)
    {
        _key = key;
        _publicKey = key.HostKeyAlgorithms.Single().Data;
        RetentionKey = Convert.ToHexString(SHA256.HashData(_publicKey));
    }

    public string RetentionKey { get; }

    public static OllamaIdentity Read(string path)
    {
        using Stream source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        byte[] bytes = new byte[MaximumKeyBytes + 1];
        try
        {
            int count = source.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > MaximumKeyBytes)
            {
                throw new InvalidDataException("Ollama identity exceeds the size limit.");
            }

            using var encoded = new MemoryStream(bytes, 0, count, writable: false);
            var key = new PrivateKeyFile(encoded);
            if (key.Key is not ED25519Key)
            {
                key.Dispose();
                throw new InvalidDataException("Ollama identity must use Ed25519.");
            }

            return new OllamaIdentity(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public HttpRequestMessage CreateAccountRequest(DateTimeOffset now) =>
        CreateRequest(HttpMethod.Post, "/api/me", now);

    public HttpRequestMessage CreateUsageRequest(DateTimeOffset now) =>
        CreateRequest(HttpMethod.Get, "/api/usage", now);

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint, DateTimeOffset now)
    {
        string path = endpoint + "?ts=" + now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        byte[] signature = _key.Key.Sign(Encoding.UTF8.GetBytes(method.Method + "," + path));
        string authorization = Convert.ToBase64String(_publicKey) + ":" + Convert.ToBase64String(signature);
        var request = new HttpRequestMessage(method, new Uri("https://ollama.com" + path));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    public void Dispose() => _key.Dispose();
}
