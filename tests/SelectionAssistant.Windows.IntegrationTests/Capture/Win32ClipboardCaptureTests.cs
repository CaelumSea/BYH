using SelectionAssistant.Platform.Abstractions;
using SelectionAssistant.Platform.Windows.Capture;
using SelectionAssistant.Platform.Windows.Clipboard;
using Xunit;

namespace SelectionAssistant.Windows.IntegrationTests.Capture;

public sealed class Win32ClipboardCaptureTests
{
    private static readonly ClipboardCaptureOptions FastOptions = new(
        ChangeTimeout: TimeSpan.FromMilliseconds(70),
        StabilizationDelay: TimeSpan.FromMilliseconds(15),
        CancellationCleanupTimeout: TimeSpan.FromMilliseconds(160),
        OverallTimeout: TimeSpan.FromMilliseconds(400),
        MaxTextLength: 100);

    [Fact]
    public async Task EmptyClipboard_IsClearedAfterSuccessfulCapture()
    {
        var clipboard = new FakeClipboard(wasEmpty: true);
        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal(CaptureSource.SimulatedCopyCtrlInsert, result.Source);
        Assert.Equal(1, clipboard.ClearCalls);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task TextWithAdditionalFormats_RestoresSupportedText()
    {
        var clipboard = new FakeClipboard(text: "original");
        clipboard.PrivateFormatsPresent = true;
        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal("original", clipboard.Text);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task OversizedImageWithoutRestorableFormat_AbortsBeforeInput()
    {
        var clipboard = new FakeClipboard
        {
            UnsupportedNonEmptyContent = true,
        };
        var input = new FakeInput();
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(0, input.SendCount);
        Assert.Equal(0, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task FileList_IsRestoredAfterCapture()
    {
        string[] originalFiles = [@"C:\one.txt", @"C:\two.txt"];
        var clipboard = new FakeClipboard(files: originalFiles);
        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal(originalFiles, clipboard.Files);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task DelayedOrUnmaterializableBackup_AbortsBeforeInput()
    {
        var clipboard = new FakeClipboard
        {
            BackupAvailable = false,
        };
        var input = new FakeInput();
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(0, input.SendCount);
    }

    [Fact]
    public async Task ClipboardOwnerExitsAfterCopy_RestoresButDoesNotReturnUnownedText()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                _ = clipboard.WriteAfterAsync("selected", ownerProcessId: null, delayMs: 5);
                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal("original", clipboard.Text);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task OwnerlessCtrlShiftCText_IsAcceptedWhenPolicyOptsIn()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                if (chord == SimulatedCopyChord.CtrlShiftC)
                {
                    _ = clipboard.WriteAfterAsync("selected", ownerProcessId: null, delayMs: 5);
                }

                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(
            Gesture(),
            new ClipboardCaptureInvocation(
                [SimulatedCopyChord.CtrlShiftC],
                AllowOwnerlessResult: true),
            CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal(CaptureSource.SimulatedCopyCtrlShiftC, result.Source);
        Assert.Equal("original", clipboard.Text);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task OwnerlessCtrlShiftC_RetriesRestoreAfterLateSameTextWrite()
    {
        var clipboard = new FakeClipboard(text: "original");
        int restoreAttempts = 0;
        clipboard.BeforeRestore = () =>
        {
            // Simulate Warp's late ownerless transaction arriving between the
            // stability probe and the sequence-guarded restore.
            if (Interlocked.Increment(ref restoreAttempts) == 1)
            {
                clipboard.Write("selected", ownerProcessId: null);
            }
        };
        var input = new FakeInput
        {
            OnSend = _chord =>
            {
                _ = clipboard.WriteAfterAsync("selected", ownerProcessId: null, delayMs: 5);
                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(
            Gesture(),
            new ClipboardCaptureInvocation(
                [SimulatedCopyChord.CtrlShiftC],
                AllowOwnerlessResult: true,
                HistorySuppressionCount: 8),
            CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal("original", clipboard.Text);
        Assert.True(clipboard.RestoreCalls >= 2);
        Assert.Equal(1, clipboard.SuccessfulRestores);
    }

    [Fact]
    public async Task PreserveCapturedClipboard_LeavesSelectedTextInClipboard()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(
            Gesture(),
            new ClipboardCaptureInvocation(
                [SimulatedCopyChord.CtrlC],
                PreserveCapturedClipboard: true),
            CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal(CaptureSource.SimulatedCopyCtrlC, result.Source);
        Assert.Equal("selected", clipboard.Text);
        Assert.Equal(0, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task ScopedCaptureSubscription_PreservesLongLivedClipboardListener()
    {
        var clipboard = new FakeClipboard(text: "original");
        int historyNotifications = 0;
        clipboard.SubscribeChanges(() => historyNotifications++);

        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        int notificationsAfterCapture = historyNotifications;

        clipboard.Write("later", ownerProcessId: 99);

        Assert.Equal(notificationsAfterCapture + 1, historyNotifications);
    }

    [Fact]
    public async Task SimulatedCopyWithoutClipboardChange_ReleasesHistorySuppressionReservation()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            // The target accepts the injected chord but never writes to the
            // clipboard. This is the common unsupported-target path that used
            // to leave two suppression reservations per chord behind.
            OnSend = _ => true,
        };
        int pendingSuppression = 0;
        using var capture = CreateCapture(clipboard, input);
        capture.SetHistoryChangeSuppressor(delta => pendingSuppression += delta);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(0, pendingSuppression);
    }

    [Fact]
    public async Task ExternalClipboardWrite_ReleasesCaptureSuppressionReservation()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                _ = clipboard.WriteAfterAsync("user copy", ownerProcessId: 99, delayMs: 5);
                return true;
            },
        };
        int pendingSuppression = 0;
        using var capture = CreateCapture(clipboard, input);
        capture.SetHistoryChangeSuppressor(delta => pendingSuppression += delta);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal("user copy", clipboard.Text);
        Assert.Equal(0, pendingSuppression);
    }

    [Fact]
    public async Task FailedRestore_ReleasesRestoreSuppressionReservation()
    {
        var clipboard = new FakeClipboard(text: "original");
        clipboard.BeforeRestore = () => clipboard.Write("user copy", ownerProcessId: 99);
        int pendingSuppression = 0;
        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);
        capture.SetHistoryChangeSuppressor(delta => pendingSuppression += delta);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal("user copy", clipboard.Text);
        Assert.Equal(0, pendingSuppression);
    }

    [Fact]
    public async Task UserCopiesDuringCapture_UserContentWinsAndIsNotReportedAsSelection()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                _ = clipboard.WriteAfterAsync("user copy", ownerProcessId: 99, delayMs: 5);
                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal("user copy", clipboard.Text);
        Assert.Equal(0, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task UserCopiesDuringRestore_SequenceRecheckPreservesUserContent()
    {
        var clipboard = new FakeClipboard(text: "original");
        clipboard.BeforeRestore = () => clipboard.Write("user copy", ownerProcessId: 99);
        var input = SourceCopyingInput(clipboard, "selected");
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal("user copy", clipboard.Text);
        Assert.Equal(1, clipboard.RestoreCalls);
        Assert.Equal(0, clipboard.SuccessfulRestores);
    }

    [Fact]
    public async Task ThreeClipboardUpdates_UsesFinalStableText()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5);
                    clipboard.Write("part", Gesture().SourceProcessId);
                    await Task.Delay(5);
                    clipboard.Write("partial", Gesture().SourceProcessId);
                    await Task.Delay(5);
                    clipboard.Write("final text", Gesture().SourceProcessId);
                });
                return true;
            },
        };
        using var capture = new Win32ClipboardCapture(
            clipboard,
            input,
            FastOptions with
            {
                StabilizationDelay = TimeSpan.FromMilliseconds(80),
                ChangeTimeout = TimeSpan.FromMilliseconds(200),
            });

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("final text", result.Text);
        Assert.Equal("original", clipboard.Text);
    }

