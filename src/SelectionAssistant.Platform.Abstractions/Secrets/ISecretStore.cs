namespace SelectionAssistant.Platform.Abstractions.Secrets;

/// <summary>
/// Platform-agnostic secret storage. Secrets (API keys, bearer tokens) are
/// addressed by a <c>secret://</c>-style reference string and stored in a
/// platform-native secure store — never in plaintext config (§11.3).
/// Implementations must encrypt at rest (Windows: DPAPI / Credential Manager).
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret for the given reference, or null if not set.</summary>
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>Stores (overwriting) the secret for the given reference.</summary>
    Task SetAsync(string reference, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes the secret for the given reference if it exists.</summary>
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
