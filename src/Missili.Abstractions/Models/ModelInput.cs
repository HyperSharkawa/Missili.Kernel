using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Missili.Abstractions;

/// <summary>系统指令与有序资料、真实轮次组成一次输入，聊天资料不会覆盖工具历史。</summary>
public sealed record ModelInput
{
    public string Instructions { get; init; } = "";
    public required ImmutableArray<ModelInputItem> Items { get; init; }

    /// <summary>为转述和独立请求构造纯轮次输入，不附加会话提示词。</summary>
    public static ModelInput FromTurns(ImmutableArray<ModelMessage> turns, string instructions = "") => new()
    {
        Instructions = instructions,
        Items = turns.Select(message => (ModelInputItem)new ModelTurnInput { Message = message }).ToImmutableArray()
    };

    /// <summary>显式选择聊天快照与本用途提示词，后续工具轮次可在其后继续追加。</summary>
    public static ModelInput FromConversation(ConversationContextSnapshot conversation, string instructions) => new()
    {
        Instructions = instructions,
        Items = [new ConversationSliceInput { Conversation = conversation }]
    };

    /// <summary>取得跨调用内容所有权，所有共享 JSON 和历史段均复制到当前快照。</summary>
    public ModelInput Freeze()
    {
        if (Instructions is null || Items.IsDefault) throw new ModelInputException("模型输入未初始化。");
        return this with
        {
            Items = Items.Select(item => item switch
            {
                ModelTurnInput turn => (ModelInputItem)(turn with { Message = turn.Message.Freeze() }),
                ConversationSliceInput slice => slice with
                {
                    Conversation = slice.Conversation with
                    {
                        Window = slice.Conversation.Window with
                        {
                            Messages = slice.Conversation.Window.Messages.Select(message => message with
                            {
                                Segments = message.Segments.Select(segment => segment.Freeze()).ToImmutableArray()
                            }).ToImmutableArray()
                        }
                    }
                },
                _ => throw new ModelInputException("模型输入项类型无效。")
            }).ToImmutableArray()
        };
    }
}

/// <summary>仅允许宿主声明的两种输入项，稳定 JSON 标识不携带程序集信息。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ModelTurnInput), "turn")]
[JsonDerivedType(typeof(ConversationSliceInput), "conversation")]
public abstract record ModelInputItem;

/// <summary>真实用户、模型或工具轮次，在输入序列中的位置不可被聊天投影改变。</summary>
public sealed record ModelTurnInput : ModelInputItem
{
    public required ModelMessage Message { get; init; }
}

/// <summary>已选定身份与记录集合的聊天资料，候选只决定媒体表示。</summary>
public sealed record ConversationSliceInput : ModelInputItem
{
    public required ConversationContextSnapshot Conversation { get; init; }
}
