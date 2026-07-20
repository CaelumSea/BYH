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
