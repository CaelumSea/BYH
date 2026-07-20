using SelectionAssistant.Core.Capture;
using Xunit;

namespace SelectionAssistant.Core.Tests.Capture;

public sealed class ProcessPolicyResolverTests
{
    [Fact]
    public void Resolve_UsesExplicitMatchTierPrecedence()
    {
        var resolver = new ProcessPolicyResolver();
        ProcessCapturePolicy byName = Policy(SimulatedCopyMode.CtrlInsertOnly);
        ProcessCapturePolicy bySignature = Policy(SimulatedCopyMode.None);
        ProcessCapturePolicy byPath = Policy(SimulatedCopyMode.CtrlInsertThenCtrlC, 175);
        resolver.AddRule(new PolicyRule(PolicyMatchKind.ExactPath, @"C:\Apps\Reader.exe", byPath));
        resolver.AddRule(new PolicyRule(PolicyMatchKind.ProcessName, "Reader.exe", byName));
        resolver.AddRule(new PolicyRule(PolicyMatchKind.SignedIdentity, "Publisher.Reader", bySignature));

        ProcessCapturePolicy result = resolver.Resolve(
            "Reader",
            @"C:\Apps\Reader.exe",
            null,
            "Publisher.Reader");

        Assert.Same(byPath, result);
    }

    [Fact]
    public void Resolve_LaterRuleOverridesBuiltInAtSameTier()
    {
        var resolver = new ProcessPolicyResolver();
        ProcessCapturePolicy builtIn = Policy(SimulatedCopyMode.CtrlInsertOnly);
        ProcessCapturePolicy user = Policy(SimulatedCopyMode.None);
        resolver.AddRule(new PolicyRule(PolicyMatchKind.ProcessName, "pwsh", builtIn));
        resolver.AddRule(new PolicyRule(PolicyMatchKind.ProcessName, "pwsh.exe", user));

        ProcessCapturePolicy result = resolver.Resolve("pwsh", null, null);

        Assert.Same(user, result);
    }

    [Fact]
    public void Resolve_ReturnsConfiguredDefaultWhenNoRuleMatches()
    {
        ProcessCapturePolicy fallback = Policy(SimulatedCopyMode.None);
        var resolver = new ProcessPolicyResolver(fallback);

        ProcessCapturePolicy result = resolver.Resolve("unknown", null, null);

        Assert.Same(fallback, result);
    }

    [Fact]
    public void Provider_DisablesInputInjectionAcrossElevationBoundary()
    {
        var resolver = new ProcessPolicyResolver();
        var identity = new FakeIdentityResolver(
            currentElevated: false,
            new ProcessIdentity(42, "admin-app", null, IsElevated: true));
        var provider = new ProcessCapturePolicyProvider(resolver, identity);

        ProcessCapturePolicy result = provider.Resolve(42);

        Assert.Equal(SimulatedCopyMode.None, result.CopyMode);
        Assert.True(result.ManualFallbackEnabled);
        Assert.True(result.AccessibilityEnabled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void Validate_RejectsUnsafeStabilizationDurations(int milliseconds)
    {
        ProcessCapturePolicy policy = ProcessCapturePolicy.Default with
        {
            ClipboardStabilizationMs = milliseconds,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
    }

    private static ProcessCapturePolicy Policy(
        SimulatedCopyMode copyMode,
        int stabilization = 0) => ProcessCapturePolicy.Default with
    {
        CopyMode = copyMode,
        ClipboardStabilizationMs = stabilization,
    };

    private sealed class FakeIdentityResolver(
        bool currentElevated,
        ProcessIdentity identity) : IProcessIdentityResolver
    {
        public bool IsCurrentProcessElevated { get; } = currentElevated;

        public ProcessIdentity Resolve(uint processId) => identity;
    }
}
