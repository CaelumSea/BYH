using System.Text.Json;
using SelectionAssistant.Core.Capture;

namespace SelectionAssistant.Infrastructure.Capture;

public static class CapturePolicyConfigurationLoader
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFileBytes = 256 * 1024;
    public const int MaximumRules = 256;

    public static IReadOnlyList<PolicyRule> LoadIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return [];
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumFileBytes)
        {
            throw new CapturePolicyConfigurationException("策略文件超过 256 KB 上限。");
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
            {
                throw new CapturePolicyConfigurationException("不支持的策略 schemaVersion。");
            }

            if (!root.TryGetProperty("rules", out JsonElement rulesElement) ||
                rulesElement.ValueKind != JsonValueKind.Array)
            {
                throw new CapturePolicyConfigurationException("策略文件缺少 rules 数组。");
            }

            if (rulesElement.GetArrayLength() > MaximumRules)
            {
                throw new CapturePolicyConfigurationException($"策略规则不能超过 {MaximumRules} 条。");
            }

            var rules = new List<PolicyRule>(rulesElement.GetArrayLength());
            foreach (JsonElement ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseRule(ruleElement));
            }

            return rules;
        }
        catch (CapturePolicyConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CapturePolicyConfigurationException("策略文件不是有效 JSON。", exception);
        }
        catch (IOException exception)
        {
            throw new CapturePolicyConfigurationException("无法读取策略文件。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new CapturePolicyConfigurationException("没有权限读取策略文件。", exception);
        }
    }

    private static PolicyRule ParseRule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("match", out JsonElement match) ||
            match.ValueKind != JsonValueKind.Object)
        {
            throw new CapturePolicyConfigurationException("每条规则必须包含 match 对象。");
        }

        (PolicyMatchKind matchKind, string matchValue) = ParseMatch(match);
        ProcessCapturePolicy defaults = ProcessCapturePolicy.Default;
        var policy = new ProcessCapturePolicy(
            DetectionEnabled: ReadBoolean(element, "detectionEnabled", defaults.DetectionEnabled),
            AccessibilityEnabled: ReadBoolean(element, "accessibilityCapture", defaults.AccessibilityEnabled),
            CopyMode: ReadCopyMode(element, defaults.CopyMode),
            ClipboardStabilizationMs: ReadInteger(
                element,
                "clipboardStabilizationMs",
                defaults.ClipboardStabilizationMs),
            ManualFallbackEnabled: ReadBoolean(element, "manualFallback", defaults.ManualFallbackEnabled));
        policy = policy with
        {
            PreserveCapturedClipboard = ReadBoolean(
                element,
                "preserveCapturedClipboard",
                defaults.PreserveCapturedClipboard),
            HistorySuppressionCount = ReadInteger(
                element,
                "historySuppressionCount",
                defaults.HistorySuppressionCount),
        };

        try
        {
            policy.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new CapturePolicyConfigurationException("剪贴板稳定时间或历史抑制次数超出允许范围。", exception);
        }

        return new PolicyRule(matchKind, matchValue, policy);
    }

    private static (PolicyMatchKind Kind, string Value) ParseMatch(JsonElement match)
    {
        (string JsonName, PolicyMatchKind Kind)[] candidates =
        [
            ("exactPath", PolicyMatchKind.ExactPath),
            ("bundleId", PolicyMatchKind.BundleId),
            ("signedIdentity", PolicyMatchKind.SignedIdentity),
            ("processName", PolicyMatchKind.ProcessName),
        ];

        PolicyMatchKind selectedKind = default;
        string? selectedValue = null;
        int matches = 0;
        foreach ((string jsonName, PolicyMatchKind kind) in candidates)
        {
            if (match.TryGetProperty(jsonName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                matches++;
                selectedKind = kind;
                selectedValue = value.GetString()!.Trim();
            }
        }

        if (matches != 1 || selectedValue is null)
        {
            throw new CapturePolicyConfigurationException("match 必须且只能指定一种应用身份。");
        }

        return (selectedKind, selectedValue);
    }

    private static bool ReadBoolean(JsonElement element, string name, bool defaultValue)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CapturePolicyConfigurationException($"{name} 必须是布尔值。"),
        };
    }

    private static int ReadInteger(JsonElement element, string name, int defaultValue)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        return value.TryGetInt32(out int result)
            ? result
            : throw new CapturePolicyConfigurationException($"{name} 必须是整数。");
    }

    private static SimulatedCopyMode ReadCopyMode(
        JsonElement element,
        SimulatedCopyMode defaultValue)
    {
        if (!element.TryGetProperty("simulatedCopyMode", out JsonElement value))
        {
            return defaultValue;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return Enum.TryParse(text, ignoreCase: true, out SimulatedCopyMode result) &&
            Enum.IsDefined(result)
                ? result
                : throw new CapturePolicyConfigurationException("simulatedCopyMode 无效。");
    }
}

public sealed class CapturePolicyConfigurationException : Exception
{
    public CapturePolicyConfigurationException(string message)
        : base(message)
    {
    }

    public CapturePolicyConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
