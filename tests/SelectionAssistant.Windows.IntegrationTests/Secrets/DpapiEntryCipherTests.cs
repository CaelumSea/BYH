using SelectionAssistant.Platform.Windows.Secrets;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Secrets;

/// <summary>
/// Integration tests for <see cref="DpapiEntryCipher"/> — exercises real
/// Windows DPAPI (CurrentUser scope), so these run only on Windows and produce
/// ciphertext that is only decryptable in this account. Sibling to
/// <see cref="DpapiSecretStoreTests"/>.
/// </summary>
public sealed class DpapiEntryCipherTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginalText()
    {
        var cipher = new DpapiEntryCipher();
        const string secret = "api_key=sk-1234567890abcdef";

        string encrypted = cipher.Encrypt(secret);
        string? decrypted = cipher.Decrypt(encrypted);

        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void Encrypt_NonDeterministic_DifferentCiphertextEachCall()
    {
        // DPAPI uses a random salt per call, so encrypting the same plaintext
        // twice must yield different ciphertext. This is the property that
        // forces encryption to live at the serialization boundary rather than
        // in the in-memory entry (ciphertext can't be used for dedup).
        var cipher = new DpapiEntryCipher();
        const string secret = "password=hunter2";

        string first = cipher.Encrypt(secret);
        string second = cipher.Encrypt(secret);

        Assert.NotEqual(first, second);
        // Both must still decrypt back to the same plaintext.
        Assert.Equal(secret, cipher.Decrypt(first));
        Assert.Equal(secret, cipher.Decrypt(second));
    }

    [Theory]
    [InlineData("")]          // empty
    [InlineData("not-base64!@#")] // invalid base64 chars
    public void Decrypt_InvalidInput_ReturnsNull(string input)
    {
        var cipher = new DpapiEntryCipher();
        Assert.Null(cipher.Decrypt(input));
    }

    [Fact]
    public void Decrypt_CorruptCipher_ReturnsNull()
    {
        // Valid base64 but not a real DPAPI blob — Unprotect throws
        // CryptographicException, which Decrypt must swallow into null.
        var cipher = new DpapiEntryCipher();
        string corrupt = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Null(cipher.Decrypt(corrupt));
    }

    [Fact]
    public void Encrypt_OutputIsBase64_AndDoesNotContainPlaintext()
    {
        // Guard against a regression that stores plaintext: the ciphertext must
        // be valid base64 and must not contain the secret string verbatim.
        var cipher = new DpapiEntryCipher();
        const string marker = "UNIQUE_SECRET_MARKER_9876543210";

        string encrypted = cipher.Encrypt(marker);

        // Round-trips back, proving it's a real (not random) encoding.
        Assert.Equal(marker, cipher.Decrypt(encrypted));
        // Convert succeeds → valid base64.
        _ = Convert.FromBase64String(encrypted);
        // The marker must not appear in the ciphertext.
        Assert.DoesNotContain(marker, encrypted);
    }
}
