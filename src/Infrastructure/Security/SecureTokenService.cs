using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Security;

public sealed class SecureTokenService : ISecureTokenService
{
    private const int TokenBytes = 32;

    public string CreateToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    public byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
