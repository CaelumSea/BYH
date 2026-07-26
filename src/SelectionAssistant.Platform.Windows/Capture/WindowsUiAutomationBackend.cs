using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SelectionAssistant.Platform.Abstractions;

namespace SelectionAssistant.Platform.Windows.Capture;

/// <summary>
/// Minimal AOT-safe UI Automation client built directly on the COM vtables from
/// the Windows SDK UIAutomationClient.h. No WPF assemblies or runtime COM wrappers
/// are used.
/// </summary>
public sealed unsafe class WindowsUiAutomationBackend : IUiAutomationBackend, IDisposable
{
    private const uint GaRoot = 2;
    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitMultithreaded = 0x0;
    private const int UiaTextPatternId = 10014;
    private const int UiaTextPattern2Id = 10024;
    private const int UiaValuePatternId = 10002;

    // R24 track A: ancestor walk widened from 5 -> 8 to mirror SnapTraTranslator's
    // 8-level ancestor chain. NOTE (R33): ancestor walk was removed because
    // walking up to ancestor TextPatterns surfaced false positives — their
    // getSelection returned degenerate or whole-document ranges even when the
    // user hadn't selected anything. Only direct candidates are read now.
    // Cap on DocumentRange/Value text we will read; avoids pulling a giant
    // document body when the element is an editor/document root.
    private const int MaxElementTextChars = 4000;

    private static readonly Guid ClsidCuiAutomation8 =
        new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IidIUiAutomation =
        new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private static readonly Guid IidIUiAutomationTextPattern =
        new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");
    private static readonly Guid IidIUiAutomationTextPattern2 =
        new("506a921a-fcc9-409f-b23b-37eb74106872");
    private static readonly Guid IidIUiAutomationValuePattern =
        new("a94cd8b1-0844-4cd6-9d2d-640537ab39e9");

    private nint _automation;
    private nint _controlViewWalker;
    private bool _comInitialized;
    private bool _disposed;

    // R24 region-OCR: the bounds query is called from the UI thread (STA), but
    // EnsureInitialized() calls CoInitializeEx(COINIT_MULTITHREADED), which
    // returns RPC_E_CHANGED_MODE on an STA thread (Avalonia sets up the UI
    // thread as STA). To keep COM apartment + thread-affinity correct, all
    // region-OCR work runs on this dedicated MTA worker thread — mirroring how
    // phase 1's UiAutomationWorker pins its backend to a single MTA thread.
    // Lazy-started on first GetElementBoundsAt call; reused for the backend's
    // lifetime so the cached _automation COM pointer is always touched from the
    // same thread that created it.
    private Thread? _boundsThread;
    private readonly BlockingCollection<Action> _boundsQueue = new();

    public WindowsUiAutomationBackend()
    {
    }

    public UiAutomationReadResult ReadSelection(SelectionGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!SourceContextStillMatches(gesture) || !EnsureInitialized())
        {
            return new UiAutomationReadResult(null);
        }

        nint focusedElement = 0;
        nint elementUnderMouse = 0;
        nint rootElement = 0;

        try
        {
            focusedElement = GetFocusedElement();
            elementUnderMouse = ElementFromPoint(gesture.MouseUpX, gesture.MouseUpY);

            // R24 track A: multi-root strategy mirroring SnapTraTranslator's
            // system-wide / focused-window / focused-app candidate roots.
            // Priority order: hit-tested element first (what the user pointed at),
            // then focused element, then the desktop root as last resort.
            nint* candidates = stackalloc nint[3]
            {
                elementUnderMouse,
                focusedElement,
                0,
            };
            rootElement = GetRootElement();
            candidates[2] = rootElement;

            // Pass 1: read the *selection* on every direct candidate (TextPattern2
            // then TextPattern). Selection is the most precise source.
            for (int index = 0; index < 3; index++)
            {
                string? selectedText = TryReadSelectedText(
                    candidates[index],
                    gesture.SourceProcessId);
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    return new UiAutomationReadResult(selectedText);
                }
            }

