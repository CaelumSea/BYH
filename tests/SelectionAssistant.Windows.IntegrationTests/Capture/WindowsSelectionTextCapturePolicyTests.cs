using SelectionAssistant.Core.Capture;
using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Windows.Capture;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Capture;

public sealed class WindowsSelectionTextCapturePolicyTests
{
    [Fact]
    public async Task DetectionDisabled_SkipsEveryCaptureBackend()
    {
        var accessibility = new FakeAccessibilityCapture(Result("uia", CaptureSource.Accessibility));
        var clipboard = new FakeClipboardCapture(Result("clipboard", CaptureSource.SimulatedCopyCtrlC));
        using var capture = Create(
            ProcessCapturePolicy.Default with { DetectionEnabled = false },
            accessibility,
            clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal(CaptureSource.None, result.Source);
        Assert.Equal(0, accessibility.CallCount);
        Assert.Equal(0, clipboard.CallCount);
    }

    [Fact]
    public async Task ReliableAccessibilityResult_PreventsSimulatedCopy()
    {
        var accessibility = new FakeAccessibilityCapture(Result("selected", CaptureSource.Accessibility));
        var clipboard = new FakeClipboardCapture(Result("clipboard", CaptureSource.SimulatedCopyCtrlC));
        using var capture = Create(ProcessCapturePolicy.Default, accessibility, clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal(CaptureSource.Accessibility, result.Source);
        Assert.Equal(0, clipboard.CallCount);
    }

    [Fact]
    public async Task TerminalPolicy_UsesOnlyCtrlInsertAndCustomStabilization()
    {
        var accessibility = new FakeAccessibilityCapture(Result(null, CaptureSource.None));
        var clipboard = new FakeClipboardCapture(Result("terminal", CaptureSource.SimulatedCopyCtrlInsert));
        ProcessCapturePolicy policy = ProcessCapturePolicy.Default with
        {
            AccessibilityEnabled = false,
            CopyMode = SimulatedCopyMode.CtrlInsertOnly,
            ClipboardStabilizationMs = 180,
        };
        using var capture = Create(policy, accessibility, clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("terminal", result.Text);
        Assert.Equal([SimulatedCopyChord.CtrlInsert], clipboard.LastInvocation?.Chords);
        Assert.Equal(TimeSpan.FromMilliseconds(180), clipboard.LastInvocation?.StabilizationDelay);
        Assert.Equal(0, accessibility.CallCount);
    }

    [Fact]
    public async Task WarpPolicy_UsesOnlyCtrlShiftCAndRestoresClipboard()
    {
        var accessibility = new FakeAccessibilityCapture(Result(null, CaptureSource.None));
        var clipboard = new FakeClipboardCapture(
            Result("warp", CaptureSource.SimulatedCopyCtrlShiftC));
        ProcessCapturePolicy policy = ProcessCapturePolicy.Default with
        {
            AccessibilityEnabled = false,
            CopyMode = SimulatedCopyMode.CtrlShiftCOnly,
            ClipboardStabilizationMs = 120,
        };
        using var capture = Create(policy, accessibility, clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("warp", result.Text);
        Assert.Equal(
            [SimulatedCopyChord.CtrlShiftC],
            clipboard.LastInvocation?.Chords);
        Assert.Equal(TimeSpan.FromMilliseconds(120), clipboard.LastInvocation?.StabilizationDelay);
        Assert.False(clipboard.LastInvocation?.PreserveCapturedClipboard ?? false);
        Assert.Equal(0, accessibility.CallCount);
    }

    [Fact]
    public async Task WeChatPolicy_UsesCtrlCWithoutPreservingCapturedClipboard()
    {
        var accessibility = new FakeAccessibilityCapture(Result(null, CaptureSource.None));
        var clipboard = new FakeClipboardCapture(
            Result("wechat", CaptureSource.SimulatedCopyCtrlC));
        ProcessCapturePolicy policy = ProcessCapturePolicy.Default with
        {
            AccessibilityEnabled = false,
            CopyMode = SimulatedCopyMode.CtrlCOnly,
        };
        using var capture = Create(policy, accessibility, clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("wechat", result.Text);
        Assert.Equal([SimulatedCopyChord.CtrlC], clipboard.LastInvocation?.Chords);
        Assert.False(clipboard.LastInvocation?.PreserveCapturedClipboard ?? false);
    }

    [Fact]
    public async Task AmbiguousAccessibilityWithoutCopy_ReturnsManualFallbackNotText()
    {
        var accessibility = new FakeAccessibilityCapture(
            new CaptureResult("entire control", CaptureSource.Accessibility, true));
        var clipboard = new FakeClipboardCapture(Result("must not run", CaptureSource.SimulatedCopyCtrlC));
        ProcessCapturePolicy policy = ProcessCapturePolicy.Default with
        {
            CopyMode = SimulatedCopyMode.None,
            ManualFallbackEnabled = true,
        };
        using var capture = Create(policy, accessibility, clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(CaptureSource.ManualFallback, result.Source);
        Assert.Equal(0, clipboard.CallCount);
    }

    [Fact]
    public async Task ManualFallbackDisabled_ReturnsNoneAfterAllAutomaticTiersFail()
    {
        var accessibility = new FakeAccessibilityCapture(Result(null, CaptureSource.None));
        var clipboard = new FakeClipboardCapture(Result(null, CaptureSource.None));
        ProcessCapturePolicy policy = ProcessCapturePolicy.Default with
        {
            ManualFallbackEnabled = false,
        };
        using var capture = Create(policy, accessibility, clipboard);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(CaptureSource.None, result.Source);
        Assert.Equal(
            [SimulatedCopyChord.CtrlInsert, SimulatedCopyChord.CtrlC],
            clipboard.LastInvocation?.Chords);
    }

    [Theory]
    [InlineData("powershell.exe", SimulatedCopyMode.CtrlInsertOnly, 0)]
    [InlineData("AcroRd32.exe", SimulatedCopyMode.CtrlInsertThenCtrlC, 150)]
    [InlineData("warp.exe", SimulatedCopyMode.CtrlShiftCOnly, 120)]
    [InlineData("Weixin.exe", SimulatedCopyMode.CtrlCOnly, 0)]
    [InlineData("WeChatAppEx.exe", SimulatedCopyMode.CtrlCOnly, 0)]
    public void WindowsDefaults_ResolveExpectedApplicationPolicy(
        string processName,
        SimulatedCopyMode expectedMode,
        int expectedStabilization)
    {
        var resolver = new ProcessPolicyResolver();
        WindowsDefaultCapturePolicies.AddTo(resolver);

        ProcessCapturePolicy result = resolver.Resolve(processName, null, null);

        Assert.Equal(expectedMode, result.CopyMode);
        Assert.Equal(expectedStabilization, result.ClipboardStabilizationMs);
    }

    [Fact]
    public void WindowsDefaults_RestoreClipboardAfterSelectionCapture()
    {
        var resolver = new ProcessPolicyResolver();
        WindowsDefaultCapturePolicies.AddTo(resolver);

        Assert.False(resolver.Resolve("warp", null, null).PreserveCapturedClipboard);
        Assert.False(resolver.Resolve("Weixin", null, null).PreserveCapturedClipboard);
        Assert.False(resolver.Resolve("WeChatAppEx", null, null).PreserveCapturedClipboard);
    }

    [Fact]
    public void UserRuleOverridesWindowsDefaultAtSameTier()
    {
        var resolver = new ProcessPolicyResolver();
        WindowsDefaultCapturePolicies.AddTo(resolver);
        ProcessCapturePolicy userPolicy = ProcessCapturePolicy.Default with
        {
            DetectionEnabled = false,
        };
        resolver.AddRule(new PolicyRule(PolicyMatchKind.ProcessName, "cmd.exe", userPolicy));

        ProcessCapturePolicy result = resolver.Resolve("cmd", null, null);

        Assert.Same(userPolicy, result);
    }

    [Fact]
    public void WindowsIdentityResolver_ReadsCurrentProcessWithoutFailure()
    {
        var resolver = new WindowsProcessIdentityResolver();

        ProcessIdentity identity = resolver.Resolve((uint)Environment.ProcessId);

        Assert.Equal((uint)Environment.ProcessId, identity.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(identity.ProcessName));
        Assert.False(string.IsNullOrWhiteSpace(identity.ExecutablePath));
    }

    private static WindowsSelectionTextCapture Create(
        ProcessCapturePolicy policy,
        ISelectionTextCapture accessibility,
        IConfiguredClipboardCapture clipboard) => new(
            new FixedPolicyProvider(policy),
            accessibility,
            clipboard);

    private static SelectionGesture Gesture() => new(
        MouseUpX: 1,
        MouseUpY: 1,
        MouseDownX: 0,
        MouseDownY: 0,
        MouseDownTimestampMs: 1,
        MouseUpTimestampMs: 2,
        SourceRootHwnd: 1,
        SourceProcessId: 42);

    private static CaptureResult Result(string? text, CaptureSource source) =>
        new(text, source, false);

    private sealed class FixedPolicyProvider(ProcessCapturePolicy policy)
        : IProcessCapturePolicyProvider
    {
        public ProcessCapturePolicy Resolve(uint processId) => policy;
    }

    private sealed class FakeAccessibilityCapture(CaptureResult result) : ISelectionTextCapture
    {
        public int CallCount { get; private set; }

        public Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeClipboardCapture(CaptureResult result) : IConfiguredClipboardCapture
    {
        public int CallCount { get; private set; }

        public ClipboardCaptureInvocation? LastInvocation { get; private set; }

        public Task<CaptureResult> CaptureAsync(
            SelectionGesture gesture,
            ClipboardCaptureInvocation invocation,
            CancellationToken token)
        {
            CallCount++;
            LastInvocation = invocation;
            return Task.FromResult(result);
        }
    }
}
