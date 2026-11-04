using System.Text.Json;

namespace Missili.Tools.ModelMock;

/// <summary>独立合成模型协议服务，默认只监听回环地址。</summary>
public static class Program
{
    /// <summary>读取可选场景文件，控制台输入信号名可推进暂停的响应。</summary>
    public static async Task Main(string[] args)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddCommandLine(args).Build();
        string? path = configuration["scenario"];
        ModelMockScenario scenario = path is null ? new ModelMockScenario() : JsonSerializer.Deserialize<ModelMockScenario>(await File.ReadAllTextAsync(path), ModelMockServer.JsonOptions)
            ?? throw new InvalidDataException("模型 Mock 场景为空。");
        using CancellationTokenSource stop = new();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        await using ModelMockServer server = await ModelMockServer.StartAsync(scenario, configuration["url"] ?? "http://127.0.0.1:24080", stop.Token);
        Task requests = ObserveRequestsAsync(server, stop.Token);
        Console.WriteLine("模型 Mock 已启动：" + server.BaseAddress);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Console 的同步包装会阻塞所谓异步读取；一个控制台读取最多占用一个后台线程。
                // 停止只结束等待，先释放实际网络服务，再由进程退出结束尚未返回的终端读取。
                // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Console/src/System/IO/SyncTextReader.cs
                string? signal = await Task.Run(Console.ReadLine, stop.Token).WaitAsync(stop.Token);
                if (signal is null) { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); break; }
                server.Release(signal);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await stop.CancelAsync(); await requests; }
    }

    /// <summary>独立进程持续消费有界通知，避免运行 256 次后因没有测试消费者而停住。</summary>
    private static async Task ObserveRequestsAsync(ModelMockServer server, CancellationToken token)
    {
        try
        {
            while (true)
            {
                ModelMockRequest request = await server.NextRequestAsync(token);
                Console.WriteLine($"请求 {request.Sequence}：{request.Path}，模型 {request.Model}，流式 {request.Streaming}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
