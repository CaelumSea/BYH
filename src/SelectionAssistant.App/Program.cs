using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using SelectionAssistant.Core.Clipboard;
using SelectionAssistant.Infrastructure.Configuration;
using SelectionAssistant.Infrastructure.Logging;
using SelectionAssistant.Infrastructure.Translation;
using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Abstractions.Secrets;
using SelectionAssistant.Platform.Windows.Capture;
using SelectionAssistant.Platform.Windows.Clipboard;
using SelectionAssistant.Platform.Windows.Secrets;
using SelectionAssistant.Providers;

namespace SelectionAssistant.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Last-resort managed-exception capture. Records unhandled exceptions
        // and unobserved task exceptions to crash.log alongside the rolling
        // BYH.log. This does NOT survive native FailFast (0xc0000409 etc.) —
        // those bypass managed handlers entirely and are captured by Windows
        // Error Reporting. Its value is: (a) catching async void / fire-and-
        // forget Task exceptions that would otherwise vanish, and (b) leaving
        // managed-layer evidence when a non-fatal exception precedes a later
        // crash. The handler itself must never throw.
        AppDomain.CurrentDomain.UnhandledException += OnManagedCrash;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (args.Contains("--probe-uia", StringComparer.OrdinalIgnoreCase))
        {
            return ProbeUiAutomationOnMtaThread();
        }

        // R24: tests GetElementBoundsAt from the MAIN thread (which is STA after
        // [STAThread] — same apartment as the real UI thread). Confirms the MTA
        // dispatch fix works end-to-end. Usage:
        //   --probe-bounds <x> <y>
        // Prints the bounding rect or "null". Exit 0 = got a rect, 2 = null.
        if (args.Length >= 3 &&
            args[0].Equals("--probe-bounds", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int pbX) && int.TryParse(args[2], out int pbY))
        {
            return ProbeBoundsFromCurrentThread(pbX, pbY);
        }

        // R24 diagnostic: walks the UI Automation tree inside a screen region
        // and dumps every text-bearing element's text (Name + TextPattern +
        // ValuePattern), in reading order. This is the UIA tier of the new
        // region-OCR fallback — use it to see what UIA returns for a region
        // before OCR runs. If it returns text, OCR won't even be called.
        // Usage: --probe-uia-region <x> <y> <w> <h>
        // Exit 0 = got text, 2 = empty (OCR fallback would run), 3 = error.
        if (args.Length >= 5 &&
            args[0].Equals("--probe-uia-region", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int purX) && int.TryParse(args[2], out int purY) &&
            int.TryParse(args[3], out int purW) && int.TryParse(args[4], out int purH))
        {
            return ProbeUiaRegion(purX, purY, purW, purH);
        }

        if (args.Length >= 4 &&
            args[0].Equals("--probe-clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeClipboardFallback(args[1], args[2], args[3]);
        }

        if (args.Length >= 4 &&
            args[0].Equals("--probe-capture", StringComparison.OrdinalIgnoreCase))
        {
            return ProbePolicyAwareCapture(args[1], args[2], args[3]);
        }

        if (args.Contains("--probe-translation", StringComparer.OrdinalIgnoreCase))
        {
            return ProbeTranslation();
        }

        // Measures streaming translation latency against the configured
        // OpenAI-compatible provider (DeepSeek/...). Reports TTFB (first token),
        // total wall time, character count, and a per-100-char rate. Usage:
        //   --probe-translate-speed "some english text"
        //   --probe-translate-speed                (uses a default sentence)
        // Requires a configured provider + DPAPI key; real network call.
        if (args.Length >= 1 &&
            args[0].Equals("--probe-translate-speed", StringComparison.OrdinalIgnoreCase))
        {
            string text = args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1])
                ? args[1]
                : "The quick brown fox jumps over the lazy dog near the riverbank at dawn.";
            return ProbeTranslateSpeed(text);
        }

        if (args.Contains("--probe-policy", StringComparer.OrdinalIgnoreCase))
        {
            return ProbeCapturePolicy();
        }

        // REQ-029 diagnostic: resolve the capture policy for another process
        // without starting the desktop shell. This is useful for validating
        // app-specific terminal rules (for example Warp's Ctrl+Shift+C copy
        // path) before asking the user to retry a real selection.
        // Usage: --probe-process-policy <pid>
        if (args.Length >= 2 &&
            args[0].Equals("--probe-process-policy", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(args[1], out uint processPolicyPid) &&
            processPolicyPid != 0)
        {
            return ProbeProcessPolicy(processPolicyPid);
        }

        // R24: exercises the vision OCR tier end-to-end without a selection
        // session. Captures a small screen region at the given point (or screen
        // center), encodes it to PNG, and runs the configured OCR model. Usage:
        //   --probe-vision              (captures screen center, ~300x150)
        //   --probe-vision <x> <y> <w> <h>
        // Requires a configured vision provider (vision.json) + DPAPI key; real
        // network + screenshot call. Exit 0 = got text, 2 = empty, 3 = error.
        if (args.Length >= 1 &&
            args[0].Equals("--probe-vision", StringComparison.OrdinalIgnoreCase))
        {
            int vx = 600, vy = 300, vw = 300, vh = 150;
            if (args.Length >= 5 &&
                int.TryParse(args[1], out vx) && int.TryParse(args[2], out vy) &&
                int.TryParse(args[3], out vw) && int.TryParse(args[4], out vh))
            {
                // explicit region
            }

            return ProbeVisionOcr(vx, vy, vw, vh);
        }

        // R24 diagnostic: same as --probe-vision but dumps the RAW HTTP body the
        // model returned (before SSE parsing / cleaning). Use this to answer
        // "is the extra/garbage text model output, an SSE parse bug, or client-
        // side concatenation?". Prints status code, raw body, then the cleaned
        // text for comparison. Usage:
        //   --probe-ocr-raw <x> <y> <w> <h>
        // Exit 0 = 2xx + got cleaned text, 2 = empty, 3 = error.
        if (args.Length >= 5 &&
            args[0].Equals("--probe-ocr-raw", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int prX) && int.TryParse(args[2], out int prY) &&
            int.TryParse(args[3], out int prW) && int.TryParse(args[4], out int prH))
        {
            return ProbeOcrRaw(prX, prY, prW, prH);
        }

        // R24 diagnostic: captures a screen region (physical px) and saves the
        // raw PNG to disk so we can verify the BitBlt coordinates are correct
        // (the "OCR returns extra/garbage text" bug is almost always a wrong
        // capture rectangle, not an OCR problem). Usage:
        //   --probe-save-region <x> <y> <w> <h> [out.png]
        // Writes to probe-region.png in the CWD by default. No OCR, no network.
        if (args.Length >= 5 &&
            args[0].Equals("--probe-save-region", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int psrX) && int.TryParse(args[2], out int psrY) &&
            int.TryParse(args[3], out int psrW) && int.TryParse(args[4], out int psrH))
        {
            string outPath = args.Length >= 6 ? args[5] : "probe-region.png";
            return ProbeSaveRegion(psrX, psrY, psrW, psrH, outPath);
        }

        // R28 diagnostic: exercises the complete Ocean Eyes save path without
        // opening the selection overlay. It captures BGRA + PNG, builds the
        // CF_DIB payload from the raw buffer, writes the PNG, and optionally
        // places both formats on the clipboard. Usage:
        //   --probe-ocean-eyes-save <x> <y> <w> <h> [out.png] [--copy]
        // Exit 0 = all requested stages completed, 2 = clipboard rejected,
        // 3 = capture/conversion/save error. The --copy switch deliberately
        // changes the current clipboard and is intended for controlled QA.
        if (args.Length >= 5 &&
            args[0].Equals("--probe-ocean-eyes-save", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int poesX) && int.TryParse(args[2], out int poesY) &&
            int.TryParse(args[3], out int poesW) && int.TryParse(args[4], out int poesH))
        {
            string outPath = args.Length >= 6 &&
                !args[5].StartsWith("--", StringComparison.Ordinal)
                ? args[5]
                : "ocean-eyes-probe.png";
            bool copy = args.Contains("--copy", StringComparer.OrdinalIgnoreCase);
            return ProbeOceanEyesSave(poesX, poesY, poesW, poesH, outPath, copy);
        }

        // R28 diagnostic: exercises the Ocean Eyes clipboard write together
        // with the same long-lived image listener used by the desktop app.
        // The earlier save probe exits immediately after SetImageDibAndPng and
        // therefore cannot observe the WM_CLIPBOARDUPDATE -> DIB -> PNG path
        // that runs in ClipboardHistoryService. Usage:
        //   --probe-ocean-eyes-history <x> <y> <w> <h>
        // Exit 0 = image write + listener capture completed (or an oversized
        // DIB was intentionally downgraded to PNG-only), 2 = write or
        // listener did not produce an image entry, 3 = diagnostic error.
        if (args.Length >= 5 &&
            args[0].Equals("--probe-ocean-eyes-history", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int poehX) && int.TryParse(args[2], out int poehY) &&
            int.TryParse(args[3], out int poehW) && int.TryParse(args[4], out int poehH))
        {
            return ProbeOceanEyesHistory(poehX, poehY, poehW, poehH);
        }

        // Writes an API key / secret to DPAPI-encrypted storage (§11.3). The
        // value is never logged or echoed. Usage: --set-secret <reference> <value>
        if (args.Length >= 3 &&
            args[0].Equals("--set-secret", StringComparison.OrdinalIgnoreCase))
        {
            return SetSecret(args[1], args[2]);
        }

        // R23 probe: extracts the small icon from an exe and writes it to a PNG.
        // Validates the SHGetFileInfo → GetDIBits → PngEncoder chain end-to-end
        // before wiring it into the UI. Usage: --probe-icon-extract <exePath> [out.png]
        if (args.Length >= 2 &&
            args[0].Equals("--probe-icon-extract", StringComparison.OrdinalIgnoreCase))
        {
            string exePath = args[1];
            string outPath = args.Length >= 3 ? args[2] : "probe-icon.png";
            return ProbeIconExtract(exePath, outPath);
        }

        // R23 probe: lists all configured launcher entries (no IO on disk other
        // than reading launcher-entries.json). Useful for verifying that user
        // edits persisted correctly. Exit 0 = file read, 2 = empty, 3 = error.
        if (args.Length >= 1 &&
            args[0].Equals("--probe-launcher-list", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeLauncherList();
        }

        // R23 probe: runs a launcher entry by id (does NOT go through the UI;
        // used to verify that the LauncherRunner → Process.Start path works in
        // a NativeAOT-published build). Usage: --probe-launcher-run <id>
        // Optional: --probe-launcher-run <id> --clip "text" --sel "text"
        if (args.Length >= 2 &&
            args[0].Equals("--probe-launcher-run", StringComparison.OrdinalIgnoreCase))
        {
            string entryId = args[1];
            string? clip = ExtractOptionalValue(args, "--clip");
            string? sel = ExtractOptionalValue(args, "--sel");
            return ProbeLauncherRun(entryId, clip, sel);
        }

        // Single-instance guard: only one BYH desktop app may run at a time.
        // The named Mutex is global to this user session; the first instance
        // owns it for its lifetime, and any later launch exits silently here.
        // Probe/diagnostic branches above bypass this (they're CLI tools).
        //
        // The Mutex lives in a static field (not a local `using`) so the
        // restart path (RequestRestart → ReleaseForRestart) can drop ownership
        // before spawning the new copy; otherwise the new process races on
        // this Mutex while the old one is still tearing down and silently
        // exits, leaving no tray icon behind (R31 fix).
        s_singleInstance = new Mutex(initiallyOwned: true, name: "Global\\BYH_ByYourHand_SingleInstance",
            out bool acquired);

        // Restart path: this process was spawned by an exiting instance that
        // has (or is about to) release the Mutex. Give it a short grace window
        // instead of bailing instantly, so "重启 BYH" reliably relaunches.
        bool isRestart = args.Contains("--restart", StringComparer.OrdinalIgnoreCase);
        if (!acquired && isRestart)
        {
            const int restartAttempts = 30;
            const int restartRetryDelayMs = 100;
            for (int i = 0; i < restartAttempts; i++)
            {
                try { acquired = s_singleInstance.WaitOne(restartRetryDelayMs); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) break;
            }
        }

        if (!acquired)
        {
            // Another instance is already running. Bail out without starting
            // a second tray icon / mouse hook.
            s_singleInstance.Dispose();
            s_singleInstance = null!;
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static Mutex? s_singleInstance;

    /// <summary>
    /// Drops ownership of the single-instance Mutex so a freshly spawned copy
    /// (started by RequestRestart) can pick it up. After this call the current
    /// process is no longer the sole owner — it must exit immediately. Safe to
    /// call when no Mutex was ever acquired (no-op).
    /// </summary>
    public static void ReleaseForRestart()
    {
        try { s_singleInstance?.ReleaseMutex(); }
        catch (ApplicationException) { /* wasn't held on this thread; ignore */ }
        catch (ObjectDisposedException) { /* already gone; ignore */ }
        s_singleInstance?.Dispose();
        s_singleInstance = null;
    }

    /// <summary>
    /// Records a managed unhandled exception to <c>logs/crash.log</c>. Paired
    /// with <see cref="OnUnobservedTaskException"/>; both are registered at the
    /// top of <see cref="Main"/>. The handler swallows all errors of its own —
    /// a crash logger must never become a second crash source. See the Main
    /// remark for why this cannot catch native FailFast (0xc0000409).
    /// </summary>
    private static void OnManagedCrash(object? sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashRecord("UnhandledException", e.ExceptionObject as Exception, isTerminating: e.IsTerminating);
    }

    /// <summary>
    /// Records an unobserved task exception (fire-and-forget <c>Task.Run</c> /
    /// async void) to <c>logs/crash.log</c>. Marks the exception observed so
    /// the finalizer does not re-escalate it.
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashRecord("UnobservedTaskException", e.Exception, isTerminating: false);
        e.SetObserved();
    }

    private static void WriteCrashRecord(string source, Exception? exception, bool isTerminating)
    {
        // Build the record from simple primitives only (no reflection) so this
        // stays NativeAOT- and trimming-safe. Exception.ToString already
        // includes the type, message, and stack trace; we never log raw
        // selected text or credentials here — only framework exception text.
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string body = exception is null ? "(no exception object)" : exception.ToString();
        string threadId = Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
        string record =
            $"==== {timestamp} pid={Environment.ProcessId} tid={threadId} " +
            $"source={source} terminating={isTerminating} ====" + Environment.NewLine +
            body + Environment.NewLine + Environment.NewLine;

        try
        {
            string logDirectory = ByhApplicationPaths.CreateDefault().LogsDirectory;
            Directory.CreateDirectory(logDirectory);
            string crashPath = Path.Combine(logDirectory, "crash.log");
            File.AppendAllText(crashPath, record, Encoding.UTF8);
        }
        catch
        {
            // Best-effort: never rethrow from the crash handler.
        }
    }

    private static int SetSecret(string reference, string value)
    {
        try
        {
            ByhApplicationPaths paths = ByhApplicationPaths.CreateDefault();
            paths.EnsureDirectories();
            ISecretStore store = new DpapiSecretStore(paths.SecretsDirectory);

            store.SetAsync(reference, value, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            // Deliberately do not echo the value. Confirm only the reference.
            Console.WriteLine($"已保存密钥引用：{reference}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"保存密钥失败：{ex.Message}");
            return 3;
        }
    }

    private static int ProbeTranslation()
    {
        try
        {
            using var provider = new MyMemoryTranslationProvider(timeout: TimeSpan.FromSeconds(15));
            var request = new Core.Translation.TranslationRequest("Hello world", "en", "zh-CN");
            Core.Translation.TranslationResult result = provider
                .TranslateAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return !string.IsNullOrWhiteSpace(result.TranslatedText) &&
                !result.TranslatedText.Equals(request.SourceText, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 2;
        }
        catch
        {
            return 3;
        }
    }

    /// <summary>
    /// Streams a translation through the configured OpenAI-compatible provider
    /// and reports timing breakdown: time-to-first-token, total wall time,
    /// output character count, and chars/100ms rate. A real network call.
    /// Exit 0 = success, 2 = no output, 3 = error (message printed).
    /// </summary>
    private static int ProbeTranslateSpeed(string sourceText)
    {
        try
        {
            ByhApplicationPaths paths = ByhApplicationPaths.CreateDefault();
            paths.EnsureDirectories();

            ProviderConfiguration config = ProviderConfigurationLoader
                .LoadIfExists(paths.ProvidersFile);
            ProviderProfileEntry? entry = null;
            if (!string.IsNullOrEmpty(config.DefaultProviderId))
            {
                entry = config.Providers.FirstOrDefault(
                    p => string.Equals(p.Id, config.DefaultProviderId, StringComparison.OrdinalIgnoreCase));
            }
            entry ??= config.Providers.FirstOrDefault();
            if (entry is null)
            {
                Console.Error.WriteLine("没有配置 Provider（providers.json 为空）。");
                return 3;
            }

            var options = new OpenAiCompatibleProviderOptions
            {
                Id = entry.Id,
                DisplayName = entry.Name,
                BaseUrl = entry.BaseUrl,
                ApiKeyReference = entry.ApiKeyReference,
                DefaultModel = entry.DefaultModel,
                ChatCompletionsPath = entry.ChatCompletionsPath,
                Timeout = TimeSpan.FromSeconds(entry.TimeoutSeconds),
                MaxSourceCharacters = entry.MaxSourceCharacters,
            };

            var secretStore = new DpapiSecretStore(paths.SecretsDirectory);
            using var provider = new OpenAiCompatibleStreamingProvider(options, secretStore);

            // Auto-detect direction: Chinese source → English; else → Simplified Chinese.
            bool hasCjk = sourceText.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            var request = new Core.Translation.TranslationRequest(
                sourceText,
                hasCjk ? "zh-CN" : "en",
                hasCjk ? "en" : "zh-CN");

            Console.WriteLine($"Provider : {entry.Name} ({entry.DefaultModel})");
            Console.WriteLine($"Source   : \"{sourceText}\"");
            Console.WriteLine($"Direction: {(hasCjk ? "zh-CN → en" : "en → zh-CN")}");
            Console.WriteLine($"Thinking : disabled (thinking:{{type:disabled}} + enable_thinking:false)");
            Console.WriteLine("Streaming...");
            Console.Out.Flush();

            (long firstChunkMs, long totalMs, int chars, int chunks, string output) =
                ProbeTranslateSpeedCoreAsync(provider, request)
                    .GetAwaiter().GetResult();

            if (chars == 0)
            {
                Console.Error.WriteLine($"失败：未收到任何内容（{totalMs} ms）。");
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine($"首 token (TTFB) : {firstChunkMs} ms");
            Console.WriteLine($"流式总耗时        : {totalMs} ms");
            Console.WriteLine($"收到 chunk 数      : {chunks}");
            Console.WriteLine($"输出字符数         : {chars}");
            double ratePer100ms = totalMs > 0 ? chars * 100.0 / totalMs : 0;
            Console.WriteLine($"流速              : {ratePer100ms:F1} 字符/100ms");
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine($"译文: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"测速失败：{ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static async Task<(long firstChunkMs, long totalMs, int chars, int chunks, string output)>
        ProbeTranslateSpeedCoreAsync(
            OpenAiCompatibleStreamingProvider provider,
            Core.Translation.TranslationRequest request)
    {
        var sw = Stopwatch.StartNew();
        long firstChunkMs = -1;
        int chars = 0;
        int chunks = 0;
        var output = new System.Text.StringBuilder();

        await foreach (Core.Translation.TranslationDelta delta in
            provider.StreamAsync(request, CancellationToken.None).ConfigureAwait(false))
        {
            if (firstChunkMs < 0)
            {
                firstChunkMs = sw.ElapsedMilliseconds;
            }
            chunks++;
            chars += delta.Content.Length;
            output.Append(delta.Content);
            Console.Write(delta.Content);
            Console.Out.Flush();
        }

        sw.Stop();
        return (firstChunkMs < 0 ? sw.ElapsedMilliseconds : firstChunkMs,
            sw.ElapsedMilliseconds, chars, chunks, output.ToString());
    }

    /// <summary>
    /// R24: end-to-end probe of the vision OCR tier. Captures a screen region,
    /// encodes PNG, runs the configured OCR model, and prints recognized text.
    /// No UI / no selection session — pure CLI verification of the new code
    /// path. Exit 0 = text recognized, 2 = empty, 3 = config/network error.
    /// </summary>
    private static int ProbeVisionOcr(int x, int y, int width, int height)
    {
        try
        {
            ByhApplicationPaths paths = ByhApplicationPaths.CreateDefault();
            paths.EnsureDirectories();

            Core.Capture.VisionCaptureSettings vision;
            try
            {
                vision = VisionCaptureStore.LoadIfExists(paths.VisionCaptureFile);
            }
            catch (ProviderConfigurationException ex)
            {
                Console.Error.WriteLine($"vision.json 解析失败：{ex.Message}");
                return 3;
            }

            if (!vision.Enabled)
            {
                Console.Error.WriteLine("视觉识别在 vision.json 里是关闭的（enabled=false）。");
                return 3;
            }

            ProviderConfiguration providers = ProviderConfigurationLoader
                .LoadIfExists(paths.ProvidersFile);
            ProviderProfileEntry? entry = providers.Providers.FirstOrDefault(
                p => string.Equals(p.Id, vision.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                Console.Error.WriteLine(
                    $"providers.json 里找不到 vision 配的 Provider '{vision.ProviderId}'。");
                return 3;
            }

            var options = new OpenAiCompatibleProviderOptions
            {
                Id = entry.Id,
                DisplayName = entry.Name,
                BaseUrl = entry.BaseUrl,
                ApiKeyReference = entry.ApiKeyReference,
                DefaultModel = vision.Model,
                ChatCompletionsPath = entry.ChatCompletionsPath,
                Timeout = entry.TimeoutSeconds < 30
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(entry.TimeoutSeconds),
                MaxSourceCharacters = entry.MaxSourceCharacters,
            };

            var secretStore = new DpapiSecretStore(paths.SecretsDirectory);
            using var ocrClient = new OpenAiCompatibleVisionOcrClient(options, secretStore, vision.OcrPrompt, vision.DisableThinking);

            Console.WriteLine($"Provider : {entry.Name}");
            Console.WriteLine($"Model    : {vision.Model}");
            Console.WriteLine($"Prompt   : \"{vision.OcrPrompt}\"");
            Console.WriteLine($"Region   : ({x},{y}) {width}x{height}");
            Console.WriteLine("Capturing + OCR...");
            Console.Out.Flush();

            string? dataUri = ScreenRegionCapture.CaptureAsDataUri(x, y, width, height);
            if (string.IsNullOrEmpty(dataUri))
            {
                Console.Error.WriteLine("截图失败（BitBlt/编码返回空）。");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            string text = ocrClient
                .RecognizeAsync(dataUri, CancellationToken.None)
                .GetAwaiter().GetResult();
            sw.Stop();

            text = text.Trim();
            Console.WriteLine($"耗时     : {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"字符数   : {text.Length}");
            Console.WriteLine("---- 识别结果 ----");
            Console.WriteLine(text);
            Console.WriteLine("------------------");

            return string.IsNullOrEmpty(text) ? 2 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"OCR 探针出错：{exception.Message}");
            return 3;
        }
    }

    /// <summary>
    /// R24 diagnostic: dumps the raw OCR HTTP response body so we can see
    /// exactly what the model returned — including any <c>&lt;think&gt;</c>
    /// reasoning blocks, SSE framing artifacts, or extra tokens that the SSE
    /// parser/cleaner would otherwise hide. Side-by-side with the cleaned text.
    /// Exit 0 = 2xx + non-empty cleaned text, 2 = empty, 3 = error.
    /// </summary>
    private static int ProbeOcrRaw(int x, int y, int width, int height)
    {
        try
        {
            ByhApplicationPaths paths = ByhApplicationPaths.CreateDefault();
            paths.EnsureDirectories();

            Core.Capture.VisionCaptureSettings vision;
            try
            {
                vision = VisionCaptureStore.LoadIfExists(paths.VisionCaptureFile);
            }
            catch (ProviderConfigurationException ex)
            {
                Console.Error.WriteLine($"vision.json 解析失败：{ex.Message}");
                return 3;
            }

            if (!vision.Enabled)
            {
                Console.Error.WriteLine("视觉识别在 vision.json 里是关闭的（enabled=false）。");
                return 3;
            }

            ProviderConfiguration providers = ProviderConfigurationLoader
                .LoadIfExists(paths.ProvidersFile);
            ProviderProfileEntry? entry = providers.Providers.FirstOrDefault(
                p => string.Equals(p.Id, vision.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                Console.Error.WriteLine(
                    $"providers.json 里找不到 vision 配的 Provider '{vision.ProviderId}'。");
                return 3;
            }

            var options = new OpenAiCompatibleProviderOptions
            {
                Id = entry.Id,
                DisplayName = entry.Name,
                BaseUrl = entry.BaseUrl,
                ApiKeyReference = entry.ApiKeyReference,
                DefaultModel = vision.Model,
                ChatCompletionsPath = entry.ChatCompletionsPath,
                Timeout = entry.TimeoutSeconds < 30
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(entry.TimeoutSeconds),
                MaxSourceCharacters = entry.MaxSourceCharacters,
            };

            var secretStore = new DpapiSecretStore(paths.SecretsDirectory);
            using var ocrClient = new OpenAiCompatibleVisionOcrClient(options, secretStore, vision.OcrPrompt, vision.DisableThinking);

            Console.WriteLine($"Provider : {entry.Name}");
            Console.WriteLine($"Model    : {vision.Model}");
            Console.WriteLine($"Region   : ({x},{y}) {width}x{height}");
            Console.WriteLine("Capturing + OCR (raw dump)...");
            Console.Out.Flush();

            string? dataUri = ScreenRegionCapture.CaptureAsDataUri(x, y, width, height);
            if (string.IsNullOrEmpty(dataUri))
            {
                Console.Error.WriteLine("截图失败（BitBlt/编码返回空）。");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            OcrRawResult raw = ocrClient
                .RecognizeRawAsync(dataUri, CancellationToken.None)
                .GetAwaiter().GetResult();
            sw.Stop();

            Console.WriteLine($"耗时     : {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"HTTP     : {raw.StatusCode}");
            Console.WriteLine($"原始字节 : {raw.RawBody.Length}");
            Console.WriteLine("---- 原始响应体 ----");
            Console.WriteLine(raw.RawBody);
            Console.WriteLine("---- 清洗后文字 ----");
            Console.WriteLine(raw.CleanedText ?? "(non-2xx，无清洗结果)");
            Console.WriteLine("--------------------");

            return raw.StatusCode >= 200 && raw.StatusCode < 300
                ? (string.IsNullOrEmpty(raw.CleanedText) ? 2 : 0)
                : 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"OCR raw 探针出错：{exception.Message}");
            return 3;
        }
    }

    private static int ProbeCapturePolicy()
    {
        try
        {
            var identityResolver = new WindowsProcessIdentityResolver();
            Core.Capture.ProcessIdentity identity = identityResolver.Resolve((uint)Environment.ProcessId);
            Core.Capture.IProcessCapturePolicyProvider provider =
                WindowsDefaultCapturePolicies.CreateProvider();
            Core.Capture.ProcessCapturePolicy policy = provider.Resolve((uint)Environment.ProcessId);

            return !string.IsNullOrWhiteSpace(identity.ProcessName) &&
                policy.DetectionEnabled &&
                policy.ClipboardStabilizationMs >= 0
                    ? 0
                    : 2;
        }
        catch
        {
            return 3;
        }
    }

    private static int ProbeProcessPolicy(uint processId)
    {
        try
        {
            var identityResolver = new WindowsProcessIdentityResolver();
            Core.Capture.ProcessIdentity identity = identityResolver.Resolve(processId);
            Core.Capture.IProcessCapturePolicyProvider provider =
                WindowsDefaultCapturePolicies.CreateProvider();
            Core.Capture.ProcessCapturePolicy policy = provider.Resolve(processId);

            Console.WriteLine($"PID                  : {identity.ProcessId}");
            Console.WriteLine($"Process name         : {identity.ProcessName ?? "(unknown)"}");
            Console.WriteLine($"Executable           : {identity.ExecutablePath ?? "(unknown)"}");
            Console.WriteLine($"Elevated             : {identity.IsElevated}");
            Console.WriteLine($"Detection enabled    : {policy.DetectionEnabled}");
            Console.WriteLine($"Accessibility        : {policy.AccessibilityEnabled}");
            Console.WriteLine($"Simulated copy mode  : {policy.CopyMode}");
            Console.WriteLine($"Stabilization (ms)   : {policy.ClipboardStabilizationMs}");
            Console.WriteLine($"Manual fallback      : {policy.ManualFallbackEnabled}");

            return identity.ProcessName is null ? 2 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"进程策略探针出错：{exception.Message}");
            return 3;
        }
    }

    // R24 diagnostic: saves the captured screen region as a PNG so the capture
    // rectangle can be visually verified. Decodes the data URI (base64 PNG)
    // produced by ScreenRegionCapture and writes the raw PNG bytes to disk.
    private static int ProbeSaveRegion(int x, int y, int w, int h, string outPath)
    {
        try
        {
            string? dataUri = SelectionAssistant.Platform.Windows.Capture.ScreenRegionCapture
                .CaptureAsDataUri(x, y, w, h);
            if (string.IsNullOrEmpty(dataUri))
            {
                Console.Error.WriteLine($"截图失败：CaptureAsDataUri({x},{y},{w},{h}) 返空。");
                return 3;
            }

            // data:image/png;base64,<base64>
            const string prefix = "data:image/png;base64,";
            int idx = dataUri.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0)
            {
                Console.Error.WriteLine("data URI 前缀不对。");
                return 3;
            }

            string base64 = dataUri[(idx + prefix.Length)..];
            byte[] png = Convert.FromBase64String(base64);
            string? directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(outPath, png);

            Console.WriteLine($"Region : ({x},{y}) {w}x{h}");
            Console.WriteLine($"Bytes  : {png.Length}");
            Console.WriteLine($"Saved  : {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"截图探针出错：{exception.Message}");
            return 3;
        }
    }

    private static int ProbeOceanEyesSave(
        int x, int y, int width, int height, string outPath, bool copyToClipboard)
    {
        try
        {
            var captured = ScreenRegionCapture.CaptureAsPngAndBgra(x, y, width, height);
            if (captured is null)
            {
                Console.Error.WriteLine("截图失败（BitBlt/GetDIBits 返回空）。");
                return 3;
            }

            byte[] png = captured.Value.Png;
            // SaveOceanEyesScreenshot receives an annotation-free clone from
            // BurnAnnotationsIntoPng before it builds the DIB. Keep that same
            // ownership boundary here so the probe exercises the real sizes.
            byte[] finalBgra = (byte[])captured.Value.Bgra.Clone();
            byte[]? dib = PngToDibConverter.ConvertBgraToDib(finalBgra, width, height);
            // The production path intentionally falls back to PNG-only when
            // the CF_DIB budget would be exceeded. Keep the diagnostic aligned
            // with that behavior so a large but valid PNG can still be tested
            // without manufacturing an oversized clipboard HGLOBAL.
            long expectedDibBytes = 40L + (long)width * height * 4;
            bool dibDowngradedToPngOnly = dib is null && expectedDibBytes > 32L * 1024 * 1024;
            if (dib is null && !dibDowngradedToPngOnly)
            {
                Console.Error.WriteLine("BGRA→CF_DIB 转换失败。");
                return 3;
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(outPath, png);

            bool clipboardPlaced = true;
            if (copyToClipboard)
            {
                using var clipboard = new Win32Clipboard();
                clipboardPlaced = clipboard.SetImageDibAndPng(png, dib);
            }

            Console.WriteLine($"Region : ({x},{y}) {width}x{height}");
            Console.WriteLine($"PNG    : {png.Length} bytes");
            Console.WriteLine($"BGRA   : {finalBgra.Length} bytes");
            Console.WriteLine($"DIB    : {(dib is null ? "skipped (PNG-only budget)" : $"{dib.Length} bytes")}");
            Console.WriteLine($"Saved  : {Path.GetFullPath(outPath)}");
            Console.WriteLine($"Copied : {(copyToClipboard ? clipboardPlaced : false)}");
            return copyToClipboard && !clipboardPlaced ? 2 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ocean Eyes 保存探针出错：{exception}");
            return 3;
        }
    }

    private static int ProbeOceanEyesHistory(int x, int y, int width, int height)
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "probe-clipboard-history",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);
            string historyPath = Path.Combine(root, "clipboard-history.json");
            string tagsPath = Path.Combine(root, "clipboard-tags.json");
            string iconsPath = Path.Combine(root, "clipboard-icons.json");
            string imagesPath = Path.Combine(root, "images");
            string archivePath = Path.Combine(root, "archive");
            string logPath = Path.Combine(root, "probe.log");

            var captured = ScreenRegionCapture.CaptureAsPngAndBgra(x, y, width, height);
            if (captured is null)
            {
                Console.Error.WriteLine("截图失败（BitBlt/GetDIBits 返回空）。");
                return 3;
            }

            byte[] png = captured.Value.Png;
            byte[] finalBgra = (byte[])captured.Value.Bgra.Clone();
            byte[]? dib = PngToDibConverter.ConvertBgraToDib(finalBgra, width, height);
            // The production path intentionally falls back to PNG-only when
            // the CF_DIB budget would be exceeded. Keep the diagnostic aligned
            // with that behavior so a large but valid PNG can still be tested
            // without manufacturing an oversized clipboard HGLOBAL.
            long expectedDibBytes = 40L + (long)width * height * 4;
            bool dibDowngradedToPngOnly = dib is null && expectedDibBytes > 32L * 1024 * 1024;
            if (dib is null && !dibDowngradedToPngOnly)
            {
                Console.Error.WriteLine("BGRA→CF_DIB 转换失败。");
                return 3;
            }

            // This is the production listener path, isolated to a throw-away
            // store so the probe never mutates the user's clipboard history.
            var settings = ClipboardHistorySettings.Default with
            {
                MaxEntries = 20,
                MaxImageEntries = 5,
            };
            using var clipboard = new Win32Clipboard();
            using var service = new ClipboardHistoryService(
                clipboard,
                historyPath,
                tagsPath,
                iconsPath,
                imagesPath,
                settings,
                new RedactedLogger(logPath),
                entryCipher: null,
                archiveDirectory: archivePath);

            bool placed = clipboard.SetImageDibAndPng(png, dib);
            // WM_CLIPBOARDUPDATE is delivered on Win32Clipboard's STA message
            // thread. Give the listener enough time to open the clipboard, run
            // DIB→PNG conversion, and persist the disposable probe entry.
            Thread.Sleep(TimeSpan.FromSeconds(2));

            ClipboardEntry? image = service.Snapshot.FirstOrDefault(
                entry => entry.Kind == ClipboardEntryKind.Image);
            Console.WriteLine($"Region : ({x},{y}) {width}x{height}");
            Console.WriteLine($"PNG    : {png.Length} bytes");
            Console.WriteLine($"BGRA   : {finalBgra.Length} bytes");
            Console.WriteLine($"DIB    : {(dib is null ? "skipped (PNG-only budget)" : $"{dib.Length} bytes")}");
            Console.WriteLine($"Placed : {placed}");
            Console.WriteLine($"History image: {(image is null ? "none" : image.ImageFileName ?? "(unnamed)")}");
            Console.WriteLine($"Probe root: {root}");

            // SetImageDibAndPng deliberately omits a DIB above the 32 MiB
            // clipboard-history read budget. In that case the PNG is still a
            // successful, crash-safe result; the listener cannot create an
            // image entry because CF_DIB is intentionally absent.
            return placed && (image is not null || dibDowngradedToPngOnly) ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ocean Eyes 历史监听探针出错：{exception}");
            return 3;
        }
    }

    // R23 diagnostic: extracts the small icon from an exe and writes it as a PNG.
    // Validates the SHGetFileInfo → GetIconInfo → GetDIBits → PngEncoder chain.
    // Exit 0 = PNG written, 2 = no icon returned, 3 = exception. The output PNG
    // should open in any image viewer; if the dimensions look right the whole
    // launcher-icon pipeline is confirmed working in this build.
    private static int ProbeIconExtract(string exePath, string outPath)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"文件不存在：{exePath}");
                return 3;
            }
            byte[]? png = SelectionAssistant.Platform.Windows.Launcher.WindowsIconExtractor
                .ExtractSmallIconPng(exePath);
            if (png is null || png.Length == 0)
            {
                Console.Error.WriteLine($"提取图标失败：返回空。路径={exePath}");
                Console.Error.WriteLine($"  Diagnostic = {SelectionAssistant.Platform.Windows.Launcher.WindowsIconExtractor.LastDiagnostic}");
                Console.Error.WriteLine($"  SHGetFileInfo result={SelectionAssistant.Platform.Windows.Launcher.WindowsIconExtractor.LastShGetFileInfoResult} cbSize={SelectionAssistant.Platform.Windows.Launcher.WindowsIconExtractor.LastShGetFileInfoCbSize} err={SelectionAssistant.Platform.Windows.Launcher.WindowsIconExtractor.LastShGetFileInfoError}");
                return 2;
            }
            File.WriteAllBytes(outPath, png);
            Console.WriteLine($"Source : {exePath}");
            Console.WriteLine($"Bytes  : {png.Length}");
            Console.WriteLine($"PNG    : {Convert.ToBase64String(png)[..Math.Min(64, png.Length)]}...");
            Console.WriteLine($"Saved  : {Path.GetFullPath(outPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"提取图标异常：{ex}");
            return 3;
        }
    }

    // R23 diagnostic: lists all configured launcher entries. Exit 0 = file
    // read OK (even if empty), 3 = error.
    private static int ProbeLauncherList()
    {
        try
        {
            var paths = SelectionAssistant.Infrastructure.Configuration.ByhApplicationPaths.CreateDefault();
            var set = SelectionAssistant.Infrastructure.Configuration.LauncherEntryStore.LoadIfExists(paths.LauncherEntriesFile);
            var entries = set.AsList();
            Console.WriteLine($"File   : {paths.LauncherEntriesFile}");
            Console.WriteLine($"Count  : {entries.Count}");
            foreach (var entry in entries)
            {
                Console.WriteLine($"  [{entry.Kind}] {entry.Name}  →  {entry.Target}");
                if (!string.IsNullOrEmpty(entry.Arguments))
                {
                    Console.WriteLine($"    args={entry.Arguments}");
                }
            }
            return entries.Count > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取启动项异常：{ex.Message}");
            return 3;
        }
    }

    // R23 diagnostic: runs a launcher entry by id, expanding {clip}/{sel} from
    // the optional --clip/--sel args. If the entry has {prompt:...} placeholders
    // this probe aborts (no UI for prompts here). Exit 0 = launched, 2 = not
    // found / needs prompt, 3 = error.
    private static int ProbeLauncherRun(string entryId, string? clip, string? sel)
    {
        try
        {
            var paths = SelectionAssistant.Infrastructure.Configuration.ByhApplicationPaths.CreateDefault();
            var set = SelectionAssistant.Infrastructure.Configuration.LauncherEntryStore.LoadIfExists(paths.LauncherEntriesFile);
            var entry = set.Find(entryId);
            if (entry is null)
            {
                Console.Error.WriteLine($"找不到启动项：{entryId}");
                return 2;
            }

            var expanded = SelectionAssistant.Core.Launcher.ParameterReplace.Expand(entry.Arguments, clip, sel);
            if (expanded.NeedsPrompt)
            {
                Console.Error.WriteLine($"此启动项需要 {expanded.Prompts.Count} 个运行时输入参数，CLI 探针无法收集。");
                return 2;
            }
            Console.WriteLine($"Entry  : {entry.Name}");
            Console.WriteLine($"Kind   : {entry.Kind}");
            Console.WriteLine($"Target : {entry.Target}");
            Console.WriteLine($"Args   : '{expanded.ExpandedArguments}'");
            string? err = SelectionAssistant.Platform.Windows.Launcher.LauncherRunner.Start(entry, expanded.ExpandedArguments);
            if (err is not null)
            {
                Console.Error.WriteLine($"启动失败：{err}");
                return 3;
            }
            Console.WriteLine("已启动。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"运行启动项异常：{ex.Message}");
            return 3;
        }
    }

    // Extracts --flag value from args (case-insensitive flag). Returns null if
    // the flag is absent or has no following value.
    private static string? ExtractOptionalValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    // R24 diagnostic: calls GetElementBoundsAt from the current (STA) thread to
    // confirm the MTA dispatch fix works. The real app's UI thread is STA; this
    // probe reproduces that. If the MTA worker dispatch is broken, this returns
    // null (the pre-fix behavior). Prints the rect + thread apartment for debug.
    private static int ProbeBoundsFromCurrentThread(int x, int y)
    {
        Console.WriteLine($"Thread apt: {Thread.CurrentThread.GetApartmentState()}");
        using var backend = new WindowsUiAutomationBackend();
        var sw = Stopwatch.StartNew();
        var rect = backend.GetElementBoundsAt(x, y);
        sw.Stop();
        if (rect is { } r)
        {
            Console.WriteLine($"Bounds  : ({r.X},{r.Y}) {r.Width}x{r.Height}");
            Console.WriteLine($"耗时    : {sw.ElapsedMilliseconds} ms");
            return 0;
        }
        Console.WriteLine($"Bounds  : null (UIA 在 ({x},{y}) 没有元素，或 MTA dispatch 失败)");
        Console.WriteLine($"耗时    : {sw.ElapsedMilliseconds} ms");
        return 2;
    }

    /// <summary>
    /// R24 diagnostic: dumps every text element UIA finds inside a screen
    /// region. This is the UIA tier of the new region-OCR fallback. Use it to
    /// see what UIA would return for a region before OCR runs — if it returns
    /// text, the runtime won't call OCR at all. Exit 0 = got text, 2 = empty,
    /// 3 = error. Usage: --probe-uia-region &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt;
    /// </summary>
    private static int ProbeUiaRegion(int x, int y, int width, int height)
    {
        Console.WriteLine($"Thread apt: {Thread.CurrentThread.GetApartmentState()}");
        Console.WriteLine($"Region   : ({x},{y}) {width}x{height}");
        using var backend = new WindowsUiAutomationBackend();
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string> texts = backend.GetTextsInRegion(new SelectionAssistant.Platform.Windows.Capture.Rect(x, y, width, height));
        sw.Stop();

        Console.WriteLine($"元素数   : {texts.Count}");
        Console.WriteLine($"耗时     : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("---- UIA 文字 ----");
        foreach (string t in texts)
        {
            Console.WriteLine(t);
        }
        Console.WriteLine("------------------");

        return texts.Count > 0 ? 0 : 2;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int ProbeUiAutomationOnMtaThread()
    {
        int exitCode = 2;
        var thread = new Thread(() =>
        {
            using var backend = new WindowsUiAutomationBackend();
            exitCode = backend.ProbeAvailability() ? 0 : 2;
        })
        {
            IsBackground = false,
            Name = "BYH.UIAutomationProbe",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int ProbeClipboardFallback(
        string rootWindowArgument,
        string processIdArgument,
        string expectedText)
    {
        if (!long.TryParse(rootWindowArgument, out long rootWindowValue) ||
            !uint.TryParse(processIdArgument, out uint processId) ||
            rootWindowValue == 0 || processId == 0)
        {
            return 4;
        }

        try
        {
            using var clipboard = new Win32Clipboard();
            using var capture = new Win32ClipboardCapture(
                clipboard,
                new SendInputHelper());
            var gesture = new SelectionGesture(
                MouseUpX: 0,
                MouseUpY: 0,
                MouseDownX: 0,
                MouseDownY: 0,
                MouseDownTimestampMs: 0,
                MouseUpTimestampMs: 1,
                SourceRootHwnd: new nint(rootWindowValue),
                SourceProcessId: processId);

            CaptureResult result = capture
                .CaptureAsync(gesture, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return result.Text == expectedText ? 0 : 2;
        }
        catch
        {
            return 3;
        }
    }

    private static int ProbePolicyAwareCapture(
        string rootWindowArgument,
        string processIdArgument,
        string expectedText)
    {
        if (!long.TryParse(rootWindowArgument, out long rootWindowValue) ||
            !uint.TryParse(processIdArgument, out uint processId) ||
            rootWindowValue == 0 || processId == 0)
        {
            return 4;
        }

        try
        {
            Core.Capture.IProcessCapturePolicyProvider policyProvider =
                WindowsDefaultCapturePolicies.CreateProvider();
            using var capture = new WindowsSelectionTextCapture(policyProvider);
            var gesture = new SelectionGesture(
                MouseUpX: 0,
                MouseUpY: 0,
                MouseDownX: 0,
                MouseDownY: 0,
                MouseDownTimestampMs: 0,
                MouseUpTimestampMs: 1,
                SourceRootHwnd: new nint(rootWindowValue),
                SourceProcessId: processId);

            CaptureResult result = capture
                .CaptureAsync(gesture, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return result.Text == expectedText && !result.IsAmbiguous ? 0 : 2;
        }
        catch
        {
            return 3;
        }
    }
}
