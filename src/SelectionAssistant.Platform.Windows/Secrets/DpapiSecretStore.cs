using System.Security.Cryptography;
using System.Text;
using SelectionAssistant.Platform.Abstractions.Secrets;

namespace SelectionAssistant.Platform.Windows.Secrets;

/// <summary>
/// Windows DPAPI-backed secret store (§11.3). Secrets are encrypted with
/// <see cref="DataProtectionScope.CurrentUser" /> (tied to the Windows account,
/// unreadable by other users) and written as opaque blobs in a dedicated
/// directory. The filename is the SHA-256 of the reference, so the reference
/// string itself is not directly discoverable from the directory listing.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _secretsDirectory;

    public DpapiSecretStore(string secretsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);
        _secretsDirectory = secretsDirectory;
    }

    public Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        cancellationToken.ThrowIfCancellationRequested();

        string path = GetPath(reference);
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        byte[] cipher = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
    }

    public Task SetAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_secretsDirectory);

        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] cipher = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);

        string path = GetPath(reference);
        File.WriteAllBytes(path, cipher);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        cancellationToken.ThrowIfCancellationRequested();

        string path = GetPath(reference);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>Maps a reference string to an opaque filename (SHA-256 hex).</summary>
    private string GetPath(string reference)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(reference));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }
        return Path.Combine(_secretsDirectory, sb.ToString() + ".bin");
    }
}
