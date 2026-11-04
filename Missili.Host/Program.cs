using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Missili.Abstractions;
using Missili.Kernel;

namespace Missili.Host;

/// <summary>基础设施宿主入口；平台由明确启用的动态插件接入。</summary>
public static class Program
{
    /// <summary>构建并运行 Web 宿主，退出时异步释放基础设施。</summary>
    public static async Task Main(string[] args)
    {
        await using WebApplication application = BuildApplication(args);
        await application.RunAsync();
    }

    /// <summary>组装真实运行时；配置回调用于隔离测试覆盖默认选项与传输注册。</summary>
    public static WebApplication BuildApplication(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        InfrastructureOptions options = builder.Configuration.GetSection("Infrastructure").Get<InfrastructureOptions>() ?? new InfrastructureOptions();
        options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
        options.Validate();
        ModelOptions models = builder.Configuration.GetSection("Models").Get<ModelOptions>() ?? new ModelOptions();
        models = models with { ConfigurationDirectory = Path.GetFullPath(models.ConfigurationDirectory, builder.Environment.ContentRootPath) };
        models.Validate();
        builder.Services.AddSingleton(models);
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(services => InfrastructureFactory.Create(
            services.GetRequiredService<InfrastructureOptions>(), services.GetRequiredService<ILoggerFactory>(),
            services.GetServices<IMessageTransport>(), services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ModelOptions>()));
        builder.Services.AddHostedService<InfrastructureLifetime>();
        builder.Services.AddAuthorization();
        builder.Services.AddOpenApi();
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, HostJsonContext.Default));
        WebApplication app = builder.Build();
        if (app.Environment.IsDevelopment()) app.MapOpenApi();
        app.UseWebSockets();
        app.UseAuthorization();
        app.MapGet("/health/live", Live);
        app.MapGet("/health/ready", ReadyAsync);
        app.Map("/adapters/{adapterId}/ws", AdapterWebSocketEndpoint.HandleAsync);
        return app;
    }

    /// <summary>只报告进程存活，不返回任何业务数据。</summary>
    private static IResult Live() => TypedResults.Ok(new HealthStatus { Status = "live" });

    /// <summary>实际检查初始化和存储可用性，失败响应不包含内部错误详情。</summary>
    private static async Task<IResult> ReadyAsync(InfrastructureRuntime runtime, CancellationToken cancellationToken) =>
        await runtime.CheckReadyAsync(cancellationToken)
            ? TypedResults.Ok(new HealthStatus { Status = "ready" })
            : TypedResults.Json(new HealthStatus { Status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>固定健康响应，不包含聊天、配置或本地路径。</summary>
public sealed record HealthStatus
{
    public required string Status { get; init; }
}

/// <summary>为固定 HTTP DTO 生成序列化元数据，动态插件加载仍保持正常 JIT。</summary>
[JsonSerializable(typeof(HealthStatus))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class HostJsonContext : JsonSerializerContext { }
