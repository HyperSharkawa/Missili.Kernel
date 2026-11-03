using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Missili.Abstractions;
using Missili.Persistence;

namespace Missili.Tests;

/// <summary>每个测试独占临时 SQLite 文件，不读取真实账号或业务数据。</summary>
internal sealed class TestStore : IAsyncDisposable
{
    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public InfrastructureOptions Options { get; }
    public SqliteStore Store { get; }
    public Guid RunId { get; } = Guid.NewGuid();
    public static DateTimeOffset Now { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>创建唯一测试目录和尚未初始化的存储。</summary>
    private TestStore()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "history.db");
        Options = new InfrastructureOptions { DataDirectory = DirectoryPath };
        Store = new SqliteStore(DatabasePath, Options);
    }

    /// <summary>通过实际迁移初始化新库。</summary>
    public static async Task<TestStore> CreateAsync()
    {
        TestStore result = new();
        await result.Store.InitializeAsync(result.RunId, CancellationToken.None);
        return result;
    }

    /// <summary>构造固定的合成会话地址。</summary>
    public static MessageAddress Address(string conversation = "room") => new()
    {
        AdapterId = "test",
        PlatformId = "synthetic",
        BotAccountId = "bot",
        ConversationKind = "group",
        ConversationId = conversation
    };

    /// <summary>构造合成文本输入；键为空表示不推测内容去重。</summary>
    public static IncomingMessage Message(string text = "hello", string? key = null, string conversation = "room") => new()
    {
        Address = Address(conversation),
        SenderId = "user",
        DeduplicationKey = key,
        OccurredAt = Now,
        Segments = [new IncomingSegment { Kind = SegmentTypes.Text, Text = text }]
    };

    /// <summary>构造无需真实下载即可测试引用存储的合成媒体输入。</summary>
    public static IncomingMessage MediaMessage(Uri uri, string? sourceKey = null) => new()
    {
        Address = Address(),
        SenderId = "user",
        Segments =
        [new IncomingSegment { Kind = SegmentTypes.Image, Media = [new IncomingMedia { Kind = MediaKind.Image, Source = MediaSource.Http(uri, sourceKey) }] }]
    };

    /// <summary>查询请求显式表达时间和可见性，避免测试隐式默认行为。</summary>
    public static HistoryQuery Query(int size = 100) => new()
    {
        Time = HistoryTime.Received,
        IncludeDeleted = true,
        IncludeRecalled = true,
        IncludeUnconfirmed = true,
        PageSize = size
    };

    /// <summary>只清理本测试创建的目录；先释放对应连接池，避免 Windows 文件占用。</summary>
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString());
        SqliteConnection.ClearPool(connection);
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missili.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(DirectoryPath).StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("测试清理路径越界。");
        Directory.Delete(DirectoryPath, true);
    }
}
