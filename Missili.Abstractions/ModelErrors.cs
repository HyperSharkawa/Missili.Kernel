namespace Missili.Abstractions;

/// <summary>本地输入不符合已声明能力或资源上限，协调器不得当作网络故障反复请求。</summary>
public sealed class ModelInputException : InvalidOperationException
{
    /// <summary>保存不包含凭据的输入诊断。</summary>
    public ModelInputException(string message) : base(message) { }
}

/// <summary>增量消费者失败属于调用方，不能启动其他付费候选。</summary>
public sealed class ModelConsumerException : InvalidOperationException
{
    /// <summary>不保留业务插件异常对象，避免故障历史阻止卸载。</summary>
    public ModelConsumerException() : base("模型增量接收器执行失败。") { }
}

/// <summary>排障写入失败时立即停止模型调用，不执行模型候选重试。</summary>
public sealed class ModelRecordingException : IOException
{
    /// <summary>只保留明确的存储诊断，不引用提供商专有异常。</summary>
    public ModelRecordingException() : base("模型调用记录保存失败。") { }
}
