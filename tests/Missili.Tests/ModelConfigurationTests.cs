using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missili.Abstractions;
using Missili.Models;
using Xunit;

namespace Missili.Tests;

/// <summary>验证独立配置档、首匹配路由以及发布失败时的快照边界。</summary>
public sealed class ModelConfigurationTests
{
    /// <summary>共享配置不共享身份，旧快照不受后续发布或无效修改影响。</summary>
    [Fact]
    public async Task RoutesUseIndependentProfilesAndImmutableRevisions()
    {
        await using ModelConfigurationService service = new(Path.GetTempPath(), 8, NullLogger.Instance, TimeProvider.System);
        ModelConfiguration configuration = Configuration(new Uri("http://127.0.0.1/"));
        ConversationProfile defaultProfile = configuration.Profiles[0] with { MaximumMessages = 70 };
        ConversationProfile groupProfile = new() { Id = "group", ReplyModels = ["A"] };
        service.Publish(configuration with
        {
            Profiles = [defaultProfile, groupProfile],
            Routes =
            [new ConversationRoute { ConversationId = "room*", ProfileId = "group" }, new ConversationRoute { ProfileId = "default" }]
        });
        ConversationProfileSnapshot old = service.Resolve(TestStore.Address());
        Assert.Equal("group", old.Profile.Id);
        Assert.Equal(100, old.Profile.MaximumMessages);
        Assert.Equal(70, service.Resolve(TestStore.Address("private")).Profile.MaximumMessages);
        service.Publish(configuration with { Profiles = [defaultProfile with { SystemPrompt = "更新后" }] });
        ConversationProfileSnapshot updated = service.Resolve(TestStore.Address());
        Assert.True(updated.Revision > old.Revision);
        Assert.Equal("group", old.Profile.Id);
        Assert.Throws<ArgumentException>(() => service.Publish(configuration with { DefaultProfileId = "missing" }));
        Assert.Equal(updated.Revision, service.Resolve(TestStore.Address()).Revision);
    }

    /// <summary>非法窗口和不存在的模型不能作为有效配置发布。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(100, 0)]
    public async Task InvalidWindowRejected(int maximum, int discard)
    {
        await using ModelConfigurationService service = new(Path.GetTempPath(), 8, NullLogger.Instance, TimeProvider.System);
        ModelConfiguration config = Configuration(new Uri("http://127.0.0.1/"));
        Assert.Throws<ArgumentException>(() => service.Publish(config with { Profiles = [config.Profiles[0] with { MaximumMessages = maximum, DiscardMessages = discard }] }));
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
            await File.WriteAllTextAsync(profile, """{"id":"default","replyModels":["A"]}""", token);
            await using ModelConfigurationService service = new(root, 8, NullLogger.Instance, TimeProvider.System);
            await service.ReloadAsync(TestContext.Current.CancellationToken);
            long revision = service.Resolve(TestStore.Address()).Revision;
            await File.WriteAllTextAsync(profile, """{"id":"default","replyModels":["A"],"maximumMesages":2}""", token);
            await Assert.ThrowsAsync<JsonException>(() => service.ReloadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(revision, service.Resolve(TestStore.Address()).Revision);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>创建无真实凭据的模型目录，供协议协调和配置测试复用。</summary>
    internal static ModelConfiguration Configuration(Uri address, string variable = "MISSILI_SYNTHETIC_UNUSED") => new()
    {
        DefaultProfileId = "default",
        Providers = [new ModelProviderDefinition { Id = "test", Protocol = ModelProtocol.ChatCompletions, BaseUrl = address, ApiKeyEnvironmentVariable = variable }],
        Models = [new ModelDefinition { Id = "A", Name = "A", ProviderId = "test" }, new ModelDefinition { Id = "B", Name = "B", ProviderId = "test" }],
        Profiles = [new ConversationProfile { Id = "default", ReplyModels = ["A", "B"], Execution = new ModelExecutionOptions { RoundDelay = TimeSpan.Zero } }]
    };
}
