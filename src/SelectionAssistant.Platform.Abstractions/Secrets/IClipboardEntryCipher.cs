namespace SelectionAssistant.Platform.Abstractions.Secrets;

/// <summary>
/// R54 v2 Phase 2: encrypts/decrypts clipboard entry text at the JSON
/// serialization boundary (not in the in-memory model).
/// <para>
/// <b>Why boundary-only:</b> DPAPI (<see cref="System.Security.Cryptography.ProtectedData.Protect(byte[],byte[],System.Security.Cryptography.DataProtectionScope)"/>)
/// is intentionally non-deterministic — each call produces a different
/// ciphertext (random salt + MAC). Storing ciphertext as the entry's
/// <c>Text</c> would break exact-text dedup in
/// <c>ClipboardHistoryStore.AddAndEvict</c> and force every preview/paste to
/// decrypt. Instead the in-memory <c>ClipboardEntry.Text</c> is always
/// plaintext; only <c>clipboard-history.json</c> holds ciphertext for
/// <c>IsSensitive</c> entries.
/// </para>
/// <para>
/// Implementations MUST return null on any decryption failure (wrong account,
/// corrupt cipher, DPAPI unavailable) instead of throwing — the caller relies
/// on this to degrade to a placeholder rather than crashing.
/// </para>
/// </summary>
public interface IClipboardEntryCipher
{
    /// <summary>Encrypts plaintext into a self-delimiting string (typically
    /// base64). The same plaintext MAY produce different ciphertext on each
    /// call (non-deterministic).</summary>
    string Encrypt(string plaintext);

    /// <summary>Decrypts a value previously produced by <see cref="Encrypt"/>.
    /// Returns null on any failure — never throws.</summary>
    string? Decrypt(string ciphertext);
}
