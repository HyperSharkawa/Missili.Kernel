using System.Text.Json;
using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>并发修改使用的明确冲突，调用者不能把它当作成功。</summary>
public sealed class RevisionConflictException : InvalidOperationException
{
    /// <summary>创建可读冲突信息，不包含用户内容。</summary>
    public RevisionConflictException() : base("记录已被其他操作修改，请重新读取后提交。") { }
}
