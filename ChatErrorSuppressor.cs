using System;
using System.Reflection;
using System.Reflection.Emit;
using Dalamud.Plugin.Services;

namespace PillionRider;

/// <summary>
/// 临时吞掉「共同乘乘」这条指令自己产生的红色错误提示。
///
/// <para><b>为什么需要它</b>：游戏执行 <c>/共同乘乘</c> 失败时会在聊天框打一条红字，
/// 比如「"&lt;1&gt;"出现问题：1号指定的目标名不正确。」。这条红字是游戏自己产生的，
/// 插件没有任何 API 能阻止它生成。</para>
///
/// <para><b>为什么不用宏里的 <c>/merror off</c></b>：查 <c>TextCommand</c> 表第 215 行
/// （<c>/macroerror</c>，别名 <c>/merror</c>）的官方说明写得很清楚 ——
/// 「Display or hide text command errors <b>within a user macro</b>.」
/// 也就是它<b>只管宏里</b>的错误。插件是把指令当普通聊天文本发出去的
/// （走 ECommons 的 <c>Chat.ExecuteCommand</c>），不在宏上下文里，这条开关对它无效。
/// 而且国服指令名是本地化的，硬发 <c>/merror</c> 反而可能变成另一条「指令不存在」的红字。</para>
///
/// <para><b>这里的做法</b>：订阅 Dalamud 的 <c>IChatGui.CheckMessageHandled</c> 事件，
/// 在「刚发完指令」的一小段时间窗口内，只拦截文本里带「出现问题」「指定的目标名不正确」
/// 这类特征的聊天消息，把 <c>isHandled</c> 置 true 让它不出现在聊天框里。其它消息一律放行。</para>
///
/// <para><b>为什么现场生成回调</b>：直接写 lambda 得先知道完整签名
/// （国服 dev 目录一个 .xml 都没有，签名查不到）。</para>
///
/// <para><b>为什么不用表达式树</b>：表达式树<b>不支持按引用（ref / out）参数</b> ——
/// <c>Expression.Lambda</c> 在参数里遇到 <c>ref bool</c> 会直接抛
/// 「By-ref parameters are not supported in expression trees」。而 Dalamud 这个事件的
/// <c>isHandled</c> 恰恰就是 <c>ref bool</c>。所以只能退回 <see cref="DynamicMethod"/>：
/// 按委托 <c>Invoke</c> 的参数列表现取现用，现场拼一条与委托签名等价的方法 ——
/// <c>if (ShouldSuppress(message)) handled = true;</c>。</para>
/// </summary>
internal static class ChatErrorSuppressor
{
    /// <summary>指令发出后这段时间内产生的错误提示会被吞掉（毫秒）。</summary>
    private const int DefaultWindowMs = 2000;

    private static bool installed;
    private static EventInfo chatEvent;
    private static Delegate handlerDelegate;

    /// <summary>事件真正的宿主对象（可能是内部的 ChatGui，不一定是 IChatGui 本身）。</summary>
    private static object chatGuiInstance;

    /// <summary>吞消息的截止时刻（Environment.TickCount64）。0 = 不在窗口内。</summary>
    private static long suppressUntil;

    /// <summary>已吞掉的条数，纯统计，方便排查时看它到底有没有生效。</summary>
    public static int SuppressedCount { get; private set; }

    public static bool IsInstalled => installed;

    /// <summary>
    /// 最近一次安装尝试的结果，给人看的。装不上时这句就是唯一线索
    /// （<c>/pr scan</c> 和 <c>/pr chatevents</c> 都会打出来）。
    /// 国服 dev 目录一个 .xml 文档都没有，签名只能现场问运行时 —— 所以原因要记细一点。
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

