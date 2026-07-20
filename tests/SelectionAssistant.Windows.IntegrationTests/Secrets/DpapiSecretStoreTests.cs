using SelectionAssistant.Platform.Windows.Secrets;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Secrets;

public sealed class DpapiSecretStoreTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "byh-secrets-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SetThenGet_RoundTripsValueExactly()
    {
        string dir = TempDir();
        try
        {
            var store = new DpapiSecretStore(dir);
            const string reference = "secret://provider/test";
            const string value = "sk-test-key-1234567890";

            await store.SetAsync(reference, value);
            string? retrieved = await store.GetAsync(reference);

            Assert.Equal(value, retrieved);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_NonExistentReference_ReturnsNull()
    {
        string dir = TempDir();
        try
        {
            var store = new DpapiSecretStore(dir);
            string? result = await store.GetAsync("secret://provider/never-set");
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesSecret()
    {
        string dir = TempDir();
        try
        {
            var store = new DpapiSecretStore(dir);
            const string reference = "secret://provider/deletable";

            await store.SetAsync(reference, "to-be-deleted");
            await store.DeleteAsync(reference);
            string? result = await store.GetAsync(reference);

            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        string dir = TempDir();
        try
        {
            var store = new DpapiSecretStore(dir);
            const string reference = "secret://provider/overwrite";

            await store.SetAsync(reference, "first");
            await store.SetAsync(reference, "second");
            string? result = await store.GetAsync(reference);

            Assert.Equal("second", result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SetAsync_WritesEncryptedBlob_NotPlaintext()
    {
        // The blob on disk must NOT contain the plaintext key — DPAPI must have
        // encrypted it. This guards against accidentally writing plaintext.
        string dir = TempDir();
        try
        {
            var store = new DpapiSecretStore(dir);
            const string reference = "secret://provider/plaintext-check";
            const string value = "UNIQUE_PLAINTEXT_MARKER_9876543210";

            await store.SetAsync(reference, value);

            string[] files = Directory.GetFiles(dir);
            Assert.Single(files);
            byte[] blob = await File.ReadAllBytesAsync(files[0]);
            string blobText = System.Text.Encoding.UTF8.GetString(blob);

            Assert.DoesNotContain("UNIQUE_PLAINTEXT_MARKER", blobText);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
