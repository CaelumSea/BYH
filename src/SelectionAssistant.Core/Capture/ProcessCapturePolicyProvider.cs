namespace SelectionAssistant.Core.Capture;

public sealed record ProcessIdentity(
    uint ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    string? BundleId = null,
    string? SignedIdentity = null,
    bool IsElevated = false);

public interface IProcessIdentityResolver
{
    bool IsCurrentProcessElevated { get; }

    ProcessIdentity Resolve(uint processId);
}

public interface IProcessCapturePolicyProvider
{
    ProcessCapturePolicy Resolve(uint processId);
}

/// <summary>Combines platform process metadata with ordered policy rules.</summary>
public sealed class ProcessCapturePolicyProvider : IProcessCapturePolicyProvider
{
    private readonly ProcessPolicyResolver _policyResolver;
    private readonly IProcessIdentityResolver _identityResolver;

    public ProcessCapturePolicyProvider(
        ProcessPolicyResolver policyResolver,
        IProcessIdentityResolver identityResolver)
    {
        _policyResolver = policyResolver ?? throw new ArgumentNullException(nameof(policyResolver));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
    }

    public ProcessCapturePolicy Resolve(uint processId)
    {
        ProcessIdentity identity = _identityResolver.Resolve(processId);
        ProcessCapturePolicy policy = _policyResolver.Resolve(
            identity.ProcessName,
            identity.ExecutablePath,
            identity.BundleId,
            identity.SignedIdentity);

        // UIPI prevents an unelevated process from injecting input into an
        // elevated source. Skip simulated copy instead of waiting for a timeout.
        if (identity.IsElevated && !_identityResolver.IsCurrentProcessElevated)
        {
            policy = policy with
            {
                CopyMode = SimulatedCopyMode.None,
                ManualFallbackEnabled = true,
            };
        }

        return policy.Validate();
    }
}
