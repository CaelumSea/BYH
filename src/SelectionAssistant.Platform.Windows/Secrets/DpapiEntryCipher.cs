using System.Security.Cryptography;
using System.Text;
using SelectionAssistant.Platform.Abstractions.Secrets;

namespace SelectionAssistant.Platform.Windows.Secrets;

/// <summary>
/// R54 v2 Phase 2: DPAPI-backed <see cref="IClipboardEntryCipher"/>. Mirrors
/// <see cref="DpapiSecretStore"/>'s use of <see cref="ProtectedData"/> with
/// <see cref="DataProtectionScope.CurrentUser"/> (bound to the Windows account,
/// unreadable by other users / on other machines).
/// <para>
/// Ciphertext is base64-encoded so it can sit in a JSON string field.
/// <see cref="Decrypt"/> swallows all failures and returns null — the caller
/// replaces a failed entry's text with a placeholder rather than crashing.
/// </para>
/// <para>
/// <b>Non-deterministic:</b> two <see cref="Encrypt"/> calls with the same
/// plaintext produce different ciphertext (DPAPI uses a random salt). This is
/// by design and is why encryption lives at the serialization boundary, not in
/// the in-memory entry.
/// </para>
/// </summary>
public sealed class DpapiEntryCipher : IClipboardEntryCipher
{
    /// <summary>Encrypts <paramref name="plaintext"/> via DPAPI and base64-encodes
    /// the result. Never returns null.</summary>
    public string Encrypt(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = ProtectedData.Protect(
            plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Decrypts base64 DPAPI ciphertext. Returns null on any failure
    /// (bad base64, corrupt cipher, wrong account, DPAPI unavailable).</summary>
    public string? Decrypt(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return null;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(ciphertext);
            byte[] plain = ProtectedData.Unprotect(
                cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null; // invalid base64
        }
        catch (CryptographicException)
        {
            return null; // wrong account / corrupt / DPAPI failure
        }
    }
}
