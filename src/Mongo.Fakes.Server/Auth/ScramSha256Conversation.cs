using System.Security.Cryptography;
using System.Text;
using Mongo.Fakes.Server.Errors;

namespace Mongo.Fakes.Server.Auth;

/// <summary>
/// Server side of a single SCRAM-SHA-256 (RFC 5802) conversation, verified against a single
/// fixed <see cref="ScramCredential"/> known to the fake server at construction time.
/// A real server (or a real client) never accepts an arbitrary password: the server-final
/// signature is a function of the salted password, so the fake must be told the credential
/// the driver will authenticate with rather than accepting anything presented to it.
/// </summary>
internal sealed class ScramSha256Conversation
{
    private const int Iterations = 4096;
    private const int KeyLength = 32;

    private static readonly byte[] ClientKeyLabel = Encoding.UTF8.GetBytes("Client Key");
    private static readonly byte[] ServerKeyLabel = Encoding.UTF8.GetBytes("Server Key");

    private readonly ScramCredential _credential;

    private string _clientFirstMessageBare = string.Empty;
    private string _serverFirstMessage = string.Empty;
    private string _combinedNonce = string.Empty;
    private byte[] _salt = [];
    private string _username = string.Empty;

    public ScramSha256Conversation(ScramCredential credential)
    {
        _credential = credential;
    }

    public byte[] ProcessClientFirst(byte[] clientFirstMessage)
    {
        string message = Encoding.UTF8.GetString(clientFirstMessage);

        int firstComma = message.IndexOf(',');
        int secondComma = firstComma < 0 ? -1 : message.IndexOf(',', firstComma + 1);
        if (secondComma < 0)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Malformed SCRAM client-first-message.");

        _clientFirstMessageBare = message[(secondComma + 1)..];

        var fields = ParseFields(_clientFirstMessageBare);
        if (!fields.TryGetValue('n', out var username) || !fields.TryGetValue('r', out var clientNonce))
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Malformed SCRAM client-first-message.");

        _username = Unescape(username);

        string serverNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        _combinedNonce = clientNonce + serverNonce;
        _salt = RandomNumberGenerator.GetBytes(16);

        _serverFirstMessage = $"r={_combinedNonce},s={Convert.ToBase64String(_salt)},i={Iterations}";
        return Encoding.UTF8.GetBytes(_serverFirstMessage);
    }

    public byte[] ProcessClientFinal(byte[] clientFinalMessage)
    {
        string message = Encoding.UTF8.GetString(clientFinalMessage);

        int proofIndex = message.LastIndexOf(",p=", StringComparison.Ordinal);
        if (proofIndex < 0)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Malformed SCRAM client-final-message.");

        string clientFinalMessageWithoutProof = message[..proofIndex];
        var fields = ParseFields(clientFinalMessageWithoutProof);

        if (!fields.TryGetValue('r', out var nonce) || nonce != _combinedNonce)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Authentication failed.");

        byte[] receivedProof = Convert.FromBase64String(message[(proofIndex + 3)..]);

        byte[] saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(_credential.Password), _salt, Iterations, HashAlgorithmName.SHA256, KeyLength);

        byte[] clientKey = HMACSHA256.HashData(saltedPassword, ClientKeyLabel);
        byte[] storedKey = SHA256.HashData(clientKey);

        string authMessage = $"{_clientFirstMessageBare},{_serverFirstMessage},{clientFinalMessageWithoutProof}";
        byte[] clientSignature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(authMessage));

        byte[] expectedProof = Xor(clientKey, clientSignature);

        bool proofValid = CryptographicOperations.FixedTimeEquals(expectedProof, receivedProof);
        bool usernameValid = _username == _credential.Username;

        if (!proofValid || !usernameValid)
            throw new MongoCommandException(ErrorCodes.AuthenticationFailed, "AuthenticationFailed", "Authentication failed.");

        byte[] serverKey = HMACSHA256.HashData(saltedPassword, ServerKeyLabel);
        byte[] serverSignature = HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(authMessage));

        return Encoding.UTF8.GetBytes($"v={Convert.ToBase64String(serverSignature)}");
    }

    private static string Unescape(string value) => value.Replace("=2C", ",").Replace("=3D", "=");

    private static Dictionary<char, string> ParseFields(string message)
    {
        var result = new Dictionary<char, string>();
        foreach (var part in message.Split(','))
        {
            if (part.Length >= 2 && part[1] == '=')
                result[part[0]] = part[2..];
        }

        return result;
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);

        return result;
    }
}
