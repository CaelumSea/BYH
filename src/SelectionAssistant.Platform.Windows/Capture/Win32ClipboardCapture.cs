using System.Threading.Channels;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// Tier 2/3 simulated-copy capture. The original clipboard is restored only
/// when the final sequence and owner still identify the injected source copy.
/// </summary>
public sealed class Win32ClipboardCapture : ISelectionTextCapture, IConfiguredClipboardCapture, IDisposable
{
    private static readonly SimulatedCopyChord[] DefaultChords =
    [
        SimulatedCopyChord.CtrlInsert,
        SimulatedCopyChord.CtrlC,
    ];

    private readonly IClipboardAccess _clipboard;
    private readonly ICopyInputInjector _input;
    private readonly ClipboardCaptureOptions _options;
    private readonly IReadOnlyList<SimulatedCopyChord> _chords;
    private readonly Action<string>? _diagnosticSink;
    // 剪贴板历史抑制回调。注入复制前调用，让 ClipboardHistoryService 忽略接下来 N 次
    // WM_CLIPBOARDUPDATE（注入 1 次 + restore backup 1 次 = 典型 2 次）。null 表示未接线
    // （历史服务未启用）。非 readonly：由 SetHistoryChangeSuppressor 在 App 组合期设置。
    private Action<int>? _suppressHistoryChanges;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private int _disposed;

    public Win32ClipboardCapture(
        IClipboardAccess clipboard,
        ICopyInputInjector input,
        ClipboardCaptureOptions? options = null,
        IReadOnlyList<SimulatedCopyChord>? chords = null,
        Action<string>? diagnosticSink = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _options = (options ?? ClipboardCaptureOptions.Default).Validate();
        _chords = chords ?? DefaultChords;
        _diagnosticSink = diagnosticSink;

        if (_chords.Count == 0)
        {
            throw new ArgumentException("At least one simulated-copy chord is required.", nameof(chords));
        }
    }

    /// <summary>
    /// 注入剪贴板历史变更抑制回调。每次注入复制（chord 真正发出）前会调用
    /// <paramref name="suppress"/> 并传 2，让 ClipboardHistoryService 忽略接下来 2 次
    /// WM_CLIPBOARDUPDATE（注入复制本身 + RestoreOriginalClipboard 还原 backup）。
    /// 传 null 取消接线。线程安全：volatile 写。App 组合期调用一次，之后只读。
    /// </summary>
    public void SetHistoryChangeSuppressor(Action<int>? suppress) =>
        _suppressHistoryChanges = suppress;

    public async Task<CaptureResult> CaptureAsync(SelectionGesture gesture, CancellationToken token)
    {
        return await CaptureAsync(
            gesture,
            new ClipboardCaptureInvocation(_chords),
            token).ConfigureAwait(false);
    }

