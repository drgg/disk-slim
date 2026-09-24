using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using DiskSlim.Core.Rules;

namespace DiskSlim.Core.Scanning;

/// <summary>
/// 并行遍历目录树，统计每个目录的大小。只读取文件系统的目录信息，不打开、不修改任何文件。
/// </summary>
/// <remarks>
/// 安全约定：
/// <list type="bullet">
/// <item>不进入目录联接、符号链接和挂载点，避免重复计算和死循环；</item>
/// <item>不进入“仅在线”的云盘目录，也不计入“仅在线”的文件，避免触发下载；</item>
/// <item>无权限的目录记录下来后跳过，不会中断扫描。</item>
/// </list>
/// 同一个实例不要同时执行多次 <see cref="Scan"/>。
/// </remarks>
public sealed class TreeScanner
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;

    /// <summary>带有这些属性的文件或目录内容不在本地。</summary>
    internal const FileAttributes CloudOnlyMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>清理时会保留的文件属性。</summary>
    internal const FileAttributes ProtectedMask = FileAttributes.ReadOnly | FileAttributes.System;

    private readonly ScanOptions _options;
    private long _liveDirectories;
    private long _liveFiles;
    private long _liveBytes;

    public TreeScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    /// <summary>已扫描的目录数，扫描过程中可从其他线程读取。</summary>
    public long DirectoriesScanned => Interlocked.Read(ref _liveDirectories);

    public long FilesScanned => Interlocked.Read(ref _liveFiles);

    public long BytesScanned => Interlocked.Read(ref _liveBytes);

    public ScanResult Scan(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"目录不存在：{fullRoot}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _liveDirectories, 0);
        Interlocked.Exchange(ref _liveFiles, 0);
        Interlocked.Exchange(ref _liveBytes, 0);

        using var run = new ScanRun(this, _options, cancellationToken);
        return run.Execute(fullRoot);
    }

    internal void ReportDirectoryDone(int files, long bytes)
    {
        Interlocked.Increment(ref _liveDirectories);
        Interlocked.Add(ref _liveFiles, files);
        Interlocked.Add(ref _liveBytes, bytes);
    }
}

