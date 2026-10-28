using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Athlos.Tenancy;
using Microsoft.Extensions.Options;

namespace Athlos.Enrollment.Api.Features.EmailPreferences;

public enum EmailTopic : byte
{
    ProgramUpdates = 1,
    ReviewReplies = 2,
    PriceDrops = 3,
}

// The token in an email's unsubscribe link (design §5.3, v17.9): the customer and the email, with an
// HMAC, so the link works without signing in and cannot be pointed at another customer. Its key is
// derived from the stage's gateway signing key (HKDF, its own label), so no new secret is needed; a
// link signed before a key rotation keeps working while the old key is still accepted, and afterwards
// the page sends the customer to their account instead. Links never expire otherwise.
public sealed class UnsubscribeTokens
{
    private const int MacLength = 16;
    private const int PayloadLength = 17;

    private static readonly byte[] Label = "athlos email unsubscribe v1"u8.ToArray();

    private readonly byte[] _key;
    private readonly byte[]? _alsoAccepted;

    public UnsubscribeTokens(IOptions<GatewaySignatureOptions> options)
    {
        _key = Derive(options.Value.Key);
        _alsoAccepted = string.IsNullOrEmpty(options.Value.AlsoAcceptKey) ? null : Derive(options.Value.AlsoAcceptKey);
    }

    public string Create(Guid customerId, EmailTopic topic)
    {
        var token = new byte[PayloadLength + MacLength];
        customerId.TryWriteBytes(token);
        token[16] = (byte)topic;
        Mac(_key, token.AsSpan(0, PayloadLength)).CopyTo(token.AsSpan(PayloadLength));
        return Base64Url.EncodeToString(token);
    }

    public bool TryRead(string token, out Guid customerId, out EmailTopic topic)
    {
        customerId = Guid.Empty;
        topic = default;
        var bytes = new byte[PayloadLength + MacLength];
        // TryDecodeFromChars throws on malformed input, so the shape is checked first.
        if (token.Length > 64 || !Base64Url.IsValid(token, out var length) || length != bytes.Length
            || !Base64Url.TryDecodeFromChars(token, bytes, out var written) || written != bytes.Length)
        {
            return false;
        }

        var payload = bytes.AsSpan(0, PayloadLength);
        var given = bytes.AsSpan(PayloadLength);
        if (!CryptographicOperations.FixedTimeEquals(Mac(_key, payload), given)
            && (_alsoAccepted is null || !CryptographicOperations.FixedTimeEquals(Mac(_alsoAccepted, payload), given)))
        {
            return false;
        }

        topic = (EmailTopic)payload[16];
        if (!Enum.IsDefined(topic))
        {
            return false;
        }

        customerId = new Guid(payload[..16]);
        return true;
    }

    private static byte[] Derive(string signingKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(signingKey), 32, salt: [], info: Label);

    private static byte[] Mac(byte[] key, ReadOnlySpan<byte> payload) => HMACSHA256.HashData(key, payload)[..MacLength];
}