            // Pass 2 (ancestor walk) and Pass 3 (element-text fallback) both removed.
            //
            // Why: when the user dragged across whitespace without selecting text,
            // these passes still surfaced text from a nearby toolbar/label/list-item
            // by walking up to an ancestor TextPattern whose getSelection returned
            // a degenerate or whole-document range. That non-empty text was treated
            // as a real selection and the toolbar popped with nothing selected.
            //
            // Selection-only now: if Pass 1 found no real selection on the hit-tested
            // element, the focused element, or the desktop root, return null and let
            // the downstream tiers (clipboard Ctrl+C simulation / vision OCR) decide.
            // Real selections always live on the element the user clicked or the
            // element that has keyboard focus — never several ancestors up.
            return new UiAutomationReadResult(null);
        }
        finally
        {
            Release(focusedElement);
            Release(elementUnderMouse);
            Release(rootElement);
        }
    }

    /// <summary>
    /// Creates the native CUIAutomation8 client and performs a harmless focused
    /// element query. Intended for startup diagnostics and integration tests.
    /// Must be called on the same thread that will own this backend.
    /// </summary>
    public bool ProbeAvailability()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EnsureInitialized())
        {
            return false;
        }

        nint focusedElement = GetFocusedElement();
        Release(focusedElement);
        return true;
    }

    private bool EnsureInitialized()
    {
        if (_automation != 0)
        {
            return true;
        }

        int initializeResult = CoInitializeEx(0, CoinitMultithreaded);
        // Audit H5: only S_OK (0) means "we initialized COM on this thread"
        // and must pair with a matching CoUninitialize. S_FALSE (1) means the
        // thread was already initialized (by us or another component) — calling
        // CoUninitialize on S_FALSE would decrement a reference count we don't
        // own, eventually unbalancing COM and breaking other consumers on this
        // thread. So we record _comInitialized ONLY on S_OK. Negative results
        // (RPC_E_CHANGED_MODE etc.) remain a hard failure as before.
        if (initializeResult == 0) // S_OK
        {
            _comInitialized = true;
        }
        else if (initializeResult == 1) // S_FALSE — already initialized; we don't own the uninit
        {
            _comInitialized = false;
        }
        else
        {
            return false;
        }

        Guid classId = ClsidCuiAutomation8;
        Guid interfaceId = IidIUiAutomation;
        fixed (nint* automationPointer = &_automation)
        {
            int createResult = CoCreateInstance(
                &classId,
                0,
                ClsctxInprocServer,
                &interfaceId,
                automationPointer);
            if (createResult < 0 || _automation == 0)
            {
                DisposeComState();
                return false;
            }
        }

        delegate* unmanaged[Stdcall]<nint, nint*, int> getControlViewWalker =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(_automation, 14);

        fixed (nint* walkerPointer = &_controlViewWalker)
        {
            int walkerResult = getControlViewWalker(_automation, walkerPointer);
            if (walkerResult < 0 || _controlViewWalker == 0)
            {
                DisposeComState();
                return false;
            }
        }

        return true;
    }

    private nint GetFocusedElement()
    {
        nint element = 0;
        delegate* unmanaged[Stdcall]<nint, nint*, int> getFocusedElement =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(_automation, 8);

        int result = getFocusedElement(_automation, &element);
        return result >= 0 ? element : 0;
    }

    private nint ElementFromPoint(int x, int y)
    {
        nint element = 0;
        var point = new NativePoint(x, y);
        delegate* unmanaged[Stdcall]<nint, NativePoint, nint*, int> elementFromPoint =
            (delegate* unmanaged[Stdcall]<nint, NativePoint, nint*, int>)GetVtableSlot(_automation, 7);

        int result = elementFromPoint(_automation, point, &element);
        return result >= 0 ? element : 0;
    }

    // IUIAutomation::GetRootElement — vtable slot 5.
    private nint GetRootElement()
    {
        nint element = 0;
        delegate* unmanaged[Stdcall]<nint, nint*, int> getRootElement =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(_automation, 5);

        int result = getRootElement(_automation, &element);
        return result >= 0 ? element : 0;
    }

    /// <summary>
    /// R24 track B: returns the bounding rectangle (screen coords) of the element
    /// under the given point, or null if UIA cannot resolve one. Used to scope the
    /// screenshot region for the vision OCR tier.
    /// </summary>
    /// <remarks>
    /// This is callable from the UI thread (STA). UIA's COM apartment must be
    /// MTA (CoInitializeEx with COINIT_MULTITHREADED returns RPC_E_CHANGED_MODE
    /// on an STA thread), so the actual query runs on the backend's dedicated
    /// MTA worker thread (see <see cref="_boundsThread"/>). Blocks until the
    /// query completes (UIA at a point is fast, typically &lt;30ms).
    /// </remarks>
    public Rect? GetElementBoundsAt(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureBoundsThread();

        Rect? result = null;
        Exception? caught = null;
        var done = new ManualResetEventSlim(false);

        _boundsQueue.Add(() =>
        {
            try
            {
                result = GetElementBoundsAtCore(x, y);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                done.Set();
            }
        });

        // Wait for the MTA thread. The query is fast; cap the wait so a wedged
        // UIA provider can never freeze the UI thread indefinitely.
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            return null;
        }

        return caught is null ? result : null;
    }

    /// <summary>
    /// R24 region-OCR fallback: walks the UI Automation tree of the window(s)
    /// inside <paramref name="region"/> and collects every text-bearing
    /// element's text (Name, TextPattern DocumentRange, ValuePattern Value),
    /// deduplicated and ordered top-to-bottom, left-to-right for natural
    /// reading order. Empty list means UIA found nothing readable in the region
    /// (caller should fall back to OCR).
    /// </summary>
    /// <remarks>
    /// Dispatched on the same MTA worker as <see cref="GetElementBoundsAt"/>
    /// (same COM apartment + thread-affinity rules). The walk:
    /// <list type="number">
    ///   <item>Find the window root containing the region's center via
    ///   <c>ElementFromPoint(center)</c> + ancestor walk to a top-level
    ///   window (one whose parent is the desktop root).</item>
    ///   <item>BFS the subtree under that window using the control-view
    ///   TreeWalker; prune branches whose bounding rect doesn't intersect the
    ///   region. This bounds the walk to on-screen, in-region elements only.</item>
    ///   <item>For each visited element, read Name (slot 23), then TextPattern
    ///   DocumentRange, then ValuePattern Value (whichever is non-empty);
    ///   collect into a list tagged with the element's center for sorting.</item>
    ///   <item>Sort by (top, left) and join with newlines.</item>
    /// </list>
    /// Capped at <see cref="MaxRegionTextElements"/> elements to bound latency.
    /// </remarks>
    public IReadOnlyList<string> GetTextsInRegion(Rect region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureBoundsThread();

        IReadOnlyList<string>? result = null;
        Exception? caught = null;
        var done = new ManualResetEventSlim(false);

        _boundsQueue.Add(() =>
        {
            try
            {
                result = GetTextsInRegionCore(region);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                done.Set();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(3)))
        {
            return Array.Empty<string>();
        }

        return caught is null ? (result ?? Array.Empty<string>()) : Array.Empty<string>();
    }

    /// <summary>Cap on elements visited during a region walk. Bounds the
    /// worst-case latency (each element ~0.1ms for Name read, more for
    /// pattern reads). 512 elements is enough for any realistic UI panel and
    /// keeps the walk under ~500ms even on hostile providers.</summary>
    private const int MaxRegionTextElements = 512;

    private IReadOnlyList<string> GetTextsInRegionCore(Rect region)
    {
        if (!EnsureInitialized() || region.Width <= 0 || region.Height <= 0)
        {
            return Array.Empty<string>();
        }

        // Seed: the element at the region's center. ElementFromPoint returns
        // the deepest element (e.g. a text span or icon), whose subtree is
        // usually empty. To collect text from the user's whole boxed region,
        // we walk UP the ancestor chain to find the smallest container whose
        // bounding rect fully contains the region — that's the semantic
        // container of what the user boxed. Then BFS that container's subtree,
        // pruned to elements intersecting the region.
        int centerX = region.X + region.Width / 2;
        int centerY = region.Y + region.Height / 2;

        nint seed = ElementFromPoint(centerX, centerY);
        if (seed == 0)
        {
            // No element at center (canvas / game / off-screen) → UIA has
            // nothing to offer in this region. Caller falls back to OCR.
            return Array.Empty<string>();
        }

        // Walk up to find the smallest ancestor that contains the region.
        // Cap the walk to avoid going all the way to the desktop root on
        // regions that happen to match a large container's bounds.
        nint bfsRoot = FindSmallestContainingAncestor(seed, region);

        var collected = new List<(int top, int left, string text)>();
        var visited = new HashSet<nint>();
        var queue = new Queue<nint>();
        queue.Enqueue(bfsRoot);
        visited.Add(bfsRoot);
        int visitedCount = 0;

        while (queue.Count > 0 && visitedCount < MaxRegionTextElements)
        {
            nint current = queue.Dequeue();
            visitedCount++;

            if (current == 0)
            {
                continue;
            }

            // Read the element's bounding rect. If it doesn't intersect the
            // region AND we're past the bfsRoot (bfsRoot always visited even
            // if its rect is huge), prune — don't descend. This bounds the
            // walk to on-screen, in-region elements only.
            Rect? bounds = ReadBoundingRectangle(current);
            if (bounds is { } b && !Intersects(b, region) && current != bfsRoot)
            {
                Release(current);
                continue;
            }

            // Read text from this element. Name is cheapest and covers most
            // visible labels; TextPattern/ValuePattern only needed for
            // documents/edit boxes (tried only when Name is empty).
            string? text = ReadElementName(current);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = TryReadElementText(current);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                text = text.Trim();
                if (text.Length > 0)
                {
                    int top = bounds?.Y ?? 0;
                    int left = bounds?.X ?? 0;
                    collected.Add((top, left, text));
                }
            }

            // Enqueue children for BFS. Children take ownership of their
            // COM refs via EnqueueChildren's transfer.
            EnqueueChildren(current, queue, visited);

            // Done with this element — release its COM ref.
            Release(current);
        }

        if (collected.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Reading order: top-to-bottom, ties broken left-to-right. Elements at
        // roughly the same vertical line (within a few px) are sorted left-to-
        // right to handle multi-column layouts; otherwise ordered by row.
        return collected
            .OrderBy(t => t.top / 8)               // group rows ~8px tolerance
            .ThenBy(t => t.left)
            .Select(t => t.text)
            .Distinct(StringComparer.Ordinal)       // dedupe identical adjacent strings
            .ToList();
    }

    /// <summary>
    /// Walks up the ancestor chain from <paramref name="element"/> and returns
    /// the <b>smallest</b> ancestor whose bounding rect fully contains the
    /// given region. This is the semantic container of what the user boxed.
    /// </summary>
    /// <remarks>
    /// ElementFromPoint returns the deepest element (a text span, an icon),
    /// whose subtree is usually empty. The user's box covers a region that
    /// typically corresponds to a larger container — a panel, a list, a
    /// paragraph. Walking up to the smallest containing ancestor finds that
    /// container without going all the way to the desktop root.
    /// <para>
    /// Stops as soon as an ancestor's bounds contain the region (the smallest
    /// such ancestor), or after <see cref="MaxAncestorDepthForRegionRoot"/>
    /// steps (whichever comes first). If no ancestor contains the region,
    /// returns the original element.
    /// </para>
    /// <b>Ownership</b>: returns a NEW COM reference that the caller must
    /// Release. The input <paramref name="element"/> ref is consumed (caller
    /// should not Release it after this call).
    /// </remarks>
    private nint FindSmallestContainingAncestor(nint element, Rect region)
    {
        if (element == 0 || _controlViewWalker == 0)
        {
            return element;
        }

        delegate* unmanaged[Stdcall]<nint, nint, nint*, int> getParent =
            (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVtableSlot(_controlViewWalker, 3);

        // Check the seed itself first — if its bounds already contain the
        // region (e.g. user boxed a single button precisely), no walk needed.
        nint current = element;
        Rect? currentBounds = ReadBoundingRectangle(current);
        if (currentBounds is { } cb && Contains(cb, region))
        {
            return current;
        }

        try
        {
            for (int depth = 0; depth < MaxAncestorDepthForRegionRoot; depth++)
            {
                nint parent = 0;
                int result = getParent(_controlViewWalker, current, &parent);
                if (result < 0 || parent == 0)
                {
                    break;
                }

                // Move up one level: release the previous element (seed on the
                // first iteration, intermediate parent on later ones) and
                // adopt the parent as our new current. If the parent contains
                // the region, transfer ownership to caller and return.
                Release(current);
                current = parent;

                Rect? parentBounds = ReadBoundingRectangle(current);
                if (parentBounds is { } pb && Contains(pb, region))
                {
                    // Smallest ancestor containing the region. Transfer
                    // ownership of `current` to caller (don't Release).
                    return current;
                }
            }

            // No ancestor fully contained the region within the depth limit.
            // Return the deepest ancestor we reached; caller takes ownership.
            return current;
        }
        catch
        {
            // On any error, hand back whatever we currently hold. Caller owns
            // `current` (we've released everything below it).
            return current;
        }
    }

    /// <summary>Cap on the ancestor walk when finding the BFS root for a
    /// region query. Smaller than <see cref="DefaultMaxAncestorDepth"/> (which
    /// is for selection reading) because we don't want to climb all the way to
    /// the desktop root — that would re-introduce the "scan the whole window"
    /// latency problem. 4 levels covers panel → group → list → list-item and
    /// similar UI nesting.</summary>
    private const int MaxAncestorDepthForRegionRoot = 4;

    /// <summary>AABB containment: does <paramref name="outer"/> fully contain
    /// <paramref name="inner"/>? Used to find the smallest ancestor whose
    /// bounds cover the user's region.</summary>
    private static bool Contains(Rect outer, Rect inner)
    {
        return outer.X <= inner.X
            && outer.Y <= inner.Y
            && outer.X + outer.Width >= inner.X + inner.Width
            && outer.Y + outer.Height >= inner.Y + inner.Height;
    }

    private void EnqueueChildren(nint element, Queue<nint> queue, HashSet<nint> visited)
    {
        if (element == 0 || _controlViewWalker == 0)
        {
            return;
        }

        // GetFirstChildElement (slot 4) + GetNextSiblingElement (slot 6).
        delegate* unmanaged[Stdcall]<nint, nint, nint*, int> getFirstChild =
            (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVtableSlot(_controlViewWalker, 4);
        delegate* unmanaged[Stdcall]<nint, nint, nint*, int> getNextSibling =
            (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVtableSlot(_controlViewWalker, 6);

        nint child = 0;
        int result = getFirstChild(_controlViewWalker, element, &child);
        if (result < 0 || child == 0)
        {
            return;
        }

        nint ownedChild = child;
        try
        {
            while (child != 0)
            {
                if (visited.Add(child))
                {
                    queue.Enqueue(child);
                    ownedChild = 0; // ownership transferred to queue
                }

                nint next = 0;
                result = getNextSibling(_controlViewWalker, child, &next);
                Release(ownedChild);
                ownedChild = next;
                child = next;
                if (result < 0)
                {
                    break;
                }
            }
        }
        finally
        {
            Release(ownedChild);
        }
    }

    /// <summary>Reads IUIAutomationElement::get_CurrentName (vtable slot 23).
    /// The Name property is the visible label on most controls (button text,
    /// label content, link text, list item text). Cheaper than TextPattern.</summary>
    private static string? ReadElementName(nint element)
    {
        if (element == 0)
        {
            return null;
        }

        nint nameBstr = 0;
        delegate* unmanaged[Stdcall]<nint, nint*, int> getCurrentName =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(element, 23);

        int result = getCurrentName(element, &nameBstr);
        if (result < 0 || nameBstr == 0)
        {
            return null;
        }

        try
        {
            uint length = SysStringLen(nameBstr);
            if (length == 0)
            {
                return string.Empty;
            }

            int charCount = Math.Min(checked((int)length), MaxElementTextChars);
            return new string((char*)nameBstr, 0, charCount);
        }
        finally
        {
            SysFreeString(nameBstr);
        }
    }

    private static bool Intersects(Rect a, Rect b)
    {
        // Standard AABB intersection test. Inclusive on edges so a zero-size
        // touch still counts if it lands exactly on the boundary.
        int aRight = a.X + a.Width;
        int aBottom = a.Y + a.Height;
        int bRight = b.X + b.Width;
        int bBottom = b.Y + b.Height;
        return a.X < bRight && b.X < aRight && a.Y < bBottom && b.Y < aBottom;
    }

    private Rect? GetElementBoundsAtCore(int x, int y)
    {
        if (!EnsureInitialized())
        {
            return null;
        }

        nint element = ElementFromPoint(x, y);
        if (element == 0)
        {
            return null;
        }

        try
        {
            return ReadBoundingRectangle(element);
        }
        finally
        {
            Release(element);
        }
    }

    /// <summary>Lazy-starts the dedicated MTA worker that owns the COM apartment.</summary>
    private void EnsureBoundsThread()
    {
        if (_boundsThread is not null)
        {
            return;
        }

        lock (_boundsQueue)
        {
            if (_boundsThread is not null)
            {
                return;
            }

            var thread = new Thread(RunBoundsThread)
            {
                IsBackground = true,
                Name = "BYH.RegionOcr.UIAutomation",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            _boundsThread = thread;
        }
    }

    private void RunBoundsThread()
    {
        foreach (Action work in _boundsQueue.GetConsumingEnumerable())
        {
            work();
        }
    }

    private static Rect? ReadBoundingRectangle(nint element)
    {
        var rect = default(NativeRect);
        // IUIAutomationElement::get_CurrentBoundingRectangle — vtable slot 43.
        // (Slot 89 was past the interface vtable end → garbage. Slot 42 was
        // off-by-one from a miscounted method list → also garbage. Slot 43
        // verified empirically: returns plausible screen-rect coordinates at
        // every tested point, matching IUIAutomationElement's 3 IUnknown + 40
        // methods where get_CurrentBoundingRectangle is the 41st method.)
        delegate* unmanaged[Stdcall]<nint, NativeRect*, int> getBoundingRectangle =
            (delegate* unmanaged[Stdcall]<nint, NativeRect*, int>)GetVtableSlot(element, 43);

        int result = getBoundingRectangle(element, &rect);
        if (result < 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return null;
        }

        return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static string? TryReadSelectedText(nint element, uint expectedProcessId)
    {
        if (element == 0 || !MatchesProcess(element, expectedProcessId))
        {
            return null;
        }

        nint pattern = GetPattern(
            element,
            UiaTextPattern2Id,
            IidIUiAutomationTextPattern2);

        if (pattern == 0)
        {
            pattern = GetPattern(
                element,
                UiaTextPatternId,
                IidIUiAutomationTextPattern);
        }

        if (pattern == 0)
        {
            return null;
        }

        try
        {
            return ReadSelectionFromTextPattern(pattern);
        }
        finally
        {
            Release(pattern);
        }
    }

    /// <summary>
    /// R24 track A: reads an element's *visible text* when selection is empty.
    /// Tries TextPattern's DocumentRange (the whole document body the element
    /// represents) then ValuePattern's Value (single-value controls: edit boxes,
    /// labels, combos). Capped to <see cref="MaxElementTextChars"/> characters.
    /// Unlike selection, this is an approximation and only used as a last resort.
    /// </summary>
    private static string? TryReadElementText(nint element)
    {
        if (element == 0)
        {
            return null;
        }

        // TextPattern DocumentRange: get_DocumentRange (slot 7) returns a
        // IUIAutomationTextRange; GetText (slot 12, already used by selection
        // range reading) with a char cap pulls the document body.
        nint textPattern = GetPattern(
            element,
            UiaTextPatternId,
            IidIUiAutomationTextPattern);

        if (textPattern != 0)
        {
            try
            {
                string? documentText = ReadDocumentRangeText(textPattern);
                if (!string.IsNullOrWhiteSpace(documentText))
                {
                    return documentText;
                }
            }
            finally
            {
                Release(textPattern);
            }
        }

        // ValuePattern: get_CurrentValue (slot 4) returns a BSTR.
        nint valuePattern = GetPattern(
            element,
            UiaValuePatternId,
            IidIUiAutomationValuePattern);

        if (valuePattern == 0)
        {
            return null;
        }

        try
        {
            return ReadValuePatternValue(valuePattern);
        }
        finally
        {
            Release(valuePattern);
        }
    }

    private static string? ReadDocumentRangeText(nint textPattern)
    {
        nint documentRange = 0;
        delegate* unmanaged[Stdcall]<nint, nint*, int> getDocumentRange =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(textPattern, 7);

        int rangeResult = getDocumentRange(textPattern, &documentRange);
        if (rangeResult < 0 || documentRange == 0)
        {
            return null;
        }

        try
        {
            return GetRangeText(documentRange, MaxElementTextChars);
        }
        finally
        {
            Release(documentRange);
        }
    }

    private static string? ReadValuePatternValue(nint valuePattern)
    {
        nint value = 0;
        delegate* unmanaged[Stdcall]<nint, nint*, int> getCurrentValue =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(valuePattern, 4);

        int result = getCurrentValue(valuePattern, &value);
        if (result < 0 || value == 0)
        {
            return null;
        }

        try
        {
            uint length = SysStringLen(value);
            if (length == 0)
            {
                return string.Empty;
            }

            int charCount = Math.Min(checked((int)length), MaxElementTextChars);
            return new string((char*)value, 0, charCount);
        }
        finally
        {
            SysFreeString(value);
        }
    }

    private static bool MatchesProcess(nint element, uint expectedProcessId)
    {
        int processId = 0;
        delegate* unmanaged[Stdcall]<nint, int*, int> getCurrentProcessId =
            (delegate* unmanaged[Stdcall]<nint, int*, int>)GetVtableSlot(element, 20);

        int result = getCurrentProcessId(element, &processId);
        return result >= 0 && unchecked((uint)processId) == expectedProcessId;
    }

    private static nint GetPattern(nint element, int patternId, Guid interfaceId)
    {
        nint pattern = 0;
        delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int> getCurrentPatternAs =
            (delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)GetVtableSlot(element, 14);

        int result = getCurrentPatternAs(element, patternId, &interfaceId, &pattern);
        return result >= 0 ? pattern : 0;
    }

    private static string? ReadSelectionFromTextPattern(nint pattern)
    {
        nint ranges = 0;
        delegate* unmanaged[Stdcall]<nint, nint*, int> getSelection =
            (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableSlot(pattern, 5);

        int selectionResult = getSelection(pattern, &ranges);
        if (selectionResult < 0 || ranges == 0)
        {
            return null;
        }

        try
        {
            int length = 0;
            delegate* unmanaged[Stdcall]<nint, int*, int> getLength =
                (delegate* unmanaged[Stdcall]<nint, int*, int>)GetVtableSlot(ranges, 3);

            if (getLength(ranges, &length) < 0 || length <= 0)
            {
                return null;
            }

            var selectedRanges = new List<string>(length);
            delegate* unmanaged[Stdcall]<nint, int, nint*, int> getElement =
                (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)GetVtableSlot(ranges, 4);

            for (int index = 0; index < length; index++)
            {
                nint range = 0;
                if (getElement(ranges, index, &range) < 0 || range == 0)
                {
                    continue;
                }

                try
                {
                    string? text = GetRangeText(range);
                    if (!string.IsNullOrEmpty(text))
                    {
                        selectedRanges.Add(text);
                    }
                }
                finally
                {
                    Release(range);
                }
            }

            return selectedRanges.Count switch
            {
                0 => null,
                1 => selectedRanges[0],
                _ => string.Join(Environment.NewLine, selectedRanges),
            };
        }
        finally
        {
            Release(ranges);
        }
    }

    private static string? GetRangeText(nint range, int maxLength = -1)
    {
        nint text = 0;
        delegate* unmanaged[Stdcall]<nint, int, nint*, int> getText =
            (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)GetVtableSlot(range, 12);

        int result = getText(range, maxLength, &text);
        if (result < 0 || text == 0)
        {
            return null;
        }

        try
        {
            uint length = SysStringLen(text);
            return length == 0
                ? string.Empty
                : new string((char*)text, 0, checked((int)length));
        }
        finally
        {
            SysFreeString(text);
        }
    }

    private static bool SourceContextStillMatches(SelectionGesture gesture)
    {
        nint foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        nint foregroundRoot = GetAncestor(foregroundWindow, GaRoot);
        if (foregroundRoot == 0)
        {
            foregroundRoot = foregroundWindow;
        }

        GetWindowThreadProcessId(foregroundRoot, out uint foregroundProcessId);
        return foregroundProcessId == gesture.SourceProcessId &&
               foregroundRoot == gesture.SourceRootHwnd;
    }

    private static nint GetVtableSlot(nint instance, int slot)
    {
        nint vtable = *(nint*)instance;
        return ((nint*)vtable)[slot];
    }

    private static void Release(nint instance)
    {
        if (instance == 0)
        {
            return;
        }

        delegate* unmanaged[Stdcall]<nint, uint> release =
            (delegate* unmanaged[Stdcall]<nint, uint>)GetVtableSlot(instance, 2);
        release(instance);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Signal the MTA bounds worker to drain and exit. The worker holds no
        // COM pointers after each callback returns (Release is in finally), so
        // it's safe to stop without CoUninitialize on that thread.
        _boundsQueue.CompleteAdding();

        DisposeComState();
        GC.SuppressFinalize(this);
    }

    private void DisposeComState()
    {
        Release(_controlViewWalker);
        _controlViewWalker = 0;
        Release(_automation);
        _automation = 0;

        if (_comInitialized)
        {
            CoUninitialize();
            _comInitialized = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    // Win32 RECT { LONG left, top, right, bottom } — matches the layout returned
    // by IUIAutomationElement::get_CurrentBoundingRectangle (vtable slot 43).
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        Guid* classId,
        nint outer,
        uint context,
        Guid* interfaceId,
        nint* instance);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("oleaut32.dll")]
    private static extern uint SysStringLen(nint bstr);

    [DllImport("oleaut32.dll")]
    private static extern void SysFreeString(nint bstr);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

/// <summary>
/// Screen-coordinate rectangle (x/y origin, width/height) returned by
/// <see cref="WindowsUiAutomationBackend.GetElementBoundsAt"/>. Independent of
/// any UI framework's Rect type so the capture layer stays dependency-free.
/// </summary>
public sealed record Rect(int X, int Y, int Width, int Height);
