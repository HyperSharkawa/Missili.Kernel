using System.Text.Json;

namespace Missili.OneBot.Mock;

/// <summary>独立协议 Mock 入口，可供后续模型联调复用。</summary>
public static class Program
{
    /// <summary>读取合成场景，顺序等待真实连接和操作，不以固定休眠推进步骤。</summary>
    public static async Task Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("用法：Missili.OneBot.Mock <场景 JSON 文件>");
        using JsonDocument scenario = JsonDocument.Parse(await File.ReadAllTextAsync(args[0], System.Text.Encoding.UTF8));
        JsonElement root = scenario.RootElement;
        using CancellationTokenSource stop = new();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        await using MockPeer peer = new();
        if (root.TryGetProperty("selfId", out JsonElement self)) peer.SelfId = self.GetInt64();
        if (root.TryGetProperty("tokenEnvironmentVariable", out JsonElement tokenVariable)) peer.Token = Environment.GetEnvironmentVariable(tokenVariable.GetString()!);
        string mode = root.GetProperty("mode").GetString()!;
        string url = root.GetProperty("url").GetString()!;
        string layout = root.GetProperty("layout").GetString()!;
        string[] manual = root.TryGetProperty("manualActions", out JsonElement manualActions) ? manualActions.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
        JsonElement[] rules = root.TryGetProperty("responses", out JsonElement responses) ? responses.EnumerateArray().Select(item => item.Clone()).ToArray() : [];
        peer.ResponseHandler = async (action, cancellation) =>
        {
            foreach (JsonElement rule in rules)
            {
                if (rule.GetProperty("action").GetString() != action.Action) continue;
                if (rule.TryGetProperty("parameters", out JsonElement match) && match.EnumerateObject().Any(field =>
                    !action.Parameters.TryGetProperty(field.Name, out JsonElement actual) || !JsonElement.DeepEquals(field.Value, actual))) continue;
                await action.RespondAsync(rule.GetProperty("status").GetString()!, rule.GetProperty("retcode").GetInt32(),
                    rule.TryGetProperty("data", out JsonElement data) ? data.Clone() : null, cancellation);
                return true;
            }
            return manual.Contains(action.Action, StringComparer.Ordinal);
        };
        if (mode == "server") { await peer.StartServerAsync(url, stop.Token); Console.WriteLine("Mock 已监听：" + peer.BaseUrl); }
        else if (mode == "client")
        { foreach (string role in layout == "Universal" ? new[] { "Universal" } : new[] { "API", "Event" }) await peer.ConnectAsync(url, role, stop.Token); }
        else throw new ArgumentException("场景 mode 必须为 server 或 client。");
        string eventRole = layout == "Universal" ? "Universal" : "Event";
        try
        {
            if (root.TryGetProperty("steps", out JsonElement steps))
            {
                Dictionary<string, MockAction> requests = new(StringComparer.Ordinal);
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    string operation = step.GetProperty("operation").GetString()!;
                    if (operation == "event")
                    {
                        await peer.WaitConnectedAsync(eventRole, stop.Token);
                        await peer.SendAsync(eventRole, step.GetProperty("event").GetRawText(),
                            step.TryGetProperty("fragmentBytes", out JsonElement fragment) ? fragment.GetInt32() : int.MaxValue, stop.Token);
                    }
                    else if (operation == "awaitAction")
                    {
                        string action = step.GetProperty("action").GetString()!;
                        MockAction request;
                        do { request = await peer.NextActionAsync(stop.Token); } while (request.Action != action);
                        if (step.TryGetProperty("saveAs", out JsonElement alias)) requests.Add(alias.GetString()!, request);
                        Console.WriteLine("已收到操作：" + action);
                    }
                    else if (operation == "respond")
                    {
                        MockAction request = requests[step.GetProperty("request").GetString()!];
                        await request.RespondAsync(step.GetProperty("status").GetString()!, step.GetProperty("retcode").GetInt32(),
                            step.TryGetProperty("data", out JsonElement data) ? data.Clone() : null, stop.Token);
                    }
                    else if (operation == "assertCalls")
                    {
                        string action = step.GetProperty("action").GetString()!;
                        if (peer.RecordedActions.Count(item => item.GetProperty("action").GetString() == action) != step.GetProperty("count").GetInt32())
                            throw new InvalidOperationException("Mock 操作调用次数与场景断言不符。");
                    }
                    else if (operation == "signal")
                    {
                        Console.WriteLine("等待输入控制信号：" + step.GetProperty("name").GetString());
                        string expected = step.GetProperty("name").GetString()!;
                        while (await Console.In.ReadLineAsync(stop.Token) != expected) { }
                    }
                    else if (operation == "abort") peer.Abort(step.GetProperty("role").GetString()!);
                    else throw new ArgumentException("不支持的 Mock 场景步骤。");
                }
                Console.WriteLine("场景步骤已完成。");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