    public async Task<CaptureResult> CaptureAsync(
        SelectionGesture gesture,
        ClipboardCaptureInvocation invocation,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(invocation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        invocation.Validate();
        ClipboardCaptureOptions effectiveOptions = invocation.StabilizationDelay is { } stabilization
            ? (_options with { StabilizationDelay = stabilization }).Validate()
            : _options;

        await _captureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await CaptureExclusiveAsync(
                gesture,
                invocation.Chords,
                invocation.AllowOwnerlessResult,
                effectiveOptions,
                token).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task<CaptureResult> CaptureExclusiveAsync(
        SelectionGesture gesture,
        IReadOnlyList<SimulatedCopyChord> chords,
        bool allowOwnerlessResult,
        ClipboardCaptureOptions options,
        CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();

        bool initialModifiers = _input.HasInterferingModifiers();
        bool initialTarget = _input.CanInjectInto(gesture);
        Trace($"clipboard start proc={gesture.SourceProcessId} chords={string.Join(',', chords)} modifiers={initialModifiers} target={initialTarget}");
        if (initialModifiers || !initialTarget)
        {
            Trace("clipboard skipped before backup");
            return NoCapture();
        }

        ClipboardSnapshot snapshot = _clipboard.Backup();
        Trace($"clipboard backup ok={snapshot.BackupSucceeded} empty={snapshot.WasEmpty} restorable={snapshot.HasRestorableData} seq={snapshot.SequenceNumber}");
        if (!snapshot.BackupSucceeded ||
            (!snapshot.WasEmpty && !snapshot.HasRestorableData))
        {
            // Safety wins over capture: never inject when existing clipboard
            // content cannot be restored at least in one supported format.
            return NoCapture();
        }

        var monitor = new ClipboardChangeMonitor(_clipboard);
        bool subscribed = false;
        IDisposable? scopedSubscription = null;
        bool inputCommitted = false;
        bool externalChangeObserved = false;
        uint lastBaseline = snapshot.SequenceNumber;
        uint? ownedSequence = null;

        using var overallCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        overallCancellation.CancelAfter(options.OverallTimeout);

        try
        {
            // Clipboard history keeps a long-lived listener on the shared
            // Win32Clipboard. Use a scoped lease when the implementation
            // supports it so this capture can observe the same WM_CLIPBOARDUPDATE
            // stream without replacing or colliding with history's callback.
            if (_clipboard is IScopedClipboardChangeAccess scopedClipboard)
            {
                scopedSubscription = scopedClipboard.SubscribeChangesScoped(monitor.Signal);
            }
            else
            {
                _clipboard.SubscribeChanges(monitor.Signal);
                subscribed = true;
            }

            foreach (SimulatedCopyChord chord in chords)
            {
                overallCancellation.Token.ThrowIfCancellationRequested();

                bool modifiers = _input.HasInterferingModifiers();
                bool target = _input.CanInjectInto(gesture);
                Trace($"clipboard chord={chord} modifiers={modifiers} target={target}");
                if (modifiers || !target)
                {
                    Trace("clipboard skipped before send");
                    return NoCapture();
                }

                lastBaseline = _clipboard.GetSequenceNumber();

                // 抑制必须在 SendCopyChord 之前：目标进程响应剪贴板写入的速度可能极快
                // （WeChatAppEx/CEF ~10-50ms），WM_CLIPBOARDUPDATE 会在 SendCopyChord 返回后
                // 立即投递。若配额在 send 之后才设，第一次变化会在配额就位前到达 → 漏抑制。
                // 配额设 2 次：① 注入复制本身写的剪贴板；② finally 里 RestoreOriginalClipboard
                // 还原 backup 写的剪贴板。sent=false 时回滚 2，避免误吞下次真实复制。
                bool suppressorWired = _suppressHistoryChanges is not null;
                if (suppressorWired)
                {
                    try { _suppressHistoryChanges!.Invoke(2); }
                    catch { suppressorWired = false; } // 抑制失败不影响取词，按未接线处理
                }

                bool sent = _input.SendCopyChord(chord);
                Trace($"clipboard chord={chord} sent={sent} baseline={lastBaseline}");
                if (!sent)
                {
                    // chord 没发出，不会有剪贴板写入，回滚刚设的配额。
                    if (suppressorWired)
                    {
                        try { _suppressHistoryChanges!.Invoke(-2); }
                        catch { }
                    }
                    continue;
                }

                inputCommitted = true;
                uint? stableSequence = await monitor.WaitForStableChangeAsync(
                    lastBaseline,
                    options.ChangeTimeout,
                    options.StabilizationDelay,
                    overallCancellation.Token).ConfigureAwait(false);

                if (stableSequence is null)
                {
                    Trace($"clipboard chord={chord} no sequence change");
                    continue;
                }

                uint? ownerProcessId = _clipboard.GetOwnerProcessId();
                Trace($"clipboard chord={chord} stable={stableSequence.Value} owner={ownerProcessId?.ToString() ?? "none"}");
                bool ownerlessResult = ownerProcessId is null && allowOwnerlessResult;
                if (ownerProcessId is null && !ownerlessResult)
                {
                    // The source process may exit after placing data. Preserve
                    // the original clipboard, but do not report unowned text as
                    // a successful capture.
                    ownedSequence = stableSequence.Value;
                    return NoCapture();
                }

                if (ownerProcessId is not null && ownerProcessId != gesture.SourceProcessId)
                {
                    // 套壳应用（微信 Weixin→WeChatAppEx、Electron 主→renderer/GPU）会把复制
                    // 路由到子进程，剪贴板属主是子 PID，与鼠标手势时记录的根窗口 PID 不同。
                    // 沿父链认后代：是 source 后代则视为同源，正常取词并让 finally 走 restore
                    // 分支（不设 externalChangeObserved）。判定失败（受保护进程等）返回 false，
                    // 走原有拒绝路径，零回归。
                    bool isDescendant = ProcessParentage.IsDescendantOf(ownerProcessId.Value, gesture.SourceProcessId);
                    Trace($"clipboard owner mismatch expected={gesture.SourceProcessId} actual={ownerProcessId.Value} descendant={isDescendant}");
                    if (!isDescendant)
                    {
                        externalChangeObserved = true;
                        return NoCapture();
                    }
                }

                // Some GPU/WebView terminals (including Warp) place clipboard
                // data without an owner HWND, so Win32 cannot map it back to a
                // process. The caller must opt in per policy; sequence change
                // + target validation still protect the normal capture path.

                ownedSequence = stableSequence.Value;
                string? text = _clipboard.GetText();

                // 读文本期间 owner 变了 = 有别的进程抢剪贴板 = 不可信。对套壳子进程
                // 场景（owner 是 source 的后代），这里复用同样的父子判定：owner 仍是
                // source 或其后代视为"没变"。ownerlessResult 路径保持原语义（有 owner 就拒）。
                uint? ownerAfterRead = _clipboard.GetOwnerProcessId();
                bool ownerChangedDuringRead = ownerlessResult
                    ? ownerAfterRead is not null
                    : ownerAfterRead != gesture.SourceProcessId &&
                      !(ownerAfterRead is uint after &&
                        ProcessParentage.IsDescendantOf(after, gesture.SourceProcessId));
                if (_clipboard.GetSequenceNumber() != ownedSequence.Value || ownerChangedDuringRead)
                {
                    Trace("clipboard rejected because sequence/owner changed during read");
                    externalChangeObserved = true;
                    ownedSequence = null;
                    return NoCapture();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Trace($"clipboard success source={chord} ownerless={ownerlessResult} length={text.Length}");
                    bool truncated = text.Length > options.MaxTextLength;
                    if (truncated)
                    {
                        text = text[..options.MaxTextLength];
                    }

                    CaptureSource source = chord switch
                    {
                        SimulatedCopyChord.CtrlInsert => CaptureSource.SimulatedCopyCtrlInsert,
                        SimulatedCopyChord.CtrlShiftC => CaptureSource.SimulatedCopyCtrlShiftC,
                        _ => CaptureSource.SimulatedCopyCtrlC,
                    };
                    return new CaptureResult(text, source, truncated);
                }
            }

            return NoCapture();
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            if (inputCommitted && !externalChangeObserved && ownedSequence is null)
            {
                ownedSequence = await TryObserveOwnedChangeAfterCancellationAsync(
                    monitor,
                    lastBaseline,
                    gesture.SourceProcessId,
                    options).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            // Internal overall timeout. Cleanup still runs below.
            return NoCapture();
        }
        catch (Exception exception)
        {
            Trace($"clipboard failed type={exception.GetType().Name} message={exception.Message}");
            return NoCapture();
        }
        finally
        {
            if (subscribed)
            {
                try
                {
                    _clipboard.UnsubscribeChanges();
                }
                catch
                {
                    // Restoration below remains sequence guarded.
                }
            }

            try
            {
                scopedSubscription?.Dispose();
            }
            catch
            {
                // Restoration below remains sequence guarded.
            }

            if (!externalChangeObserved && ownedSequence is uint expectedSequence)
            {
                // restore 即将写剪贴板（可能写多个格式 → 多次 seq 变化），单独补配额，
                // 覆盖 restore 产生的所有 WM_CLIPBOARDUPDATE。这样不依赖"注入复制产生几次
                // 变化"的猜测——chord 前设的配额覆盖注入，这里覆盖 restore。
                if (_suppressHistoryChanges is not null)
                {
                    try { _suppressHistoryChanges.Invoke(2); }
                    catch { }
                }
                RestoreOriginalClipboard(snapshot, expectedSequence);
            }

            // P2 memory: release the ArrayPool-rented CF_DIB buffer now that
            // Restore (if it ran) has copied the bytes into a fresh HGLOBAL, or
            // the capture was abandoned (NoCapture). Without this the rented
            // buffer (up to MaxDibBytes = 32 MB) waits for GC, defeating the
            // whole point of pooling on NativeAOT's non-compacting LOH. Restore
            // completes synchronously above, so the buffer is no longer needed.
            snapshot.Dispose();
        }
    }

    private async Task<uint?> TryObserveOwnedChangeAfterCancellationAsync(
        ClipboardChangeMonitor monitor,
        uint baseline,
        uint sourceProcessId,
        ClipboardCaptureOptions options)
    {
        using var cleanupCancellation =
            new CancellationTokenSource(options.CancellationCleanupTimeout);

        try
        {
            uint? sequence = await monitor.WaitForStableChangeAsync(
                baseline,
                options.CancellationCleanupTimeout,
                options.StabilizationDelay,
                cleanupCancellation.Token).ConfigureAwait(false);

            uint? ownerProcessId = _clipboard.GetOwnerProcessId();
            return sequence is not null &&
                   (ownerProcessId is null || ownerProcessId == sourceProcessId)
                ? sequence
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void RestoreOriginalClipboard(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        try
        {
            if (snapshot.HasRestorableData)
            {
                _clipboard.Restore(snapshot, expectedSequence);
            }
            else if (snapshot.WasEmpty)
            {
                _clipboard.Clear(expectedSequence);
            }
        }
        catch
        {
            // Best effort. Every native mutation remains sequence guarded.
        }
    }

    private static CaptureResult NoCapture() =>
        new(null, CaptureSource.None, false);

    private void Trace(string message)
    {
        try
        {
            _diagnosticSink?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never affect capture or clipboard restoration.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Clipboard cleanup after cancellation is intentionally non-cancellable.
        // Wait for that bounded cleanup before the owning Win32Clipboard is torn down.
        TimeSpan shutdownWait = _options.OverallTimeout + _options.CancellationCleanupTimeout;
        if (_captureGate.Wait(shutdownWait))
        {
            _captureGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class ClipboardChangeMonitor
    {
        private readonly IClipboardAccess _clipboard;
        private readonly Channel<byte> _notifications = Channel.CreateUnbounded<byte>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        public ClipboardChangeMonitor(IClipboardAccess clipboard)
        {
            _clipboard = clipboard;
        }

        public void Signal() => _notifications.Writer.TryWrite(0);

        public async Task<uint?> WaitForStableChangeAsync(
            uint baseline,
            TimeSpan changeTimeout,
            TimeSpan stabilizationDelay,
            CancellationToken token)
        {
            DrainNotifications();
            uint current = _clipboard.GetSequenceNumber();

            if (current == baseline)
            {
                using var firstChangeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(token);
                firstChangeCancellation.CancelAfter(changeTimeout);

                try
                {
                    while (current == baseline)
                    {
                        await _notifications.Reader.ReadAsync(firstChangeCancellation.Token)
                            .ConfigureAwait(false);
                        DrainNotifications();
                        current = _clipboard.GetSequenceNumber();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return null;
                }
            }

            while (true)
            {
                Task delay = Task.Delay(stabilizationDelay, token);
                Task<bool> notification = _notifications.Reader
                    .WaitToReadAsync(token)
                    .AsTask();

                await Task.WhenAny(delay, notification).ConfigureAwait(false);
                if (notification.IsCompleted)
                {
                    if (!await notification.ConfigureAwait(false))
                    {
                        return _clipboard.GetSequenceNumber();
                    }

                    DrainNotifications();
                    continue;
                }

                await delay.ConfigureAwait(false);
                if (_notifications.Reader.TryRead(out _))
                {
                    DrainNotifications();
                    continue;
                }

                return _clipboard.GetSequenceNumber();
            }
        }

        private void DrainNotifications()
        {
            while (_notifications.Reader.TryRead(out _))
            {
            }
        }
    }
}
