using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missili.Abstractions;
using Missili.Kernel;
using Missili.Nexus;
using Xunit;

namespace Missili.Tests;

/// <summary>验证独立配置档、首匹配路由以及发布失败时的快照边界。</summary>
public sealed class ConversationConfigurationTests
{
    /// <summary>共享配置不共享身份，旧快照不受后续发布或无效修改影响。</summary>
    [Fact]
    public async Task RoutesUseIndependentProfilesAndImmutableRevisions()
    {
        await using ConversationConfigurationService service = new(Path.GetTempPath(), 8, NullLogger.Instance, TimeProvider.System, new PluginConfigurationDirectory());
        ConversationConfiguration configuration = Configuration(new Uri("http://127.0.0.1/"));
        ConversationProfile defaultProfile = configuration.Profiles[0] with { MaximumMessages = 70 };
        ConversationProfile groupProfile = new() { Id = "group" };
        await service.PublishAsync(configuration with
        {
            Profiles = [defaultProfile, groupProfile],
            Routes =
            [new ConversationRoute { ConversationId = "room*", ProfileId = "group" }, new ConversationRoute { ProfileId = "default" }]
        }, TestContext.Current.CancellationToken);
        ConversationProfileSnapshot old = service.Resolve(TestStore.Address());
        Assert.Equal("group", old.Profile.Id);
        Assert.Equal(100, old.Profile.MaximumMessages);
        Assert.Equal(70, service.Resolve(TestStore.Address("private")).Profile.MaximumMessages);
        await service.PublishAsync(configuration with { Profiles = [defaultProfile with { MaximumMessages = 69 }] }, TestContext.Current.CancellationToken);
        ConversationProfileSnapshot updated = service.Resolve(TestStore.Address());
        Assert.True(updated.Revision > old.Revision);
        Assert.Equal("group", old.Profile.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(configuration with { DefaultProfileId = "missing" }, TestContext.Current.CancellationToken));
        Assert.Equal(updated.Revision, service.Resolve(TestStore.Address()).Revision);
    }

    /// <summary>非法窗口和不存在的模型不能作为有效配置发布。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(100, 0)]
    public async Task InvalidWindowRejected(int maximum, int discard)
    {
        await using ConversationConfigurationService service = new(Path.GetTempPath(), 8, NullLogger.Instance, TimeProvider.System, new PluginConfigurationDirectory());
        ConversationConfiguration config = Configuration(new Uri("http://127.0.0.1/"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(config with { Profiles = [config.Profiles[0] with { MaximumMessages = maximum, DiscardMessages = discard }] }, TestContext.Current.CancellationToken));
    }

    /// <summary>文件配置使用独立档案和统一引用校验，拼错字段不会被默认值掩盖。</summary>
    [Fact]
    public async Task FileReloadRejectsUnknownFieldsWithoutReplacingPublishedConfig()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "catalog.json"), """{"providers":[{"id":"test","protocol":"ChatCompletions","baseUrl":"http://127.0.0.1/","apiKeyEnvironmentVariable":"SYNTHETIC"}],"models":[{"id":"A","providerId":"test","name":"A"}]}""", token);
            await File.WriteAllTextAsync(Path.Combine(root, "routes.json"), """{"defaultProfileId":"default","routes":[]}""", token);
            string profile = Path.Combine(root, "profiles/default.json");
            await File.WriteAllTextAsync(profile, """{"id":"default"}""", token);
            await using ConversationConfigurationService service = new(root, 8, NullLogger.Instance, TimeProvider.System, new PluginConfigurationDirectory());
            await service.ReloadAsync(TestContext.Current.CancellationToken);
            long revision = service.Resolve(TestStore.Address()).Revision;
            await File.WriteAllTextAsync(profile, """{"id":"default","maximumMesages":2}""", token);
            await Assert.ThrowsAsync<JsonException>(() => service.ReloadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(revision, service.Resolve(TestStore.Address()).Revision);
            await File.WriteAllTextAsync(profile, """{"id":"default","agent":{"instructions":null}}""", token);
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReloadAsync(token));
            Assert.Equal(revision, service.Resolve(TestStore.Address()).Revision);
            Assert.Equal(AgentDefaults.Instructions, service.Resolve(TestStore.Address()).Profile.Agent.Instructions);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>创建无真实凭据的模型目录，供协议协调和配置测试复用。</summary>
    internal static ConversationConfiguration Configuration(Uri address, string variable = "MISSILI_SYNTHETIC_UNUSED") => new()
    {
        DefaultProfileId = "default",
        Providers = [new ModelProviderDefinition { Id = "test", Protocol = ModelProtocol.ChatCompletions, BaseUrl = address, ApiKeyEnvironmentVariable = variable }],
        Models = [new ModelDefinition { Id = "A", Name = "A", ProviderId = "test" }, new ModelDefinition { Id = "B", Name = "B", ProviderId = "test" }],
        Profiles = [new ConversationProfile { Id = "default", Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add("sample.model-chat", JsonSerializer.SerializeToElement(new Dictionary<string, object>
        { ["models"] = new[] { "A", "B" }, ["systemPrompt"] = "根据带来源的聊天资料回答。", ["stream"] = true })), Execution = new ModelExecutionOptions { RoundDelay = TimeSpan.Zero } }]
    };
}
