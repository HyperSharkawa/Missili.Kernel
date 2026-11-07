using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Tools.Shell;

/// <summary>非交互 local shell，每次调用拥有进程和管道，默认不提供任何沙盒隔离。</summary>
public sealed class ShellPlugin : IPlugin, IAgentToolHandler
{
    private ShellSettings? _settings;
    private ShellEnvironment? _environment;

    /// <summary>校验明确配置的解释器并登记工具，不启动命令或发现其他凭据。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = ShellSettings.Read(context.Configuration);
        _environment = new ShellEnvironment();
        _ = context.Tasks.Track(_environment);
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"command":{"type":"string","description":"交给已配置解释器执行的完整非交互命令"},"working_directory":{"type":"string","description":"现有绝对工作目录"},"timeout_seconds":{"type":"integer","minimum":1,"description":"执行期限，不能超过宿主配置"}},"required":["command","working_directory"],"additionalProperties":false}""");
        context.Tools.Register(new AgentToolDefinition
        {
            Id = "shell.execute",
            Name = "shell_execute",
            Description = "在 Bot 当前执行环境运行一次非交互命令；使用实际进程权限，没有沙盒。",
            OutputDescription = "启动状态、退出码、标准输出和错误、取消及超时状态；大输出使用模型预览与完整工具记录。",
            Parameters = schema.RootElement,
            HasSideEffects = true,
            ExecutionTimeout = null,
            PermissionCategory = "host.execute"
        }, this);
        return Task.CompletedTask;
    }

    /// <summary>Nexus 取消并等待实际调用退出，不能在停止回调中提前丢弃在途进程。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>冻结将执行的命令、目录和期限；环境变量只记录明确的来源，不复制凭据值。</summary>
    public Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShellSettings settings = _settings!;
        if (arguments.ValueKind != JsonValueKind.Object) return Invalid("$", "参数必须是对象。");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty field in arguments.EnumerateObject())
            if (!names.Add(field.Name) || field.Name is not ("command" or "working_directory" or "timeout_seconds")) return Invalid("$." + field.Name, "参数重复或未声明。");
        if (!arguments.TryGetProperty("command", out JsonElement commandValue) || commandValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(commandValue.GetString()))
            return Invalid("$.command", "命令必须是非空字符串。");
        if (commandValue.GetString()!.Contains('\0')) return Invalid("$.command", "命令不能包含空字符。");
        if (!arguments.TryGetProperty("working_directory", out JsonElement directoryValue) || directoryValue.ValueKind != JsonValueKind.String ||
            directoryValue.GetString() is not string directory || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            return Invalid("$.working_directory", "工作目录必须是存在的绝对路径。");
        int seconds = settings.DefaultTimeoutSeconds;
        if (arguments.TryGetProperty("timeout_seconds", out JsonElement timeout) && (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out seconds)))
            return Invalid("$.timeout_seconds", "期限必须是整数秒。");
        if (seconds < 1 || seconds > settings.MaximumTimeoutSeconds) return Invalid("$.timeout_seconds", "期限超出配置范围。");
        Dictionary<string, string> environment;
        try { environment = ShellEnvironment.Capture(settings); }
        catch (InvalidOperationException exception) { return Invalid("$.environment", exception.Message); }
        JsonElement prepared = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        { ["command"] = commandValue.GetString()!, ["working_directory"] = Path.GetFullPath(directory), ["timeout_seconds"] = seconds, ["environment_seal"] = _environment!.Seal(environment) });
        return Task.FromResult(new PreparedToolCall
        {
            Arguments = prepared,
            ExecutionDescription = "执行环境为 local，无沙盒。解释器：" + settings.Executable + "；固定参数：" + JsonSerializer.Serialize(settings.Arguments) +
                "；继承变量名：" + string.Join(", ", settings.InheritedEnvironment) + "；显式环境来源：" + JsonSerializer.Serialize(settings.EnvironmentReferences)
        });
    }

    /// <summary>启动后直到进程及两条管道真实退出才返回，取消不代表外部副作用被撤销。</summary>
    public async Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
    {
        ShellSettings settings = _settings!;
        JsonElement arguments = prepared.Arguments!.Value;
        Dictionary<string, string> environment;
        try { environment = ShellEnvironment.Capture(settings); }
        catch (InvalidOperationException exception) { return new ToolExecutionResult { State = ToolCallState.Rejected, Error = exception.Message, ErrorKind = "Environment" }; }
        if (_environment!.Seal(environment) != arguments.GetProperty("environment_seal").GetString())
            return new ToolExecutionResult { State = ToolCallState.Rejected, Error = "准备后执行环境发生变化，需要重新提交调用。", ErrorKind = "EnvironmentChanged" };
        ProcessStartInfo start = new(settings.Executable)
        {
            WorkingDirectory = arguments.GetProperty("working_directory").GetString()!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in settings.Arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(arguments.GetProperty("command").GetString()!);
        start.Environment.Clear();
        foreach (KeyValuePair<string, string> item in environment) start.Environment[item.Key] = item.Value;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(arguments.GetProperty("timeout_seconds").GetInt32()), context.Clock);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using Process process = new() { StartInfo = start };
        using OutputCapture output = new(settings.MaximumOutputBytes);
        Task? readers = null;
        bool started = false;
        bool cancelled = false;
        bool timedOut = false;
        string? error = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new InvalidOperationException("解释器未能启动。");
            process.StandardInput.Close();
            readers = Task.WhenAll(output.ReadAsync(process.StandardOutput.BaseStream, false, linked.Token), output.ReadAsync(process.StandardError.BaseStream, true, linked.Token));
            Task exited = process.WaitForExitAsync(linked.Token);
            Task first = await Task.WhenAny(exited, readers).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await exited.ConfigureAwait(false);
            await readers.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        { cancelled = cancellationToken.IsCancellationRequested; timedOut = !cancelled && deadline.IsCancellationRequested; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { error = "命令执行失败：" + exception.GetType().Name; }
        finally
        {
            if (started)
            {
                if (!process.HasExited)
                {
                    try { await ProcessTree.StopAsync(process).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    { error ??= "进程树结束状态异常：" + exception.GetType().Name; }
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false);
                if (readers is not null)
                {
                    try { await readers.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                    catch (IOException) { error ??= "命令输出管道读取失败。"; }
                }
            }
        }
        int? exitCode = started ? process.ExitCode : null;
        (string stdout, string stderr, bool valid) = output.Text();
        if (!valid) error ??= "命令输出包含无效 UTF-8；保留可读部分。";
        ToolCallState state = cancelled ? (started ? ToolCallState.CancelledDuringExecution : ToolCallState.CancelledBeforeExecution)
            : timedOut || error is not null || exitCode != 0 ? ToolCallState.Failed : ToolCallState.Succeeded;
        Dictionary<string, object?> data = new()
        {
            ["started"] = started,
            ["exit_code"] = exitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
            ["cancelled"] = cancelled,
            ["timed_out"] = timedOut,
            ["output_complete"] = valid && !output.Truncated,
            ["received_output_bytes"] = output.ReceivedBytes
        };
        JsonElement full = JsonSerializer.SerializeToElement(data);
        int previewLimit = Math.Min(settings.MaximumPreviewBytes, Math.Max(512, context.MaximumResultBytes - 1024));
        JsonElement preview = Preview(data, previewLimit);
        return new ToolExecutionResult
        {
            State = state,
            Data = full,
            ModelData = preview,
            Text = stdout + (stdout.Length > 0 && stderr.Length > 0 ? "\n" : "") + stderr,
            Complete = !cancelled && !timedOut && error is null && !output.Truncated,
            Effect = !started ? ToolEffectState.NotStarted : cancelled || timedOut || error is not null ? ToolEffectState.Unknown : ToolEffectState.Confirmed,
            Error = error ?? (timedOut ? "命令超过执行期限。" : cancelled ? "命令已取消。" : exitCode != 0 ? "命令返回非零退出码。" : null),
            ErrorKind = timedOut ? "Timeout" : cancelled ? "Cancelled" : error is not null ? "Process" : exitCode != 0 ? "ExitCode" : null
        };
    }

    /// <summary>模型投影按实际 JSON 字节缩小，两路完整保留内容仍在 Data 中。</summary>
    private static JsonElement Preview(Dictionary<string, object?> full, int limit)
    {
        Dictionary<string, object?> preview = new(full, StringComparer.Ordinal);
        preview["full_output_in_tool_record"] = true;
        JsonElement value = JsonSerializer.SerializeToElement(preview);
        while (Encoding.UTF8.GetByteCount(value.GetRawText()) > limit)
        {
            string stdout = (string)preview["stdout"]!, stderr = (string)preview["stderr"]!;
            if (stdout.Length == 0 && stderr.Length == 0) throw new InvalidOperationException("工具结果容量不足以容纳 Shell 状态。");
            preview["stdout"] = Prefix(stdout, stdout.Length / 2);
            preview["stderr"] = Prefix(stderr, stderr.Length / 2);
            preview["output_complete"] = false;
            value = JsonSerializer.SerializeToElement(preview);
        }
        return value;
    }

    /// <summary>预览裁剪不留下孤立的 UTF-16 高代理项。</summary>
    private static string Prefix(string text, int length) => text[..(length > 0 && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    /// <summary>准备失败提供字段路径，不执行解释器。</summary>
    private static Task<PreparedToolCall> Invalid(string path, string error) => Task.FromResult(new PreparedToolCall { ErrorPath = path, Error = error });

    /// <summary>两条输出管道共用存储预算，超过上限后继续排空但不继续分配输出内存。</summary>
    private sealed class OutputCapture : IDisposable
    {
        private readonly object _gate = new();
        private readonly int _maximum;
        private readonly MemoryStream _stdout = new();
        private readonly MemoryStream _stderr = new();
        public long ReceivedBytes { get; private set; }
        public bool Truncated { get; private set; }

        /// <summary>预算限制两路累计原始字节，不分别授予两倍额度。</summary>
        public OutputCapture(int maximum) => _maximum = maximum;

        /// <summary>每次固定缓冲读取，记录已到达字节并持续排空超限内容。</summary>
        public async Task ReadAsync(Stream stream, bool error, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                lock (_gate)
                {
                    ReceivedBytes += count;
                    int available = Math.Min(count, _maximum - checked((int)(_stdout.Length + _stderr.Length)));
                    if (available > 0) (error ? _stderr : _stdout).Write(buffer, 0, available);
                    if (available < count) Truncated = true;
                }
            }
        }

        /// <summary>截断时只解码完整前缀，正常输出若有非法编码则明确报告。</summary>
        public (string Stdout, string Stderr, bool Valid) Text()
        {
            bool valid = true;
            return (Decode(_stdout, ref valid), Decode(_stderr, ref valid), valid);
        }

        /// <summary>UTF-8 解码器保留边界语义，不把截断末字节伪造成乱码正文。</summary>
        private string Decode(MemoryStream content, ref bool valid)
        {
            byte[] bytes = content.ToArray();
            try
            {
                Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
                char[] text = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                decoder.Convert(bytes, text, !Truncated, out int usedBytes, out int usedChars, out bool completed);
                return new string(text, 0, usedChars);
            }
            catch (DecoderFallbackException) { valid = false; return Encoding.UTF8.GetString(bytes); }
        }

        /// <summary>调用结束后释放有限输出缓冲。</summary>
        public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); }
    }
}
