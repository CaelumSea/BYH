using SelectionAssistant.Core.Capture;

namespace SelectionAssistant.Platform.Windows.Capture;

public static class WindowsDefaultCapturePolicies
{
    private static readonly string[] TerminalProcessNames =
    [
        "WindowsTerminal",
        "wt",
        "cmd",
        "powershell",
        "pwsh",
        "OpenConsole",
    ];

    private static readonly string[] PdfReaderProcessNames =
    [
        "Acrobat",
        "AcroRd32",
        "FoxitPDFReader",
        "FoxitReader",
    ];

    private static readonly string[] WarpProcessNames =
    [
        "warp",
    ];

    private static readonly string[] WeChatProcessNames =
    [
        "Weixin",
        "WeChatAppEx",
    ];

    public static void AddTo(ProcessPolicyResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var terminalPolicy = ProcessCapturePolicy.Default with
        {
            CopyMode = SimulatedCopyMode.CtrlInsertOnly,
        };
        foreach (string processName in TerminalProcessNames)
        {
            resolver.AddRule(new PolicyRule(
                PolicyMatchKind.ProcessName,
                processName,
                terminalPolicy));
        }

        // Warp renders terminal content in its own surface and does not use
        // Ctrl+Insert. On Windows its documented copy shortcut is
        // Ctrl+Shift+C; keep this rule separate from the legacy terminal
        // Ctrl+Insert policy so other terminals retain their safe behavior.
        var warpPolicy = ProcessCapturePolicy.Default with
        {
            CopyMode = SimulatedCopyMode.CtrlShiftCOnly,
            ClipboardStabilizationMs = 120,
            // Warp's GPU/WebView surface publishes one logical copy through
            // several clipboard transactions (observed sequence delta: 5).
            // Reserve enough history notifications to hide all of those
            // intermediate writes; restore uses its own single reservation.
            HistorySuppressionCount = 8,
        };
        foreach (string processName in WarpProcessNames)
        {
            resolver.AddRule(new PolicyRule(
                PolicyMatchKind.ProcessName,
                processName,
                warpPolicy));
        }

        // The new WeChat client hosts public-account content in a Chromium
        // child process. Ctrl+Insert is not handled consistently by that
        // surface (and can consume the selection before Ctrl+C arrives), so
        // use the native Ctrl+C path. The capture layer restores the user's
        // prior clipboard; explicit Ctrl+C and toolbar C remain the only paths
        // that intentionally create a new clipboard-history entry.
        var weChatPolicy = ProcessCapturePolicy.Default with
        {
            CopyMode = SimulatedCopyMode.CtrlCOnly,
        };
        foreach (string processName in WeChatProcessNames)
        {
            resolver.AddRule(new PolicyRule(
                PolicyMatchKind.ProcessName,
                processName,
                weChatPolicy));
        }

        var pdfPolicy = ProcessCapturePolicy.Default with
        {
            ClipboardStabilizationMs = 150,
        };
        foreach (string processName in PdfReaderProcessNames)
        {
            resolver.AddRule(new PolicyRule(
                PolicyMatchKind.ProcessName,
                processName,
                pdfPolicy));
        }
    }

    public static IProcessCapturePolicyProvider CreateProvider(
        IEnumerable<PolicyRule>? userRules = null)
    {
        var resolver = new ProcessPolicyResolver();
        AddTo(resolver);

        if (userRules is not null)
        {
            foreach (PolicyRule rule in userRules)
            {
                // User rules are appended, so they win within the same match tier.
                resolver.AddRule(rule);
            }
        }

        return new ProcessCapturePolicyProvider(
            resolver,
            new WindowsProcessIdentityResolver());
    }
}
