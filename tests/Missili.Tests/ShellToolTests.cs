using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>官方 Shell 包经真实插件加载器执行受控命令，测试不引用实现程序集。</summary>
public sealed class ShellToolTests
{
    /// <summary>真实解释器保留中文双管道输出与退出码，非零退出不伪装成成功。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RealShellReturnsUtf8OutputAndExitStatus(int exitCode)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await FixtureAsync(token);
        await LoadAsync(fixture, Settings(), token);
        string command = OperatingSystem.IsWindows()
            ? "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Out.Write('中文输出'); [Console]::Error.Write('错误输出'); exit " + exitCode.ToString(CultureInfo.InvariantCulture)
            : "printf '中文输出'; printf '错误输出' >&2; exit " + exitCode.ToString(CultureInfo.InvariantCulture);
        ToolExecutionResult result = await ExecuteAsync(fixture, command, TimeProvider.System, token);
        Assert.Equal(exitCode == 0 ? ToolCallState.Succeeded : ToolCallState.Failed, result.State);
        Assert.Equal("中文输出", result.Data!.Value.GetProperty("stdout").GetString());
        Assert.Equal("错误输出", result.Data.Value.GetProperty("stderr").GetString());
        Assert.Equal(exitCode, result.Data.Value.GetProperty("exit_code").GetInt32());
        Assert.True(result.Data.Value.GetProperty("output_complete").GetBoolean());
    }

    /// <summary>超过保留上限仍排空两条管道，使大量输出的命令能够正常退出。</summary>
    [Fact]
    public async Task OutputLimitDoesNotBlockEitherPipe()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await FixtureAsync(token);
        Dictionary<string, object> settings = Settings(); settings["maximumOutputBytes"] = 4096;
        await LoadAsync(fixture, settings, token);
        string command = OperatingSystem.IsWindows()
            ? "[Console]::Out.Write(('x' * 100000)); [Console]::Error.Write(('y' * 100000))"
            : "i=0; while [ \"$i\" -lt 10000 ]; do printf 'stdout-data'; printf 'stderr-data' >&2; i=$((i+1)); done";
        ToolExecutionResult result = await ExecuteAsync(fixture, command, TimeProvider.System, token);
        Assert.Equal(ToolCallState.Succeeded, result.State);
        Assert.False(result.Complete);
        Assert.False(result.Data!.Value.GetProperty("output_complete").GetBoolean());
        Assert.True(result.Data.Value.GetProperty("received_output_bytes").GetInt64() > 4096);
        string stdout = result.Data.Value.GetProperty("stdout").GetString()!, stderr = result.Data.Value.GetProperty("stderr").GetString()!;
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(stdout) + Encoding.UTF8.GetByteCount(stderr));
    }

    /// <summary>有限模型预览与完整结果分离，压缩预览不会覆盖已取得的原始输出。</summary>
    [Fact]
    public async Task LargeOutputKeepsFullDataAndBoundedModelProjection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await FixtureAsync(token);
        Dictionary<string, object> settings = Settings(); settings["maximumPreviewBytes"] = 2048;
        await LoadAsync(fixture, settings, token);
        string command = OperatingSystem.IsWindows() ? "[Console]::Out.Write(('x' * 20000))"
            : "i=0; while [ \"$i\" -lt 2000 ]; do printf 'xxxxxxxxxx'; i=$((i+1)); done";
        ToolExecutionResult result = await ExecuteAsync(fixture, command, TimeProvider.System, token);
        Assert.Equal(20000, result.Data!.Value.GetProperty("stdout").GetString()!.Length);
        Assert.True(result.Complete);
        Assert.True(Encoding.UTF8.GetByteCount(result.ModelData!.Value.GetRawText()) <= 2048);
        Assert.False(result.ModelData.Value.GetProperty("output_complete").GetBoolean());
    }

    /// <summary>取消及可控期限都等待普通子进程真实退出，不只结束父进程的等待 Task。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeoutWaitForChildProcesses(bool timeout)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeTimeProvider clock = new();
        await using RuntimeFixture fixture = await FixtureAsync(token);
        string signal = Path.Combine(fixture.Root, "shell-started.txt");
        string variable = "MISSILI_SHELL_SIGNAL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, signal);
        try
        {
            Dictionary<string, object> settings = Settings();
            settings["environmentReferences"] = new Dictionary<string, string> { ["MISSILI_TEST_SIGNAL"] = variable };
            await LoadAsync(fixture, settings, token);
            string command = OperatingSystem.IsWindows()
                ? "$p=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' -PassThru -WindowStyle Hidden; [IO.File]::WriteAllText($env:MISSILI_TEST_SIGNAL, ([string]$PID + [Environment]::NewLine + [string]$p.Id)); $p.WaitForExit()"
                : "sleep 60 & child=$!; printf '%s\\n%s' \"$$\" \"$child\" > \"$MISSILI_TEST_SIGNAL\"; wait \"$child\"";
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<ToolExecutionResult> execution = ExecuteAsync(fixture, command, clock, cancellation.Token);
            int[] processes = await StartedAsync(signal, token);
            if (timeout) clock.Advance(TimeSpan.FromSeconds(31));
            else await cancellation.CancelAsync();
            ToolExecutionResult result = await execution.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.Equal(timeout ? ToolCallState.Failed : ToolCallState.CancelledDuringExecution, result.State);
            Assert.Equal(timeout, result.Data!.Value.GetProperty("timed_out").GetBoolean());
            Assert.All(processes, process => Assert.False(Running(process)));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>默认环境不继承任意宿主变量，显式引用才将指定值交给子进程。</summary>
    [Fact]
    public async Task EnvironmentRequiresExplicitReferences()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await FixtureAsync(token);
        string variable = "MISSILI_SHELL_SYNTHETIC_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic-explicit-value");
        try
        {
            await LoadAsync(fixture, Settings(), token);
            string probe = OperatingSystem.IsWindows() ? "[Console]::Write([Environment]::GetEnvironmentVariable('" + variable + "'))" : "printf '%s' \"$" + variable + "\"";
            Assert.Equal("", (await ExecuteAsync(fixture, probe, TimeProvider.System, token)).Data!.Value.GetProperty("stdout").GetString());
            await fixture.Runtime.Plugins.StopAsync("missili.tools.shell", token);
            Dictionary<string, object> settings = Settings(); settings["environmentReferences"] = new Dictionary<string, string> { ["PROBE"] = variable };
            await LoadAsync(fixture, settings, token);
            string explicitProbe = OperatingSystem.IsWindows() ? "[Console]::Write($env:PROBE)" : "printf '%s' \"$PROBE\"";
            Assert.Equal("synthetic-explicit-value", (await ExecuteAsync(fixture, explicitProbe, TimeProvider.System, token)).Data!.Value.GetProperty("stdout").GetString());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>模型模块只提供共享上下文服务，Shell 测试不调用任何模型。</summary>
    [Fact]
    public async Task ChangedEnvironmentCannotUsePreparedAction()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await FixtureAsync(token);
        string variable = "MISSILI_SHELL_BOUND_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "first-synthetic-value");
        try
        {
            Dictionary<string, object> settings = Settings(); settings["environmentReferences"] = new Dictionary<string, string> { ["PROBE"] = variable };
            await LoadAsync(fixture, settings, token);
            AgentToolDescriptor descriptor = Assert.Single(fixture.Runtime.Tools.CaptureTools());
            AgentToolContext context = Context(fixture, descriptor, TimeProvider.System);
            using IAgentToolLease lease = fixture.Runtime.Tools.TryAcquireTool(descriptor)!;
            PreparedToolCall prepared = await lease.PrepareAsync(context, JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["command"] = OperatingSystem.IsWindows() ? "[Console]::Write($env:PROBE)" : "printf '%s' \"$PROBE\"", ["working_directory"] = fixture.Root }), token);
            Assert.True(prepared.Valid, prepared.Error);
            Assert.DoesNotContain("first-synthetic-value", prepared.Arguments!.Value.GetRawText());
            Environment.SetEnvironmentVariable(variable, "second-synthetic-value");
            ToolExecutionResult result = await lease.ExecuteAsync(context, prepared, token);
            Assert.Equal(ToolCallState.Rejected, result.State); Assert.Equal(ToolEffectState.NotStarted, result.Effect);
            Assert.Equal("EnvironmentChanged", result.ErrorKind);
            Assert.Null(result.Data);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>模型模块只提供共享上下文服务，Shell 测试不调用任何模型。</summary>
    private static Task<RuntimeFixture> FixtureAsync(CancellationToken token) => RuntimeFixture.CreateAsync(token,
        models: ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1")));

    /// <summary>配置实际可用的系统解释器；缺失时失败而不是跳过核心验收。</summary>
    internal static Dictionary<string, object> Settings()
    {
        string executable = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe")).First(File.Exists)
            : "/bin/sh";
        return new Dictionary<string, object>
        {
            ["executable"] = Path.GetFullPath(executable),
            ["arguments"] = OperatingSystem.IsWindows() ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command" } : new[] { "-c" },
            ["defaultTimeoutSeconds"] = 30,
            ["maximumTimeoutSeconds"] = 60
        };
    }

    /// <summary>官方包来自构建输出，保证没有把实现加载进测试默认上下文。</summary>
    private static async Task LoadAsync(RuntimeFixture fixture, Dictionary<string, object> settings, CancellationToken token)
    {
        PluginStatus loaded = await fixture.Runtime.Plugins.LoadAsync(OfficialPluginPackages.GetPath("shell"), JsonSerializer.SerializeToElement(settings), token);
        Assert.Equal(PluginState.Active, loaded.State);
    }

    /// <summary>真实准备及执行各自使用固定代次资格，令牌联结使停用能够结束命令。</summary>
    private static async Task<ToolExecutionResult> ExecuteAsync(RuntimeFixture fixture, string command, TimeProvider clock, CancellationToken token)
    {
        AgentToolDescriptor descriptor = Assert.Single(fixture.Runtime.Tools.CaptureTools(), tool => tool.Enabled && tool.Definition.Id == "shell.execute");
        AgentToolContext context = Context(fixture, descriptor, clock);
        PreparedToolCall prepared;
        using (IAgentToolLease preparation = fixture.Runtime.Tools.TryAcquireTool(descriptor)!)
        {
            prepared = await preparation.PrepareAsync(context, JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["command"] = command, ["working_directory"] = fixture.Root }), token);
        }
        Assert.True(prepared.Valid, prepared.Error);
        using IAgentToolLease execution = fixture.Runtime.Tools.TryAcquireTool(descriptor)!;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, execution.LifetimeToken);
        return await execution.ExecuteAsync(context, prepared, linked.Token);
    }

    /// <summary>构造共享上下文，测试不引用实际 Shell 实现程序集。</summary>
    private static AgentToolContext Context(RuntimeFixture fixture, AgentToolDescriptor descriptor, TimeProvider clock) => new()
    {
        Origin = new OperationOrigin { PluginId = descriptor.PluginId },
        Conversation = TestStore.Address(),
        Profile = fixture.Runtime.Profiles.Resolve(TestStore.Address()),
        Models = fixture.Runtime.Models,
        History = fixture.Runtime.History,
        AgentHistory = (IAgentHistory)fixture.Runtime.History,
        ToolHistory = (IAgentToolHistory)fixture.Runtime.History,
        Search = (IHistorySearch)fixture.Runtime.History,
        Assets = (IConversationAssetAccess)fixture.Runtime.History,
        Contents = fixture.Runtime.Contents,
        Media = fixture.Runtime.Media,
        Transcriptions = fixture.Runtime.Models.Transcriptions,
        Clock = clock
    };

    /// <summary>等待实际文件信号完整写入，只用期限作为失败上界而不猜测启动耗时。</summary>
    private static async Task<int[]> StartedAsync(string path, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (File.Exists(path))
            {
                string[] lines = await File.ReadAllLinesAsync(path, deadline.Token);
                if (lines.Length == 2 && int.TryParse(lines[0], out int root) && int.TryParse(lines[1], out int child)) return [root, child];
            }
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>只观察刚由测试启动的进程，已消失的标识表示目标退出。</summary>
    private static bool Running(int id)
    {
        try { using Process process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
