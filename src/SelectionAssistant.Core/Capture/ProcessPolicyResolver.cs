using System.Collections.Generic;

namespace SelectionAssistant.Core.Capture;

/// <summary>
/// 进程策略解析器(v4 §6.6)。
/// 匹配优先级(显式,避免不可预测的覆盖):
///   1. 精确可执行路径 / macOS bundle id
///   2. 签名应用身份(可用时)
///   3. 进程名
///   4. 默认策略
/// </summary>
public sealed class ProcessPolicyResolver
{
    private readonly object _gate = new();
    private readonly List<PolicyRule> _rules = new();
    private readonly ProcessCapturePolicy _defaultPolicy;

    public ProcessPolicyResolver(ProcessCapturePolicy? defaultPolicy = null)
    {
        _defaultPolicy = (defaultPolicy ?? ProcessCapturePolicy.Default).Validate();
    }

    /// <summary>添加一条策略规则。相同匹配层级下，后添加者覆盖先添加者。</summary>
    public void AddRule(PolicyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.MatchValue))
        {
            throw new ArgumentException("A policy match value is required.", nameof(rule));
        }

        rule.Policy.Validate();
        lock (_gate)
        {
            _rules.Add(rule);
        }
    }

    /// <summary>解析给定进程的策略。按 v4 §6.6 的优先级顺序匹配。</summary>
    public ProcessCapturePolicy Resolve(
        string? processName,
        string? exePath,
        string? bundleId,
        string? signedIdentity = null)
    {
        PolicyRule[] rules;
        lock (_gate)
        {
            rules = _rules.ToArray();
        }

        // 1. 精确路径/bundle id
        for (int index = rules.Length - 1; index >= 0; index--)
        {
            PolicyRule rule = rules[index];
            if (rule.MatchKind == PolicyMatchKind.ExactPath &&
                !string.IsNullOrEmpty(exePath) &&
                rule.MatchValue.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                return rule.Policy;
            if (rule.MatchKind == PolicyMatchKind.BundleId &&
                !string.IsNullOrEmpty(bundleId) &&
                rule.MatchValue.Equals(bundleId, StringComparison.Ordinal))
                return rule.Policy;
        }

        // 2. 签名应用身份
        for (int index = rules.Length - 1; index >= 0; index--)
        {
            PolicyRule rule = rules[index];
            if (rule.MatchKind == PolicyMatchKind.SignedIdentity &&
                !string.IsNullOrEmpty(signedIdentity) &&
                rule.MatchValue.Equals(signedIdentity, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Policy;
            }
        }

        // 3. 进程名(放在默认前)
        string? normalizedProcessName = NormalizeProcessName(processName);
        for (int index = rules.Length - 1; index >= 0; index--)
        {
            PolicyRule rule = rules[index];
            string? normalizedRuleName = NormalizeProcessName(rule.MatchValue);
            if (rule.MatchKind == PolicyMatchKind.ProcessName &&
                normalizedProcessName is not null &&
                normalizedRuleName is not null &&
                normalizedRuleName.Equals(normalizedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Policy;
            }
        }

        // 4. 默认
        return _defaultPolicy;
    }

    private static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        string trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}

public sealed record PolicyRule(
    PolicyMatchKind MatchKind,
    string MatchValue,
    ProcessCapturePolicy Policy);

public enum PolicyMatchKind
{
    ExactPath,        // 精确可执行路径
    BundleId,         // macOS bundle id
    ProcessName,      // 进程名(如 "Acrobat.exe")
    SignedIdentity,   // 签名应用身份(预留)
}