    [Fact]
    public async Task CancellationAfterInput_WaitsForOwnedUpdateAndRestores()
    {
        var clipboard = new FakeClipboard(text: "original");
        using var cancellation = new CancellationTokenSource();
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                _ = clipboard.WriteAfterAsync("selected", Gesture().SourceProcessId, delayMs: 20);
                cancellation.Cancel();
                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture.CaptureAsync(Gesture(), cancellation.Token));

        Assert.Equal("original", clipboard.Text);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task ClipboardUnavailableForTimeout_DoesNotInjectOrMutate()
    {
        var clipboard = new FakeClipboard(text: "original")
        {
            BackupAvailable = false,
        };
        var input = new FakeInput();
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal("original", clipboard.Text);
        Assert.Equal(0, input.SendCount);
    }

    [Fact]
    public async Task CtrlInsertWithoutUpdate_FallsBackToCtrlC()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                if (chord == SimulatedCopyChord.CtrlC)
                {
                    _ = clipboard.WriteAfterAsync("selected", Gesture().SourceProcessId, delayMs: 5);
                }

                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Equal("selected", result.Text);
        Assert.Equal(CaptureSource.SimulatedCopyCtrlC, result.Source);
        Assert.Equal([SimulatedCopyChord.CtrlInsert, SimulatedCopyChord.CtrlC], input.SentChords);
    }

    [Fact]
    public async Task PerRequestPolicy_CanRestrictCaptureToCtrlInsertOnly()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput
        {
            OnSend = chord =>
            {
                if (chord == SimulatedCopyChord.CtrlC)
                {
                    _ = clipboard.WriteAfterAsync("must not be reached", Gesture().SourceProcessId, delayMs: 5);
                }

                return true;
            },
        };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(
            Gesture(),
            new ClipboardCaptureInvocation([SimulatedCopyChord.CtrlInsert]),
            CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal([SimulatedCopyChord.CtrlInsert], input.SentChords);
        Assert.Equal("original", clipboard.Text);
    }

    [Fact]
    public async Task HeldModifier_AbortsWithoutSendingChord()
    {
        var clipboard = new FakeClipboard(text: "original");
        var input = new FakeInput { InterferingModifiers = true };
        using var capture = CreateCapture(clipboard, input);

        CaptureResult result = await capture.CaptureAsync(Gesture(), CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(0, input.SendCount);
        Assert.Equal("original", clipboard.Text);
    }

    [Fact]
    public void NativeClipboard_MessageListenerCanStartAndStopWithoutMutation()
    {
        using var clipboard = new Win32Clipboard();
        clipboard.SubscribeChanges(() => { });
        clipboard.UnsubscribeChanges();

        Assert.True(clipboard.GetSequenceNumber() >= 0);
    }

    [Fact]
    public void NativeClipboard_AllowsScopedListenerAlongsideLongLivedListener()
    {
        using var clipboard = new Win32Clipboard();
        clipboard.SubscribeChanges(() => { });
        using IDisposable scoped = clipboard.SubscribeChangesScoped(() => { });

        scoped.Dispose();
        clipboard.UnsubscribeChanges();

        Assert.True(clipboard.GetSequenceNumber() >= 0);
    }

    private static Win32ClipboardCapture CreateCapture(
        FakeClipboard clipboard,
        FakeInput input) =>
        new(clipboard, input, FastOptions);

    // Writes synchronously inside SendCopyChord rather than via fire-and-forget
    // WriteAfterAsync. The original 5ms Task.Delay continuation queued onto the
    // thread pool, and on loaded CI runners that scheduling delay could exceed
    // ChangeTimeout (70ms), making the monitor misjudge "no sequence change"
    // and flake success-path tests. Synchronous write still correctly models the
    // sequence: baseline is read BEFORE SendCopyChord, so the Write (which bumps
    // sequence) is visible to WaitForStableChangeAsync when it starts. This only
    // affects the success-path helper; tests that specifically exercise async or
    // ownerless timing still use WriteAfterAsync directly.
    private static FakeInput SourceCopyingInput(FakeClipboard clipboard, string text) => new()
    {
        OnSend = chord =>
        {
            clipboard.Write(text, Gesture().SourceProcessId);
            return true;
        },
    };

    private static SelectionGesture Gesture() => new(
        MouseUpX: 100,
        MouseUpY: 200,
        MouseDownX: 80,
        MouseDownY: 200,
        MouseDownTimestampMs: 10,
        MouseUpTimestampMs: 20,
        SourceRootHwnd: 1,
        SourceProcessId: 42);

    private sealed class FakeInput : ICopyInputInjector
    {
        public bool InterferingModifiers { get; set; }

        public bool CanInject { get; set; } = true;

        public Func<SimulatedCopyChord, bool>? OnSend { get; set; }

        public List<SimulatedCopyChord> SentChords { get; } = [];

        public int SendCount => SentChords.Count;

        public bool HasInterferingModifiers() => InterferingModifiers;

        public bool CanInjectInto(SelectionGesture gesture) => CanInject;

        public bool SendCopyChord(SimulatedCopyChord chord)
        {
            SentChords.Add(chord);
            return OnSend?.Invoke(chord) ?? false;
        }
    }

    private sealed class FakeClipboard : IClipboardAccess, IScopedClipboardChangeAccess
    {
        private readonly object _gate = new();
        private Action? _onChanged;
        private readonly List<Action> _scopedCallbacks = [];
        private uint _sequence = 10;
        private uint? _ownerProcessId;

        public FakeClipboard(
            string? text = null,
            byte[]? imageDib = null,
            string[]? files = null,
            bool wasEmpty = false)
        {
            Text = text;
            ImageDib = imageDib;
            Files = files;
            WasEmpty = wasEmpty;
        }

        public string? Text { get; private set; }

        public byte[]? ImageDib { get; private set; }

        public string[]? Files { get; private set; }

        public bool WasEmpty { get; private set; }

        public bool BackupAvailable { get; set; } = true;

        public bool UnsupportedNonEmptyContent { get; set; }

        public bool PrivateFormatsPresent { get; set; }

        public Action? BeforeRestore { get; set; }

        public int RestoreCalls { get; private set; }

        public int SuccessfulRestores { get; private set; }

        public int ClearCalls { get; private set; }

        public uint GetSequenceNumber()
        {
            lock (_gate)
            {
                return _sequence;
            }
        }

        public uint? GetOwnerProcessId()
        {
            lock (_gate)
            {
                return _ownerProcessId;
            }
        }

        public ClipboardSnapshot Backup()
        {
            lock (_gate)
            {
                if (!BackupAvailable)
                {
                    return ClipboardSnapshot.Unavailable(_sequence);
                }

                if (UnsupportedNonEmptyContent)
                {
                    return new ClipboardSnapshot(
                        _sequence,
                        null,
                        null,
                        null,
                        BackupSucceeded: true,
                        WasEmpty: false);
                }

                return new ClipboardSnapshot(
                    _sequence,
                    Text,
                    ImageDib?.ToArray(),
                    Files?.ToArray(),
                    BackupSucceeded: true,
                    WasEmpty: WasEmpty);
            }
        }

        public bool Restore(ClipboardSnapshot snapshot, uint expectedSequence)
        {
            RestoreCalls++;
            BeforeRestore?.Invoke();

            lock (_gate)
            {
                if (_sequence != expectedSequence)
                {
                    return false;
                }

                Text = snapshot.Text;
                ImageDib = snapshot.ImageDib?.ToArray();
                Files = snapshot.Files?.ToArray();
                WasEmpty = snapshot.WasEmpty;
                _ownerProcessId = null;
                _sequence++;
                SuccessfulRestores++;
                return true;
            }
        }

        public bool Clear(uint expectedSequence)
        {
            ClearCalls++;
            lock (_gate)
            {
                if (_sequence != expectedSequence)
                {
                    return false;
                }

                Text = null;
                ImageDib = null;
                Files = null;
                WasEmpty = true;
                _ownerProcessId = null;
                _sequence++;
                return true;
            }
        }

        public string? GetText()
        {
            lock (_gate)
            {
                return Text;
            }
        }

        public void SubscribeChanges(Action onChanged)
        {
            lock (_gate)
            {
                _onChanged = onChanged;
            }
        }

        public void UnsubscribeChanges()
        {
            lock (_gate)
            {
                _onChanged = null;
            }
        }

        public IDisposable SubscribeChangesScoped(Action onChanged)
        {
            lock (_gate)
            {
                _scopedCallbacks.Add(onChanged);
            }

            return new CallbackLease(this, onChanged);
        }

        private void UnsubscribeScoped(Action callback)
        {
            lock (_gate)
            {
                _scopedCallbacks.Remove(callback);
            }
        }

        public async Task WriteAfterAsync(string? text, uint? ownerProcessId, int delayMs)
        {
            await Task.Delay(delayMs);
            Write(text, ownerProcessId);
        }

        public void Write(string? text, uint? ownerProcessId)
        {
            Action[] callbacks;
            lock (_gate)
            {
                Text = text;
                ImageDib = null;
                Files = null;
                WasEmpty = text is null;
                _ownerProcessId = ownerProcessId;
                _sequence++;
                int legacyCount = _onChanged is null ? 0 : 1;
                callbacks = new Action[legacyCount + _scopedCallbacks.Count];
                int index = 0;
                if (_onChanged is { } legacyCallback)
                {
                    callbacks[index++] = legacyCallback;
                }

                _scopedCallbacks.CopyTo(callbacks, index);
            }

            foreach (Action callback in callbacks)
            {
                callback();
            }
        }

        private sealed class CallbackLease : IDisposable
        {
            private FakeClipboard? _owner;
            private readonly Action _callback;

            public CallbackLease(FakeClipboard owner, Action callback)
            {
                _owner = owner;
                _callback = callback;
            }

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.UnsubscribeScoped(_callback);
        }
    }
}
