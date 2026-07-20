using System.Text.Json;
using SelectionAssistant.Core.Translation;

namespace SelectionAssistant.Providers.Sse;

/// <summary>
/// Parses one completed SSE data block (from <see cref="SseFrameReader" />) into
/// a typed result. This is a pure, allocation-light parser that covers three of
/// the seven required cases: empty deltas (skipped), mid-stream error objects
/// (raised as <see cref="TranslationProviderException" />), and the <c>[DONE]</c>
/// sentinel (signals end of stream).
/// </summary>
internal static class OpenAiSseEventParser
{
    /// <summary>The OpenAI-compatible stream-termination sentinel.</summary>
    public const string DoneSentinel = "[DONE]";

    /// <summary>Outcome of parsing one SSE data block.</summary>
    public enum ParseOutcomeKind
    {
        /// <summary>A content delta was extracted.</summary>
        Delta,
        /// <summary>The block carried no usable content (e.g. empty delta, role-only delta). Skip silently.</summary>
        Skip,
        /// <summary>The <c>[DONE]</c> sentinel was received. Stop consuming.</summary>
        Done,
    }

    public readonly record struct ParseOutcome(ParseOutcomeKind Kind, string? Content)
    {
        public static readonly ParseOutcome Skip = new(ParseOutcomeKind.Skip, null);
        public static readonly ParseOutcome Done = new(ParseOutcomeKind.Done, null);

        public static ParseOutcome Delta(string content) => new(ParseOutcomeKind.Delta, content);
    }

    /// <summary>
    /// Parses one SSE data payload string. Throws
    /// <see cref="TranslationProviderException" /> if the block is an error
    /// object (case 5).
    /// </summary>
    public static ParseOutcome Parse(string data)
    {
        // Case 6: stream termination sentinel. Some servers send it with or
        // without surrounding whitespace.
        if (data.AsSpan().Trim().SequenceEqual(DoneSentinel))
        {
            return ParseOutcome.Done;
        }

        // Parse with JsonDocument (reflection-free, AOT-safe). We deliberately
        // tolerate missing/extra fields — OpenAI-compatible servers vary.
        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement root = document.RootElement;

        // Case 5: mid-stream error object, e.g. {"error":{"message":"rate limited","type":"requests"}}.
        if (root.TryGetProperty("error", out JsonElement errorElement))
        {
            string message = errorElement.TryGetProperty("message", out JsonElement msg)
                ? msg.GetString() ?? "unknown error"
                : "unknown error";
            throw new TranslationProviderException($"翻译服务返回错误：{message}。");
        }

        // Standard chat-completion chunk: choices[0].delta.content
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            // No choices array — could be a usage/stats event. Skip.
            return ParseOutcome.Skip;
        }

        JsonElement delta = choices[0];
        if (!delta.TryGetProperty("delta", out delta))
        {
            return ParseOutcome.Skip;
        }

        // Case 4: empty delta (e.g. the first chunk often carries only {"role":"assistant"}).
        if (!delta.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return ParseOutcome.Skip;
        }

        string? text = content.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return ParseOutcome.Skip;
        }

        return ParseOutcome.Delta(text);
    }
}
