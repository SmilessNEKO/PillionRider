#if NATIVE_ACTION
using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PillionRider;

/// <summary>
/// 零依赖的聊天框通道：直接调游戏自己的 <c>UIModule.ProcessChatBoxEntry</c>。
///
/// <para>
/// <b>这是怎么找到的</b>：<c>ProcessChatBoxEntry</c> 是游戏里「把一行文字当成玩家在聊天框
/// 敲下的内容处理」的内部函数，没有导出符号，只能靠 FFXIVClientStructs 的特征码扫描定位。
/// 国服 Dalamud 15 自带的 <c>FFXIVClientStructs.dll</c> 已经把它的签名解出来了：
/// <code>
/// UIModule* UIModule.Instance();
/// void ProcessChatBoxEntry(Utf8String* text, IntPtr a2, bool a3);
/// </code>
/// 第二个参数传 <see cref="IntPtr.Zero"/>、第三个传 <c>false</c>，
/// 就是社区事实标准（ECommons 的 <c>Chat.ExecuteCommand</c> 内部也是这么调的）。
/// </para>
///
/// <para>
/// <b>为什么这条路比借 ECommons 好</b>：ECommons 是第三方库，机器上装了 19 份不同版本
/// （实测 2.0.0.39 到 3.2.1.26），反射调用要赌签名不变；而 <c>FFXIVClientStructs</c>
/// 是 Dalamud 自己发的，跟 Dalamud 版本严格绑定，插件编译时引的就是它。
/// 走这条路既不需要机器上装任何别的插件，也不需要往 zip 里塞第三方 dll。
/// </para>
///
/// <para>
/// <b>自己必须挡的东西</b>（ECommons 帮我们挡过，现在得自己来）：
/// 空串、超过 500 字节、含换行/回车/NUL（会让游戏把一行截成两半）。
/// 这些挡在 <see cref="ChatSender.TrySend"/> 里统一做，不重复。
/// </para>
/// </summary>
internal static class NativeChatSender
{
    /// <summary>游戏聊天框单行的上限（字节，UTF-8）。超过会被游戏截断或直接忽略。</summary>
    public const int MaxUtf8Bytes = 500;

    /// <summary>这条通道是否可用（<c>UIModule.Instance()</c> 能拿到非空指针）。</summary>
    public static unsafe bool IsAvailable
    {
        get
        {
            try
            {
                return UIModule.Instance() != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 把一行指令当成玩家在聊天框敲的内容送进游戏。
    /// <paramref name="command"/> 必须以 <c>/</c> 开头（调用方保证）。
    /// </summary>
    public static unsafe bool TrySend(string command, IPluginLog log, out string error)
    {
        error = null;

        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
            {
                error = "UIModule 实例为空（还没进游戏？）";
                return false;
            }

            // Utf8String 会在非托管堆上分配，必须释放 —— 它是 IDisposable，using 走 Dtor。
            using var text = new Utf8String(command);

            // a2 = IntPtr.Zero（没有附加数据），a3 = false（不要走「静默」分支）
            ui->ProcessChatBoxEntry(&text, IntPtr.Zero, false);

            log?.Information("[PillionRider] 已通过 ProcessChatBoxEntry 送入聊天框：{Command}", command);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            log?.Warning("[PillionRider] 调 ProcessChatBoxEntry 失败：{Message}", ex.Message);
            return false;
        }
    }

    /// <summary>给 /pr scan 显示的状态说明。</summary>
    public static string StatusNote => IsAvailable
        ? "原生通道就绪（UIModule.ProcessChatBoxEntry，零第三方依赖）"
        : "原生通道不可用（UIModule.Instance() 为 null，可能还没进游戏）";
}
#endif
