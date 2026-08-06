using System.Threading.Channels;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// Tier 2/3 simulated-copy capture. The original clipboard is restored only
/// when the final sequence and owner still identify the injected source copy;
/// policies that opt into default-copy semantics intentionally keep the
/// captured selection in the clipboard.
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
    // WM_CLIPBOARDUPDATE。注入阶段保守预留 2 次（部分应用会分阶段发布内容），恢复阶段
    // 只预留 1 次（一次 EmptyClipboard + SetClipboardData 只产生一个系统更新通知）。
    // null 表示未接线（历史服务未启用）。非 readonly：由 SetHistoryChangeSuppressor
    // 在 App 组合期设置。
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
    /// 注入剪贴板历史变更抑制回调。需要恢复原剪贴板的注入复制会在发送前
    /// 预留配额，让 ClipboardHistoryService 忽略目标写入与还原写入；保留捕获结果的
    /// 策略不会预留目标写入配额，因为该写入就是用户可见的默认复制。传 null 取消接线。
    /// 线程安全：volatile 写。App 组合期调用一次，之后只读。
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
                invocation.PreserveCapturedClipboard,
                invocation.HistorySuppressionCount,
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
        bool preserveCapturedClipboard,
        int historySuppressionCount,
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
        int pendingHistorySuppression = 0;
        bool capturedClipboardPreserved = false;
        string? capturedClipboardText = null;

        // Each simulated-copy chord that will restore the user's clipboard
        // reserves the policy-specific number of history notifications before
        // sending input. Normal applications use two; GPU/WebView targets
        // such as Warp can publish one logical copy in several transactions.
        // restore. A chord that never changes the
        // clipboard (common for unsupported targets) must release its
        // reservation immediately, otherwise later real Ctrl+C writes can be
        // mistaken for BYH-owned changes and disappear from clipboard history.
        void ReserveHistorySuppression()
        {
            Action<int>? suppressor = _suppressHistoryChanges;
            if (suppressor is null)
            {
                return;
            }

            try
            {
                if (historySuppressionCount <= 0)
                {
                    return;
                }

                suppressor(historySuppressionCount);
                pendingHistorySuppression += historySuppressionCount;
            }
            catch
            {
                // History suppression is best effort; it must never block
                // the actual selection capture.
            }
        }

        void ReleaseHistorySuppression(int amount)
        {
            if (amount <= 0 || pendingHistorySuppression <= 0)
            {
                return;
            }

            int release = Math.Min(amount, pendingHistorySuppression);
            Action<int>? suppressor = _suppressHistoryChanges;
            if (suppressor is not null)
            {
                try { suppressor(-release); }
                catch { }
            }

            pendingHistorySuppression -= release;
        }

        void ReleaseAllHistorySuppression() =>
            ReleaseHistorySuppression(pendingHistorySuppression);

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
                // 先预留策略指定的配额给注入复制本身可能产生的
                // WM_CLIPBOARDUPDATE；稳定变化确认后会回收未消耗部分，finally 再为
                // RestoreOriginalClipboard 单独预留 1 次。
                // sent=false 时立即回滚，避免误吞下次真实复制。
                if (!preserveCapturedClipboard)
                {
                    ReserveHistorySuppression();
                }

                bool sent = _input.SendCopyChord(chord);
                Trace($"clipboard chord={chord} sent={sent} baseline={lastBaseline}");
                if (!sent)
                {
                    // chord 没发出，不会有剪贴板写入，回滚刚设的配额。
                    ReleaseHistorySuppression(historySuppressionCount);
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
                    // No WM_CLIPBOARDUPDATE arrived, so this reservation can
                    // never be consumed by the target. Do not carry it into
                    // the user's next active copy.
                    ReleaseHistorySuppression(historySuppressionCount);
                    continue;
                }

                uint? ownerProcessId = _clipboard.GetOwnerProcessId();
                Trace($"clipboard chord={chord} stable={stableSequence.Value} delta={unchecked(stableSequence.Value - lastBaseline)} owner={ownerProcessId?.ToString() ?? "none"} historyReserve={historySuppressionCount}");
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
                    bool isSameWeChatFamily = ProcessParentage.IsSameWeChatFamily(
                        ownerProcessId.Value,
                        gesture.SourceProcessId);
                    Trace($"clipboard owner mismatch expected={gesture.SourceProcessId} actual={ownerProcessId.Value} descendant={isDescendant} wechatFamily={isSameWeChatFamily}");
                    if (!isDescendant && !isSameWeChatFamily)
                    {
                        // This was an external/user clipboard write. There is
                        // no restore on this path, so release any unused
                        // capture reservation before returning.
                        ReleaseHistorySuppression(historySuppressionCount);
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
                        (ProcessParentage.IsDescendantOf(after, gesture.SourceProcessId) ||
                         ProcessParentage.IsSameWeChatFamily(after, gesture.SourceProcessId)));
                if (_clipboard.GetSequenceNumber() != ownedSequence.Value || ownerChangedDuringRead)
                {
                    Trace("clipboard rejected because sequence/owner changed during read");
                    ReleaseHistorySuppression(historySuppressionCount);
                    externalChangeObserved = true;
                    ownedSequence = null;
                    return NoCapture();
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Trace($"clipboard success source={chord} ownerless={ownerlessResult} length={text.Length}");
                    // Keep the untruncated clipboard text for the ownerless
                    // restore retry below. The retry must prove that the
                    // clipboard still contains the exact text produced by
                    // this capture before it may replace it with the user's
                    // original snapshot.
                    capturedClipboardText = text;
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
                    capturedClipboardPreserved = preserveCapturedClipboard;
                    return new CaptureResult(text, source, truncated);
                }

                // A stable clipboard update with no usable text is not a
                // successful capture and the loop may try another chord. Its
                // reservation has already served its purpose, so release it
                // before the next chord reserves a fresh pair.
                ReleaseHistorySuppression(historySuppressionCount);
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

            if (!capturedClipboardPreserved &&
                !externalChangeObserved &&
                ownedSequence is uint expectedSequence)
            {
                // restore uses one EmptyClipboard + several SetClipboardData
                // calls in a single ownership transaction. Windows emits one
                // WM_CLIPBOARDUPDATE for that transaction, so reserve exactly
                // one notification here. Reserving two leaves a stale quota
                // in applications that emit the normal single notification;
                // the next real user Ctrl+C is then silently discarded by the
                // history listener.
                // The target-copy reservation has already been balanced after
                // the stable change was observed; this reservation belongs only
                // to the restore write. Ownerless GPU/WebView targets can still
                // emit a late write after the stable probe, so keep their
                // remaining target quota alive until restore has settled.
                bool deferHistoryRelease = allowOwnerlessResult;
                if (!deferHistoryRelease)
                {
                    ReleaseAllHistorySuppression();
                }
                Action<int>? suppressor = _suppressHistoryChanges;
                bool restoreSuppressionReserved = false;
                if (suppressor is not null)
                {
                    try
                    {
                        suppressor(1);
                        restoreSuppressionReserved = true;
                    }
                    catch { }
                }

                bool restored = deferHistoryRelease
                    ? RestoreOwnerlessClipboardWithRetry(
                        snapshot,
                        expectedSequence,
                        capturedClipboardText,
                        gesture,
                        options)
                    : RestoreOriginalClipboard(snapshot, expectedSequence);
                Trace($"clipboard restore expected={expectedSequence} success={restored} deferredHistory={deferHistoryRelease}");
                if (!restored && restoreSuppressionReserved)
                {
                    // A sequence-guarded restore may lose a race with a real
                    // user copy. No restore write means its reservation must
                    // be cancelled immediately.
                    try { suppressor!(-1); }
                    catch { }
                }

                if (deferHistoryRelease)
                {
                    ReleaseAllHistorySuppression();
                }
            }
            else
            {
                // No restore will consume the reserved pair (external change,
                // cancellation cleanup failure, or a failed capture). Clear
                // all remaining quota so the next user copy is never hidden.
                ReleaseAllHistorySuppression();
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

    private bool RestoreOriginalClipboard(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        try
        {
            if (snapshot.HasRestorableData)
            {
                return _clipboard.Restore(snapshot, expectedSequence);
            }
            if (snapshot.WasEmpty)
            {
                return _clipboard.Clear(expectedSequence);
            }

            return false;
        }
        catch
        {
            // Best effort. Every native mutation remains sequence guarded.
            return false;
        }
    }

    /// <summary>
    /// Warp's GPU/WebView clipboard can publish a late ownerless write after
    /// the normal stability window. A strict one-shot sequence guard then
    /// refuses to restore the user's clipboard, leaving the selected text in
    /// the system clipboard. Retry only for the ownerless policy, and only
    /// while the clipboard still contains the exact text captured by this
    /// session. Any non-matching text or external owner aborts immediately so
    /// a real user copy is never overwritten.
    /// </summary>
    private bool RestoreOwnerlessClipboardWithRetry(
        ClipboardSnapshot snapshot,
        uint expectedSequence,
        string? capturedText,
        SelectionGesture gesture,
        ClipboardCaptureOptions options)
    {
        if (string.IsNullOrEmpty(capturedText))
        {
            return RestoreOriginalClipboard(snapshot, expectedSequence);
        }

        const int maxAttempts = 8;
        int delayMs = Math.Clamp(
            (int)Math.Ceiling(options.StabilizationDelay.TotalMilliseconds / 2),
            15,
            40);
        uint candidateSequence = expectedSequence;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                Thread.Sleep(delayMs);
            }

            bool restored = RestoreOriginalClipboard(snapshot, candidateSequence);
            if (!restored)
            {
                if (!TryGetOwnerlessCapturedClipboard(
                        capturedText,
                        gesture,
                        out uint currentSequence))
                {
                    Trace($"clipboard ownerless restore aborted attempt={attempt} after sequence guard");
                    return false;
                }

                candidateSequence = currentSequence;
                continue;
            }

            // A successful restore can still race a late Warp transaction.
            // Wait one short interval and verify the selected text did not
            // reappear before declaring the clipboard restored.
            Thread.Sleep(delayMs);
            if (!TryGetOwnerlessCapturedClipboard(
                    capturedText,
                    gesture,
                    out uint postRestoreSequence))
            {
                Trace($"clipboard ownerless restore retry succeeded attempt={attempt} seq={candidateSequence}");
                return true;
            }

            candidateSequence = postRestoreSequence;
            Trace($"clipboard ownerless restore observed late write attempt={attempt} seq={candidateSequence}");
        }

        Trace($"clipboard ownerless restore retry exhausted attempts={maxAttempts}");
        return false;
    }

    private bool TryGetOwnerlessCapturedClipboard(
        string capturedText,
        SelectionGesture gesture,
        out uint sequence)
    {
        uint? owner = _clipboard.GetOwnerProcessId();
        string? currentText = _clipboard.GetText();
        sequence = _clipboard.GetSequenceNumber();
        bool ownerMatches = owner is null ||
            owner == gesture.SourceProcessId ||
            ProcessParentage.IsDescendantOf(owner.Value, gesture.SourceProcessId);
        bool textMatches = string.Equals(currentText, capturedText, StringComparison.Ordinal);
        if (!ownerMatches || !textMatches)
        {
            Trace($"clipboard ownerless restore verification owner={owner?.ToString() ?? "none"} textMatch={textMatches}");
            return false;
        }

        return true;
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
