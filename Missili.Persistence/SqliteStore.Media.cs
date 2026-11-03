using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>媒体引用、文件提交日志和转述缓存的持久化操作。</summary>
public sealed partial class SqliteStore
{
    /// <summary>原子领取一批待获取或待收尾的引用，避免同一记录重复领取。</summary>
    public Task<ImmutableArray<MediaWork>> ClaimMediaAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return WriteAsync(database =>
        {
            List<MediaEntity> rows = database.MediaReferences.Where(item => !item.Claimed &&
                (item.State == MediaState.Pending || item.State == MediaState.Finalizing))
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(count).ToList();
            foreach (MediaEntity row in rows)
            {
                row.Claimed = true;
                if (row.State == MediaState.Pending) row.State = MediaState.Downloading;
            }
            database.SaveChanges();
            return rows.Select(MediaSnapshot).ToImmutableArray();
        }, cancellationToken);
    }

    /// <summary>读取单个媒体引用，包括宿主内部恢复信息。</summary>
    public Task<MediaWork?> GetMediaAsync(Guid id, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        MediaEntity? row = database.MediaReferences.AsNoTracking().SingleOrDefault(item => item.Id == id);
        return row is null ? null : MediaSnapshot(row);
    }, cancellationToken);

    /// <summary>只复用已归档记录，不将临时 URL 作为来源身份。</summary>
    public Task<MediaWork?> FindReusableMediaAsync(string sourceKey, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        MediaEntity? row = database.MediaReferences.AsNoTracking().Where(item => item.SourceKey == sourceKey && item.State == MediaState.Available)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        return row is null ? null : MediaSnapshot(row);
    }, cancellationToken);

    /// <summary>在移动文件前保存恢复日志，清理状态不能被自动撤销。</summary>
    public Task SetMediaFinalizingAsync(Guid id, string contentKey, long length, string temporaryPath, string relativePath, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        MediaEntity row = database.MediaReferences.Single(item => item.Id == id);
        if (row.State != MediaState.Downloading) throw new InvalidOperationException("媒体当前不在下载状态。");
        row.State = MediaState.Finalizing;
        row.ContentKey = contentKey;
        row.Length = length;
        row.TemporaryPath = temporaryPath;
        row.RelativePath = relativePath;
        database.SaveChanges();
        return true;
    }, cancellationToken);

    /// <summary>复用或新增原件并提交引用，只有此步骤成功才对读取者报告可用。</summary>
    public Task CompleteMediaAsync(Guid id, string contentKey, long length, string relativePath, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        MediaEntity row = database.MediaReferences.Single(item => item.Id == id);
        if (row.State is not (MediaState.Downloading or MediaState.Finalizing or MediaState.Available))
            throw new InvalidOperationException("当前媒体状态不能完成归档。");
        BlobEntity? blob = database.MediaBlobs.Find(contentKey);
        if (blob is null)
        {
            database.MediaBlobs.Add(new BlobEntity { ContentKey = contentKey, Length = length, RelativePath = relativePath });
        }
        else if (blob.Length != length || blob.RelativePath != relativePath)
        {
            throw new InvalidOperationException("内容键对应的原件元数据不一致。");
        }
        row.ContentKey = contentKey;
        row.Length = length;
        row.RelativePath = relativePath;
        row.TemporaryPath = null;
        row.Error = null;
        row.Claimed = false;
        row.State = MediaState.Available;
        database.SaveChanges();
        return true;
    }, cancellationToken);

    /// <summary>记录一次归档失败，保留暂存登记供有归属的后续诊断。</summary>
    public Task FailMediaAsync(Guid id, string error, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        MediaEntity row = database.MediaReferences.Single(item => item.Id == id);
        // 已清理和已可用状态不是本次失败能够覆盖的状态。
        if (row.State is MediaState.Cleaned or MediaState.Available) return false;
        row.State = MediaState.Failed;
        row.Error = error;
        row.Claimed = false;
        database.SaveChanges();
        return true;
    }, cancellationToken);

    /// <summary>按内容和转述种类读取成功缓存及最后一次失败信息。</summary>
    public Task<TranscriptionSnapshot?> GetTranscriptionAsync(string contentKey, string kind, CancellationToken cancellationToken = default) => ReadAsync(database =>
    {
        TranscriptionEntity? row = database.MediaTranscriptions.AsNoTracking().SingleOrDefault(item => item.ContentKey == contentKey && item.Kind == kind);
        return row is null ? null : TranscriptionSnapshotOf(row);
    }, cancellationToken);

    /// <summary>使用应用修订号提交转述，模型和提示词只记录来源、不参与缓存失效。</summary>
    public Task<TranscriptionSnapshot> SaveTranscriptionAsync(TranscriptionSnapshot result, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result.ContentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Text);
        if (!result.CompletedAt.HasValue) throw new ArgumentException("成功转述必须提供完成时间。");
        return ChangeTranscriptionAsync(result.ContentKey, result.Kind, expectedRevision, row =>
        {
            row.Text = result.Text;
            row.Provider = result.Provider;
            row.Model = result.Model;
            row.Prompt = result.Prompt;
            row.CompletedAt = result.CompletedAt.Value.ToUnixTimeMilliseconds();
            row.LastError = null;
        }, cancellationToken);
    }

    /// <summary>失败只更新失败字段，绝不清空已有成功文本。</summary>
    public Task<TranscriptionSnapshot> RecordTranscriptionFailureAsync(string contentKey, string kind, string error, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return ChangeTranscriptionAsync(contentKey, kind, expectedRevision, row => row.LastError = error, cancellationToken);
    }

    /// <summary>在一次事务中执行转述修订检查与更新；冲突必须显式返回。</summary>
    private Task<TranscriptionSnapshot> ChangeTranscriptionAsync(string key, string kind, long expectedRevision, Action<TranscriptionEntity> change, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return WriteAsync(database =>
        {
            using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = database.Database.BeginTransaction();
            TranscriptionEntity? row = database.MediaTranscriptions.Find(key, kind);
            if ((row?.Revision ?? 0) != expectedRevision) throw new RevisionConflictException();
            if (row is null)
            {
                if (!database.MediaBlobs.Any(item => item.ContentKey == key)) throw new KeyNotFoundException("原件内容键不存在。");
                row = new TranscriptionEntity { ContentKey = key, Kind = kind };
                database.MediaTranscriptions.Add(row);
            }
            change(row);
            row.Revision++;
            try
            {
                database.SaveChanges();
                transaction.Commit();
            }
            catch (DbUpdateConcurrencyException) { throw new RevisionConflictException(); }
            catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
            { throw new RevisionConflictException(); }
            return TranscriptionSnapshotOf(row);
        }, token);
    }

    /// <summary>构造不带 EF 跟踪的内部媒体快照。</summary>
    private static MediaWork MediaSnapshot(MediaEntity row) => new()
    {
        Status = new MediaStatus { Id = row.Id, Kind = row.Kind, State = row.State, ContentKey = row.ContentKey, Length = row.Length, Error = row.Error },
        Source = new Uri(row.Source),
        SourceKey = row.SourceKey,
        TemporaryPath = row.TemporaryPath,
        RelativePath = row.RelativePath
    };

    /// <summary>复制当前转述结果，保留最近失败但不把失败文本变成成功文本。</summary>
    private static TranscriptionSnapshot TranscriptionSnapshotOf(TranscriptionEntity row) => new()
    {
        ContentKey = row.ContentKey,
        Kind = row.Kind,
        Text = row.Text,
        Provider = row.Provider,
        Model = row.Model,
        Prompt = row.Prompt,
        CompletedAt = row.CompletedAt.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(row.CompletedAt.Value) : null,
        LastError = row.LastError,
        Revision = row.Revision
    };
}
