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
public sealed record ClipboardSnapshot(
    uint SequenceNumber,
    string? Text,
    byte[]? ImageDib,
    string[]? Files,
    bool BackupSucceeded = true,
    bool WasEmpty = false)
{
    public bool HasRestorableData =>
        Text is not null || ImageDib is not null || Files is not null;

    public static ClipboardSnapshot Unavailable(uint sequenceNumber) =>
        new(sequenceNumber, null, null, null, BackupSucceeded: false, WasEmpty: false);
}