/// <summary>一次扫描的全部运行状态。</summary>
internal sealed class ScanRun : IDisposable
{
    private readonly TreeScanner _owner;
    private readonly ScanOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentStack<WorkItem>());
    private readonly ConcurrentQueue<ScanError> _errors = new();
    private readonly LargestFilesCollector? _largest;
    private readonly DateTimeOffset? _reclaimableBefore;
    private long _pending;
    private long _recordedErrors;
    private long _inaccessible;
    private long _skippedLinks;
    private long _cloudOnlyFiles;
    private long _cloudOnlyDirectories;

    public ScanRun(TreeScanner owner, ScanOptions options, CancellationToken cancellationToken)
    {
        _owner = owner;
        _options = options;
        _cancellationToken = cancellationToken;
        _reclaimableBefore = options.ReclaimableBefore;
        _largest = options.LargestFileCount > 0 ? new LargestFilesCollector(options.LargestFileCount) : null;
    }

    public ScanResult Execute(string rootPath)
    {
        var stopwatch = Stopwatch.StartNew();
        var root = new DirNode(rootPath, parent: null);

        _pending = 1;
        _queue.Add(new WorkItem(root, rootPath));

        var workers = new Task[Math.Max(1, _options.MaxDegreeOfParallelism)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException) when (_cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(_cancellationToken);
        }

        var directories = Aggregate(root);
        stopwatch.Stop();

        var stats = new ScanStats
        {
            Files = root.TotalFileCount,
            Directories = directories,
            Bytes = root.TotalBytes,
            InaccessibleDirectories = Interlocked.Read(ref _inaccessible),
            SkippedLinks = Interlocked.Read(ref _skippedLinks),
            CloudOnlyFiles = Interlocked.Read(ref _cloudOnlyFiles),
            CloudOnlyDirectories = Interlocked.Read(ref _cloudOnlyDirectories),
        };

        var errors = _errors.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new ScanResult(root, _largest?.ToSortedList() ?? [], stats, errors, stopwatch.Elapsed);
    }

    public void Dispose() => _queue.Dispose();

    /// <summary>记录一个文件。由目录枚举器在读取到文件条目时调用。</summary>
    internal void AccountFile(ref FileSystemEntry entry, DirNode node, string directory)
    {
        if ((entry.Attributes & TreeScanner.CloudOnlyMask) != 0)
        {
            Interlocked.Increment(ref _cloudOnlyFiles);
            return;
        }

        var length = entry.Length;
        node.OwnBytes += length;
        node.OwnFileCount++;
        if (FileAge.IsOlderThan(entry.LastWriteTimeUtc, entry.CreationTimeUtc, _reclaimableBefore)
            && !(_options.ProtectedFilesAreNotReclaimable && (entry.Attributes & TreeScanner.ProtectedMask) != 0))
        {
            node.OwnReclaimableBytes += length;
        }

        _largest?.Offer(ref entry, directory, length);
    }

    private void Work()
    {
        foreach (var item in _queue.GetConsumingEnumerable(_cancellationToken))
        {
            try
            {
                ProcessDirectory(item);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _queue.CompleteAdding();
                }
            }
        }
    }

    private void ProcessDirectory(WorkItem item)
    {
        var node = item.Node;
        List<DirNode>? children = null;
        try
        {
            using var enumerator = new DirectoryEntryEnumerator(item.Path, node, this);
            while (enumerator.MoveNext())
            {
                var entry = enumerator.Current;
                var childPath = Path.Join(item.Path, entry.Name);
                if (!ShouldDescend(entry.Attributes, childPath))
                {
                    continue;
                }

                var child = new DirNode(entry.Name, node);
                (children ??= []).Add(child);
                Interlocked.Increment(ref _pending);
                _queue.Add(new WorkItem(child, childPath));
            }

            if (enumerator.ErrorCode != 0)
            {
                RecordError(node, item.Path, enumerator.ErrorCode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            RecordError(node, item.Path, ex.HResult & 0xFFFF);
        }

        node.SetChildren(children);
        _owner.ReportDirectoryDone(node.OwnFileCount, node.OwnBytes);
    }

    private bool ShouldDescend(FileAttributes attributes, string path)
    {
        if ((attributes & TreeScanner.CloudOnlyMask) != 0)
        {
            Interlocked.Increment(ref _cloudOnlyDirectories);
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return true;
        }

        // 目录联接、符号链接、挂载点有链接目标，跳过；OneDrive 等云盘目录也是重解析点，但没有链接目标，需要正常统计。
        string? target;
        try
        {
            target = new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 判断不了时按链接处理：宁可少算，也不重复计算。
            target = "?";
        }

        if (target is null)
        {
            return true;
        }

        Interlocked.Increment(ref _skippedLinks);
        return false;
    }

    private void RecordError(DirNode node, string path, int errorCode)
    {
        node.ErrorCode = errorCode == 0 ? -1 : errorCode;
        Interlocked.Increment(ref _inaccessible);
        if (Interlocked.Increment(ref _recordedErrors) <= _options.MaxRecordedErrors)
        {
            _errors.Enqueue(new ScanError(path, errorCode));
        }
    }

    /// <summary>自底向上汇总大小，返回目录总数。</summary>
    private static long Aggregate(DirNode root)
    {
        // 广度优先排列后倒序处理，保证子目录总是先于父目录汇总。
        var order = new List<DirNode> { root };
        for (var i = 0; i < order.Count; i++)
        {
            order.AddRange(order[i].Children);
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            node.TotalBytes += node.OwnBytes;
            node.TotalReclaimableBytes += node.OwnReclaimableBytes;
            node.TotalFileCount += node.OwnFileCount;
            if (node.ErrorCode != 0)
            {
                node.TotalInaccessibleCount++;
            }

            if (node.Parent is { } parent)
            {
                parent.TotalBytes += node.TotalBytes;
                parent.TotalReclaimableBytes += node.TotalReclaimableBytes;
                parent.TotalFileCount += node.TotalFileCount;
                parent.TotalInaccessibleCount += node.TotalInaccessibleCount;
            }
        }

        return order.Count;
    }

    private readonly record struct WorkItem(DirNode Node, string Path);
}

internal readonly record struct DirEntry(string Name, FileAttributes Attributes);

/// <summary>
/// 枚举单个目录（不递归）。文件在枚举时直接累加到节点上，不产生额外对象；只把子目录返回给调用方。
/// </summary>
internal sealed class DirectoryEntryEnumerator : FileSystemEnumerator<DirEntry>
{
    private static readonly EnumerationOptions Options = new()
    {
        // 默认会跳过隐藏和系统文件，这里要全部统计（比如 pagefile.sys）。
        AttributesToSkip = 0,
        // 设为 false 才能在 ContinueOnError 里拿到“拒绝访问”的错误码。
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 32 * 1024,
    };

    private readonly DirNode _node;
    private readonly ScanRun _run;
    private readonly string _directory;

    public DirectoryEntryEnumerator(string directory, DirNode node, ScanRun run)
        : base(directory, Options)
    {
        _directory = directory;
        _node = node;
        _run = run;
    }

    /// <summary>读取过程中遇到的第一个 Win32 错误码；0 表示没有错误。</summary>
    /// <remarks>基类构造函数打开目录失败时也会回调 ContinueOnError，所以这里只能用字段，不能依赖构造函数中赋值的成员。</remarks>
    public int ErrorCode { get; private set; }

    protected override bool ContinueOnError(int error)
    {
        if (ErrorCode == 0)
        {
            ErrorCode = error;
        }

        return true;
    }

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            return true;
        }

        _run.AccountFile(ref entry, _node, _directory);
        return false;
    }

    protected override DirEntry TransformEntry(ref FileSystemEntry entry) =>
        new(entry.FileName.ToString(), entry.Attributes);
}

/// <summary>线程安全地保留最大的 N 个文件。</summary>
internal sealed class LargestFilesCollector(int capacity)
{
    private readonly PriorityQueue<FileEntry, long> _heap = new();
    private readonly Lock _lock = new();
    private long _threshold;

    public void Offer(ref FileSystemEntry entry, string directory, long length)
    {
        // 绝大多数文件在这里就被过滤掉，不需要加锁，也不需要拼接路径。
        if (length <= Volatile.Read(ref _threshold))
        {
            return;
        }

        lock (_lock)
        {
            if (_heap.Count >= capacity)
            {
                if (!_heap.TryPeek(out _, out var smallest) || length <= smallest)
                {
                    return;
                }

                _heap.Dequeue();
            }

            _heap.Enqueue(new FileEntry(Path.Join(directory.AsSpan(), entry.FileName), length, entry.LastWriteTimeUtc), length);
            if (_heap.Count >= capacity && _heap.TryPeek(out _, out var newSmallest))
            {
                Volatile.Write(ref _threshold, newSmallest);
            }
        }
    }

    public IReadOnlyList<FileEntry> ToSortedList()
    {
        lock (_lock)
        {
            return _heap.UnorderedItems
                .Select(item => item.Element)
                .OrderByDescending(file => file.Bytes)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