        try
        {
            // 先取接口上的事件（Dalamud 保证它就是插件能用的那个；
            // ChatGuiPluginScoped 若是显式实现，接口的 add 访问器照样能挂上去）。
            // 接口上没有再退回具体实现类型上的事件，最后把实现类实现的其它接口也扫一遍 ——
            // 有些版本里这个事件只挂在某个子接口上。
            var evt = typeof(IChatGui).GetEvent("CheckMessageHandled")
                      ?? chatGui.GetType().GetEvent("CheckMessageHandled")
                      ?? FindEventOnInterfaces(chatGui);

            if (evt == null)
            {
                StatusNote = $"没找到事件 CheckMessageHandled（实现类型 {chatGui.GetType().FullName}）。用 /pr chatevents 看这个版本到底有哪些事件";
                log.Warning("[PillionRider] 这个 Dalamud 版本没有 CheckMessageHandled 事件，无法自动隐藏错误提示");
                return false;
            }

            var handlerType = evt.EventHandlerType;

            if (handlerType == null)
            {
                StatusNote = "找到了 CheckMessageHandled，但它的委托类型取不到";
                log.Warning("[PillionRider] CheckMessageHandled 的委托类型取不到，无法自动隐藏错误提示");
                return false;
            }

            var invoke = handlerType.GetMethod("Invoke");

            if (invoke == null)
            {
                StatusNote = "CheckMessageHandled 的 Invoke 取不到";
                log.Warning("[PillionRider] CheckMessageHandled 的 Invoke 取不到，无法自动隐藏错误提示");
                return false;
            }

            var parameters = invoke.GetParameters();

            // 极少数委托会把「接收者」当成第一个参数（Invoke 的第 0 个参数就是
            // 声明类型本身）。那种委托不能用无接收者的方法直接顶替 ——
            // 顶上去运行时会 NullReference。认出来就直接放弃安装。
            if (parameters.Length > 0 && parameters[0].ParameterType == handlerType.DeclaringType)
            {
                StatusNote = "CheckMessageHandled 的委托第一个参数是接收者（不是普通事件），装不了";
                log.Warning("[PillionRider] CheckMessageHandled 的委托带接收者参数，无法自动隐藏错误提示");
                return false;
            }

            // 找「要置为 true 的那个 bool 参数」：必须是 ref bool。
            // 注意 IsByRef 是 Type 上的属性，ParameterInfo 自己没有这个成员 ——
            // ref bool 的 ParameterType 是 System.Boolean&，IsByRef 为 true，
            // GetElementType() 才是它指向的 bool。
            var handledIndex = -1;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType.IsByRef && parameters[i].ParameterType.GetElementType() == typeof(bool))
                {
                    handledIndex = i;
                    break;
                }
            }

            if (handledIndex < 0)
            {
                StatusNote = "委托签名里没有 ref bool 参数（isHandled 换类型了），装不了";
                log.Warning("[PillionRider] CheckMessageHandled 的 isHandled 不是 ref bool，无法自动隐藏错误提示");
                return false;
            }

            // 找「消息文本参数」：优先按参数名（message / text）找，找不到再退到
            // 「类型名里带 SeString 的最后一个」。
            // 委托里 sender 和 message 可能都是 SeString，只看类型名分不出来 ——
            // 所以先按名字挑，挑不到再按「从后往前找」（消息体在签名里总是排在 sender 后面）。
            var messageIndex = -1;

            for (var i = 0; i < parameters.Length; i++)
            {
                var name = parameters[i].Name ?? string.Empty;

                if (name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    messageIndex = i;
                    break;
                }
            }

            if (messageIndex < 0)
            {
                for (var i = parameters.Length - 1; i >= 0; i--)
                {
                    if (parameters[i].ParameterType.Name.IndexOf("SeString", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        messageIndex = i;
                        break;
                    }
                }
            }

            if (messageIndex < 0)
            {
                StatusNote = "委托签名里找不到消息文本参数（SeString），装不了";
                log.Warning("[PillionRider] CheckMessageHandled 的签名里找不到消息参数，无法自动隐藏错误提示");
                return false;
            }

            var handler = BuildHandler(handlerType, parameters, messageIndex, handledIndex);

            evt.AddEventHandler(chatGui, handler);

            installed = true;
            chatEvent = evt;
            handlerDelegate = handler;

            // 订阅目标就是 chatGui 本身：接口事件的 add 访问器在实现类上转发到内部 ChatGui，
            // 具体类型上的事件则直接就是它自己的。
            chatGuiInstance = chatGui;

            StatusNote = $"已启用（事件挂在 {evt.DeclaringType?.FullName ?? "?"}，指令发出后 {DefaultWindowMs} 毫秒内生效）";
            log.Information("[PillionRider] 已启用「自动隐藏共同乘骑错误提示」（指令发出后 {Window} 毫秒内）", DefaultWindowMs);
            return true;
        }
        catch (Exception ex)
        {
            StatusNote = $"安装时抛异常：{ex.GetType().Name}: {ex.Message}";
            log.Warning(ex, "[PillionRider] 安装错误提示拦截失败（不影响其它功能）");
            return false;
        }
    }

    /// <summary>
    /// 在实现类实现的其它接口里找 CheckMessageHandled 事件。
    /// 有些 Dalamud 版本里这个事件只挂在子接口上，直接在 IChatGui 上找不到。
    /// </summary>
    private static EventInfo FindEventOnInterfaces(IChatGui chatGui)
    {
        var type = chatGui.GetType();

        foreach (var iface in type.GetInterfaces())
        {
            var found = iface.GetEvent("CheckMessageHandled");

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 把运行时真正看到的聊天框类型、它实现的所有接口，以及每个接口和它自己身上
    /// 全部事件/属性名整理成一段可读文本（<c>/pr chatevents</c>）。
    ///
    /// 为什么要这个：国服 dev 目录一个 .xml 文档都没有，事件名和签名根本查不到，
    /// 装不上拦截时只能现场问运行时 —— 这条命令就是那条问路。
    /// </summary>
    public static string DescribeChatGui(IChatGui chatGui)
    {
        var sb = new System.Text.StringBuilder();
        var type = chatGui.GetType();

        sb.Append("实现类型: ").Append(type.FullName).Append('\n');
        sb.Append("它自己的事件: ").Append(JoinNames(type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))).Append('\n');
        sb.Append("它自己的属性: ").Append(JoinNames(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))).Append('\n');
        sb.Append("接口 IChatGui 的事件: ").Append(JoinNames(typeof(IChatGui).GetEvents())).Append('\n');
        sb.Append("接口 IChatGui 的属性: ").Append(JoinNames(typeof(IChatGui).GetProperties())).Append('\n');

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

    /// <summary>把成员名排好序拼成一行；没有成员时返回「(无)」。</summary>
    private static string JoinNames(MemberInfo[] members)
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

        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(names[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 现场拼一条与委托签名等价的方法：
    /// <c>if (ShouldSuppress(message)) handled = true;</c>
    ///
    /// 用 <see cref="DynamicMethod"/> 而不是表达式树，原因是表达式树不支持
    /// 按引用参数（<c>ref bool</c>），而这个委托的 isHandled 就是 ref bool。
    /// </summary>
    private static Delegate BuildHandler(
        Type handlerType,
        ParameterInfo[] parameters,
        int messageIndex,
        int handledIndex)
    {
        // 动态方法的参数类型照抄委托 Invoke 的参数类型（含 bool&），
        // 这样最后 CreateDelegate 才能对得上。
        var parameterTypes = new Type[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            parameterTypes[i] = parameters[i].ParameterType;
        }

        var method = new DynamicMethod(
            "PillionRider_SuppressChatError",
            typeof(void),
            parameterTypes,
            typeof(ChatErrorSuppressor).Module,
            skipVisibility: true);

        var il = method.GetILGenerator();

        // 1) 把消息参数压栈（按位置，不是按名字 —— 位置从参数表里查出来才准）。
        LoadArg(il, messageIndex);

        var messageType = parameters[messageIndex].ParameterType;

        if (messageType.IsValueType)
        {
            // 装箱成 object，匹配 ShouldSuppress(object) 的签名。
            il.Emit(OpCodes.Box, messageType);
        }

        var suppressMethod = typeof(ChatErrorSuppressor).GetMethod(
            nameof(ShouldSuppress),
            BindingFlags.Public | BindingFlags.Static);

        if (suppressMethod == null)
        {
            throw new InvalidOperationException("ShouldSuppress 方法找不到了");
        }

        il.Emit(OpCodes.Call, suppressMethod);

        // 2) 返回 false 就直接结束，什么都不做。
        var done = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, done);

        // 3) true 的话把 isHandled 置成 true。
        //    注意：isHandled 是按引用参数，栈上装的是它的托管指针，
        //    所以是「指针 → 存一个字节」，不是「取地址 → 存一个字节」。
        LoadArg(il, handledIndex);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stind_I1);

        il.MarkLabel(done);
        il.Emit(OpCodes.Ret);

        return method.CreateDelegate(handlerType);
    }

    /// <summary>
    /// 压入第 <paramref name="index"/> 个参数。
    /// 用 Ldarg_0..3 处理前四个，剩下的用 Ldarg_S（短形式，支持 0-255）——
    /// 别用 OpCodes.Ldarg（长形式）配 int 操作数，那条路的 int 重载只接受 0-3，
    /// 超过就直接 ArgumentOutOfRangeException。
    /// </summary>
    private static void LoadArg(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0:
                il.Emit(OpCodes.Ldarg_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldarg_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldarg_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldarg_3);
                break;
            default:
                // 注意：Ldarg_S 的操作数是 1 字节，必须走 Emit(OpCode, byte) 这条重载。
                // 传 int 会命中 Emit(OpCode, int32)，那是给 switch 跳转表用的，
                // 会给单字节操作码塞 4 字节操作数，生成非法 IL。
                il.Emit(OpCodes.Ldarg_S, (byte)index);
                break;
        }
    }

    /// <summary>取消订阅。插件卸载时调用。</summary>
    public static void Uninstall()
    {
        if (!installed || chatEvent == null || handlerDelegate == null || chatGuiInstance == null)
        {
            installed = false;
            return;
        }

        try
        {
            chatEvent.RemoveEventHandler(chatGuiInstance, handlerDelegate);
        }
        catch (Exception)
        {
            // 卸载时游戏可能已经在收尾了，移除失败无所谓。
        }

        installed = false;
        chatEvent = null;
        handlerDelegate = null;
        chatGuiInstance = null;
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

    /// <summary>
    /// 判断这条聊天消息要不要吞掉。由上面生成出来的回调调用。
    /// </summary>
    public static bool ShouldSuppress(object message)
    {
        if (!IsArmed || message == null)
        {
            return false;
        }

        string text;

        try
        {
            text = message.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return false;
        }

        if (text.Length == 0)
        {
            return false;
        }

        // 只认「指令出错」这一类文本，别的一概不动。
        var isCommandError =
            text.Contains("出现问题", StringComparison.Ordinal) ||
            text.Contains("指定的目标名不正确", StringComparison.Ordinal) ||
            text.Contains("出现错误", StringComparison.Ordinal) ||
            text.Contains("指令无效", StringComparison.Ordinal) ||
            text.Contains("无效的指令", StringComparison.Ordinal);

        if (!isCommandError)
        {
            return false;
        }

        SuppressedCount++;
        return true;
    }
}
