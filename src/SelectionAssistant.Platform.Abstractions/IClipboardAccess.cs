namespace SelectionAssistant.Platform.Abstractions;

/// <summary>
/// 剪贴板访问抽象(v4 §6.4 最佳努力剪贴板)。
/// 实现必须: 有界重试 OpenClipboard, 序列号竞争检测, 监听变化通知(非轮询)。
/// </summary>
public interface IClipboardAccess
{
    /// <summary>当前剪贴板序列号(每次内容变化或清空时递增)。</summary>
    uint GetSequenceNumber();

    /// <summary>当前剪贴板属主进程；无法可靠判定时返回 null。</summary>
    uint? GetOwnerProcessId();

    /// <summary>最佳努力备份当前剪贴板(支持的格式,有大小上限)。</summary>
    ClipboardSnapshot Backup();

    /// <summary>最佳努力恢复(仅当序列号未变时)。返回是否实际恢复。</summary>
    bool Restore(ClipboardSnapshot snapshot, uint expectedSequence);

    /// <summary>仅当序列号未变时清空剪贴板，用于恢复最初的空剪贴板。</summary>
    bool Clear(uint expectedSequence);

    /// <summary>读取当前文本(有界重试)。</summary>
    string? GetText();

    /// <summary>注册变化通知回调。Windows: AddClipboardFormatListener → WM_CLIPBOARDUPDATE。</summary>
    void SubscribeChanges(Action onChanged);

    void UnsubscribeChanges();
}

/// <summary>剪贴板快照(最佳努力,非全格式)。</summary>
/// <remarks>
/// <b>内存(P2):</b>当 <see cref="ImageDib"/> 是从 <c>ArrayPool&lt;byte&gt;</c> 租来的
/// 缓冲区时(见 <c>Win32Clipboard.Backup</c>),<see cref="Dispose"/> 会把它归还给池;
/// 否则 Dispose 是空操作。调用方(<c>Win32ClipboardCapture</c>)在 capture 结束后
/// (无论是否恢复)必须 Dispose 本快照,否则租来的大缓冲区(最大 32 MB 的 CF_DIB)要等
/// GC 才回收,而 NativeAOT 的 LOH 不压缩也不归还内存给 OS —— 这正是空闲态 private
/// bytes 持续走高的根因(每次选区探测都读一次剪贴板里的图)。直接 new 出来的快照
/// (测试替身、Unavailable)没有租用缓冲区,Dispose 安全无副作用。
/// <para>
/// <b>DIB 长度:</b><see cref="ImageDib"/> 可能是租来的超分配缓冲区
/// (ArrayPool 返回 ≥ 请求长度的桶),<see cref="ImageDibLength"/> 是实际有效字节数;
/// 消费者(Restore/CreateSnapshotMemory)必须只读前 <see cref="ImageDibLength"/> 字节。
/// 非租用构造路径默认 ImageDibLength = ImageDib?.Length ?? 0。
/// </para>
/// </remarks>
public sealed record ClipboardSnapshot(
    uint SequenceNumber,
    string? Text,
    byte[]? ImageDib,
    string[]? Files,
    bool BackupSucceeded = true,
    bool WasEmpty = false) : IDisposable
{
    /// <summary>
    /// 由 <see cref="Backup"/> 注册的释放回调;null 表示 ImageDib 不是租来的
    /// (普通 new 构造或 Unavailable),Dispose 无副作用。回调幂等由调用方保证
    /// (ArrayPool.Return 本身可重复调用)。
    /// </summary>
    private Action? _disposeHook;

    /// <summary>ImageDib 缓冲区的有效字节数。租用缓冲区可能比实际 DIB 大
    /// (ArrayPool 桶对齐),Restore 只复制这么多字节。默认 = ImageDib?.Length。</summary>
    public int ImageDibLength { get; init; } = ImageDib?.Length ?? 0;

    /// <summary>带释放钩子 + 显式 DIB 长度的工厂,仅供把 ImageDib 绑定到租来
    /// 缓冲区的实现使用。调用方负责保证 <paramref name="disposeHook"/> 幂等且
    /// 线程安全,<paramref name="imageDibLength"/> ≤ ImageDib?.Length。</summary>
    public ClipboardSnapshot(
        uint sequenceNumber,
        string? text,
        byte[]? imageDib,
        int imageDibLength,
        string[]? files,
        bool backupSucceeded,
        bool wasEmpty,
        Action? disposeHook) : this(sequenceNumber, text, imageDib, files, backupSucceeded, wasEmpty)
    {
        ImageDibLength = imageDib == null ? 0 : imageDibLength;
        _disposeHook = disposeHook;
    }

    public bool HasRestorableData =>
        Text is not null || (ImageDib is not null && ImageDibLength > 0) || Files is not null;

    public static ClipboardSnapshot Unavailable(uint sequenceNumber) =>
        new(sequenceNumber, null, null, null, BackupSucceeded: false, WasEmpty: false);

    /// <summary>归还租来的缓冲区(若有)。幂等;重复调用安全。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeHook, null)?.Invoke();
    }
}
