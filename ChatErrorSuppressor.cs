using System;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace PillionRider;

/// <summary>
/// 临时吞掉「共同骑乘」这条指令自己产生的红色错误提示。
///
/// <para><b>为什么需要它</b>：游戏执行 <c>/共同骑乘</c> 失败时会在聊天框打一条红字，
/// 比如「"&lt;1&gt;"出现问题：1号指定的目标名不正确。」。这条红字是游戏自己产生的，
/// 插件没有任何 API 能阻止它生成。</para>
///
/// <para><b>为什么不用宏里的 <c>/merror off</c></b>：查 <c>TextCommand</c> 表第 215 行
/// （<c>/macroerror</c>，别名 <c>/merror</c>）的官方说明写得很清楚 ——
/// 「Display or hide text command errors <b>within a user macro</b>.」
/// 也就是它<b>只管宏里</b>的错误。插件是把指令当普通聊天文本发出去的
/// （走 <c>UIModule.ProcessChatBoxEntry</c>，见 <see cref="NativeChatSender"/>），
/// 不在宏上下文里，这条开关对它无效。
/// 而且国服指令名是本地化的，硬发 <c>/merror</c> 反而可能变成另一条「指令不存在」的红字。</para>
///
/// <para><b>这里的做法</b>：订阅 <c>IChatGui.CheckMessageHandled</c>，在「刚发完指令」
/// 的一小段时间窗口内，只拦截文本里带「出现问题」「指定的目标名不正确」这类特征的聊天消息，
/// 调 <c>IHandleableChatMessage.PreventOriginal()</c> 让它不出现在聊天框里。其它消息一律放行。</para>
///
/// <para><b>签名为什么是这样</b>（国服 Dalamud 15.0.3.8，用 System.Reflection.Metadata
/// 解 <c>%APPDATA%\XIVLauncherCN\addon\Hooks\dev\Dalamud.dll</c> 得到，不是猜的）：</para>
/// <list type="bullet">
///   <item><c>IChatGui.CheckMessageHandled</c> → 委托 <c>OnHandleableChatMessageDelegate</c>，
///   <c>Invoke(Void)</c> 只有一个参数：<c>Dalamud.Game.Chat.IHandleableChatMessage</c>。</item>
///   <item><b>没有 <c>ref bool isHandled</c> 参数了</b> —— 旧版 Dalamud 是
///   <c>(ref SeString, ref bool)</c>，15 改成了「传一个消息对象进来，想拦就调它的方法」。
///   所以老代码里那套「找 ref bool 参数再写 IL 置 true」完全失效，
///   现场 <c>/pr scan</c> 打的就是「委托签名里没有 ref bool 参数（isHandled 换类型了），装不了」。</item>
///   <item><c>IHandleableChatMessage</c> 实现 <c>IMutableChatMessage</c> + <c>IChatMessage</c>，
///   自己只有一个方法：<c>void PreventOriginal()</c>。</item>
///   <item><c>IChatMessage.Message</c> 是 <c>SeString</c>（只读属性），
///   <c>SeString.ToString()</c> 返回 <c>TextValue</c>，也就是纯文本。</item>
/// </list>
///
/// <para><b>为什么不再用 DynamicMethod / 反射拼 IL</b>：既然签名固定且公开，
/// 直接写一个普通方法挂上去就行。手写 IL 只会在签名变化时静默失效，
/// 还多一层调试成本。</para>
/// </summary>
internal static class ChatErrorSuppressor
{
    /// <summary>指令发出后这段时间内产生的错误提示会被吞掉（毫秒）。</summary>
    private const int DefaultWindowMs = 2000;

    private static bool installed;

    /// <summary>订阅时用的那个 IChatGui 实例，卸载时要从同一个对象上摘掉。</summary>
    private static IChatGui subscribedGui;

    /// <summary>吞消息的截止时刻（Environment.TickCount64）。0 = 不在窗口内。</summary>
    private static long suppressUntil;

    /// <summary>已吞掉的条数，纯统计，方便排查时看它到底有没有生效。</summary>
    private static int suppressedCount;

    public static int SuppressedCount => suppressedCount;

    public static bool IsInstalled => installed;

    /// <summary>
    /// 最近一次安装尝试的结果，给人看的。装不上时这句就是唯一线索
    /// （<c>/pr scan</c> 和 <c>/pr chatevents</c> 都会打出来）。
    /// </summary>
    public static string StatusNote { get; private set; } = "尚未安装（插件刚载入时还没试过）";

    /// <summary>当前是否处于「吞错误」窗口内。</summary>
    public static bool IsArmed => suppressUntil != 0 && Environment.TickCount64 <= suppressUntil;

    /// <summary>
    /// 订阅聊天框拦截。失败不抛异常 —— 这只是让聊天框干净一点的锦上添花，
    /// 装不上就当没这功能，其它一切照常。
    /// </summary>
    public static bool Install(IChatGui chatGui, IPluginLog log)
    {
        if (installed)
        {
            return true;
        }

        if (chatGui == null)
        {
            StatusNote = "IChatGui 是空的，装不了";
            return false;
        }

        try
        {
            chatGui.CheckMessageHandled += OnCheckMessageHandled;

            installed = true;
            subscribedGui = chatGui;

            StatusNote = $"已启用（IChatGui.CheckMessageHandled + PreventOriginal，指令发出后 {DefaultWindowMs} 毫秒内生效）";
            log?.Information("[PillionRider] 已启用「自动隐藏共同骑乘错误提示」（指令发出后 {Window} 毫秒内）", DefaultWindowMs);
            return true;
        }
        catch (Exception ex)
        {
            StatusNote = $"安装时抛异常：{ex.GetType().Name}: {ex.Message}";
            log?.Warning(ex, "[PillionRider] 安装错误提示拦截失败（不影响其它功能）");
            return false;
        }
    }

