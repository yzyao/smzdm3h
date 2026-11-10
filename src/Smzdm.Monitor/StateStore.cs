using System.Text.Json;

namespace Smzdm.Monitor;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _directory;

    public StateStore(MonitorOptions options)
    {
        _directory = Path.GetFullPath(options.DataDirectory);
        _path = Path.Combine(_directory, "state.json");
    }

    public FileStream AcquireLock()
    {
        Directory.CreateDirectory(_directory);
        try
        {
            return new FileStream(Path.Combine(_directory, "monitor.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("无法锁定数据目录：请确认仅一个监控实例使用此目录，并检查目录权限。");
        }
    }

    public async Task<MonitorState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new MonitorState();
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var state = await JsonSerializer.DeserializeAsync<MonitorState>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("state.json 为空；请恢复备份，或移走文件后重新建立基线。");
        if (state.Version is not (1 or 2) || state.SourceKey is null || state.DestinationKey is null ||
            (state.Pending is not null && (!ValidText(state.Pending.Text, state.Pending.IsRich) ||
                (state.Pending.IsRich && state.Pending.Items is null) ||
                (state.Pending.Items is not null && (state.Pending.Items.Count is < 1 or > 20 ||
                    (!state.Pending.IsRich && MessageFormatter.PlainText(state.Pending.Text).Length > PhotoMessageFormatter.MaxLength) ||
                    state.Pending.Items.Any(x => x is null || string.IsNullOrEmpty(x.Id) || string.IsNullOrEmpty(x.Url)) ||
                    state.Pending.Items.Select(x => x.Id).Distinct().Count() != state.Pending.Items.Count)))) ||
            (state.Published is not null && (state.Published.MessageId <= 0 || !ValidText(state.Published.Text, state.Published.IsRich))) ||
            (state.Report is not null && (state.Report.Events is null ||
                state.Report.Events.Any(x => x is null || x.Deal is null || string.IsNullOrEmpty(x.Deal.Id)) ||
                state.Report.Events.Select(x => x.Deal.Id).Distinct().Count() != state.Report.Events.Count)) ||
            (state.Snapshot is not null && (state.Snapshot.Items is null || state.Snapshot.Items.Count == 0 ||
                state.Snapshot.Items.Any(x => x is null || x.Deal is null || string.IsNullOrEmpty(x.Deal.Id) || x.ConsecutivePolls < 1) ||
                state.Snapshot.Items.Select(x => x.Deal.Id).Distinct().Count() != state.Snapshot.Items.Count)))
            throw new InvalidDataException("state.json 格式或版本不正确；为避免误报，拒绝自动重置。");
        return state;
    }

    private static bool ValidText(string? text, bool isRich) => !string.IsNullOrWhiteSpace(text) &&
        (isRich ? RichMessageFormatter.ValidLength(text) : MessageFormatter.PlainText(text).Length <= MessageFormatter.MaxLength);

    public async Task SaveAsync(MonitorState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }
}