    /// <summary>
    /// 聊天消息进入聊天框之前的一道关卡。
    ///
    /// 参数类型必须是 <c>IHandleableChatMessage</c>（15 的新签名）——
    /// 写成 <c>(ref SeString, ref bool)</c> 编译期就会对不上，挂不上去。
    /// </summary>
    private static void OnCheckMessageHandled(IHandleableChatMessage message)
    {
        if (!IsArmed || message == null)
        {
            return;
        }

        string text;

        try
        {
            text = message.Message?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            // SeString 里塞了奇怪 payload 时 ToString 可能抛，拦不住就放行。
            return;
        }

        if (!LooksLikeCommandError(text))
        {
            return;
        }

        try
        {
            // 15 的官方拦截手段：阻止这条消息被原样写进聊天框。
            // 注意它拦的是「原始消息」，插件自己用 IChatGui.Print 打的东西不受影响。
            message.PreventOriginal();
            suppressedCount++;
        }
        catch (Exception)
        {
            // 拦不住也不能让异常冒到 Dalamud 的事件循环里。
        }
    }

    /// <summary>
    /// 只认「指令出错」这一类文本，别的一概不动。
    /// 判定词按国服客户端实际输出取的，见 <c>DEVNOTES.md</c> 里记的那几条红字原文。
    /// </summary>
    private static bool LooksLikeCommandError(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("出现问题", StringComparison.Ordinal) ||
               text.Contains("指定的目标名不正确", StringComparison.Ordinal) ||
               text.Contains("出现错误", StringComparison.Ordinal) ||
               text.Contains("指令无效", StringComparison.Ordinal) ||
               text.Contains("无效的指令", StringComparison.Ordinal);
    }

    /// <summary>
    /// 把运行时真正看到的聊天框类型、它实现的所有接口，以及每个接口和它自己身上
    /// 全部事件/属性名整理成一段可读文本（<c>/pr chatevents</c>）。
    ///
    /// 留着它是因为签名可能再变 —— 下次「未启用」时不用再猜，直接看这条输出。
    /// </summary>
    public static string DescribeChatGui(IChatGui chatGui)
    {
        if (chatGui == null)
        {
            return "IChatGui 是空的";
        }

        var sb = new System.Text.StringBuilder();
        var type = chatGui.GetType();

        sb.Append("实现类型: ").Append(type.FullName).Append('\n');
        sb.Append("它自己的事件: ").Append(JoinNames(type.GetEvents())).Append('\n');
        sb.Append("它自己的属性: ").Append(JoinNames(type.GetProperties())).Append('\n');
        sb.Append("接口 IChatGui 的事件: ").Append(JoinNames(typeof(IChatGui).GetEvents())).Append('\n');
        sb.Append("接口 IChatGui 的属性: ").Append(JoinNames(typeof(IChatGui).GetProperties())).Append('\n');
        sb.Append("CheckMessageHandled 的委托: ").Append(DescribeHandler()).Append('\n');

        foreach (var iface in type.GetInterfaces())
        {
            var events = iface.GetEvents();
            var props = iface.GetProperties();

            if (events.Length == 0 && props.Length == 0)
            {
                continue;
            }

            sb.Append("接口 ").Append(iface.FullName)
              .Append(" → 事件: ").Append(JoinNames(events))
              .Append(" | 属性: ").Append(JoinNames(props))
              .Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>把 <c>CheckMessageHandled</c> 的委托签名拼成一行（参数类型逐个列出）。</summary>
    private static string DescribeHandler()
    {
        try
        {
            var evt = typeof(IChatGui).GetEvent("CheckMessageHandled");

            if (evt == null)
            {
                return "(接口上没有这个事件)";
            }

            var handlerType = evt.EventHandlerType;
            var invoke = handlerType?.GetMethod("Invoke");

            if (invoke == null)
            {
                return $"{handlerType?.FullName ?? "?"}（Invoke 取不到）";
            }

            var parameters = invoke.GetParameters();
            var parts = new string[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                parts[i] = parameters[i].ParameterType.Name;
            }

            return $"{handlerType.Name}({string.Join(", ", parts)})";
        }
        catch (Exception ex)
        {
            return $"（读签名时异常：{ex.GetType().Name}）";
        }
    }

    /// <summary>把成员名排好序拼成一行；没有成员时返回「(无)」。</summary>
    private static string JoinNames(System.Reflection.MemberInfo[] members)
    {
        if (members == null || members.Length == 0)
        {
            return "(无)";
        }

        var names = new string[members.Length];

        for (var i = 0; i < members.Length; i++)
        {
            names[i] = members[i].Name;
        }

        Array.Sort(names, StringComparer.Ordinal);

        return string.Join(", ", names);
    }

    /// <summary>取消订阅。插件卸载时调用。</summary>
    public static void Uninstall()
    {
        if (!installed || subscribedGui == null)
        {
            installed = false;
            subscribedGui = null;
            return;
        }

        try
        {
            subscribedGui.CheckMessageHandled -= OnCheckMessageHandled;
        }
        catch (Exception)
        {
            // 卸载时游戏可能已经在收尾了，移除失败无所谓。
        }

        installed = false;
        subscribedGui = null;
        suppressUntil = 0;
    }

    /// <summary>
    /// 打开吞错误窗口。发指令<b>之前</b>调用 —— 错误提示是执行指令那一刻就产生的。
    /// </summary>
    public static void Arm(int windowMs = DefaultWindowMs)
    {
        suppressUntil = Environment.TickCount64 + Math.Max(200, windowMs);
    }

    /// <summary>关掉吞错误窗口。</summary>
    public static void Disarm()
    {
        suppressUntil = 0;
    }
}
