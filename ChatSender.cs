using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace PillionRider;

/// <summary>
/// 把「游戏自带的文本指令」真正送进游戏聊天框。
///
/// <para>
/// <b>为什么不能直接用 <c>ICommandManager.ProcessCommand</c></b>：
/// 那个方法只是 Dalamud 自己的指令分发器 —— 它查的是「插件注册过的指令表」，
/// 命中就调对应插件的回调。<c>/pr</c> 这类插件指令走它没问题，但
/// <c>/共同骑乘</c> 是游戏原生指令，Dalamud 的指令表里根本没有这一项，
/// 所以 ProcessCommand 只会返回 false，游戏那边什么都没收到。
/// 实测就是这样：聊天框一路打印「已发送游戏指令 /共同骑乘 &lt;2&gt;」，
/// 而角色纹丝不动。真正需要的是「模拟玩家在聊天框里敲了一行」。
/// </para>
///
/// <para>
/// <b>主路径：直接调游戏的 <c>UIModule.ProcessChatBoxEntry</c></b>。
/// 那是「把一行文字当成玩家在聊天框敲的内容处理」的内部函数，没有导出符号，
/// 靠 FFXIVClientStructs 的特征码扫描定位。国服 Dalamud 15 自带的
/// <c>FFXIVClientStructs.dll</c> 已经解出签名：
/// <code>void ProcessChatBoxEntry(Utf8String* text, IntPtr a2, bool a3);</code>
/// 这条路零第三方依赖，详见 <see cref="NativeChatSender"/>。
/// </para>
///
/// <para>
/// <b>备用路径：借 ECommons</b>。万一某个 Dalamud 版本的 FFXIVClientStructs
/// 没有这个函数（或签名变了），就退回反射调用
/// <c>ECommons.Automation.Chat.ExecuteCommand(string)</c> —— 它内部干的是同一件事。
/// ECommons 是社区事实标准库，机器上装了 AutoDuty / Questionable / Lifestream /
/// SomethingNeedDoing / Artisan / BOCCHI 这类插件就一定有它（实测本机 19 份，
/// 版本从 2.0.0.39 到 3.2.1.26 各不相同，所以只能反射不能硬引用）。
/// </para>
///
/// <para>
/// <b>为什么备用路径用反射而不是直接引用 ECommons.dll</b>：ECommons 是跟着各个插件
/// 各自带一份的第三方库，版本各不相同。编译期硬引用哪一份都是赌博：用户在游戏里
/// 实际加载的可能是另一份、版本不同、甚至签名改了。反射调用只要「类型和这个签名的
/// 方法在」就能用，找不到就明确告诉用户缺什么，不会连累整个插件编译/加载。
/// </para>
/// </summary>
internal static class ChatSender
{
    private const string ECommonsTypeName = "ECommons.Automation.Chat";

    private const string MethodName = "ExecuteCommand";

    /// <summary>探测失败后隔多久再试（毫秒）。插件加载顺序不确定，ECommons 可能晚于我们加载。</summary>
    private const long RetryIntervalMs = 5000;

    private static bool probeDone;
    private static long nextProbeTick;
    private static MethodInfo executeCommandMethod;

    /// <summary>ECommons 里所有跟「Chat」有关的类型名，探测失败时用来给用户留证据。</summary>
    private static readonly List<string> foundChatTypes = new();

    /// <summary>给 /pr scan 与设置窗口显示的状态说明。</summary>
    public static string StatusNote { get; private set; } = "尚未探测";

    /// <summary>聊天框通道是否可用（原生优先，其次 ECommons）。</summary>
    public static bool IsAvailable
    {
        get
        {
#if NATIVE_ACTION
            if (NativeChatSender.IsAvailable)
            {
                return true;
            }
#endif
            Probe(null);
            return executeCommandMethod != null;
        }
    }

    /// <summary>当前实际走的是哪条路，给 /pr scan 显示。</summary>
    public static string ChannelName
    {
        get
        {
#if NATIVE_ACTION
            if (NativeChatSender.IsAvailable)
            {
                return "原生（ProcessChatBoxEntry）";
            }
#endif
            Probe(null);
            return executeCommandMethod != null ? "ECommons（反射）" : "无";
        }
    }

    /// <summary>
    /// 真正把一行指令送进游戏聊天框。<paramref name="command"/> 必须以 <c>/</c> 开头。
    /// 成功返回 true；失败时 <paramref name="error"/> 说明原因。
    ///
    /// <para>
    /// 优先走原生 <c>ProcessChatBoxEntry</c>，拿不到再退 ECommons。
    /// 输入校验在这里统一做 —— 以前是 ECommons 帮我们挡的，现在主路径自己发，
    /// 必须自己挡：空串、超长、换行/回车/NUL 都会让游戏把一行截断或直接忽略。
    /// </para>
    /// </summary>
    public static bool TrySend(string command, IPluginLog log, out string error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "指令是空的";
            return false;
        }

        command = command.Trim();

        // 没带斜杠的文本，游戏会当成聊天内容发出去而不是执行指令。
        if (!command.StartsWith("/", StringComparison.Ordinal))
        {
            command = "/" + command;
        }

        // 换行/回车/NUL 会把一行切成两半，后半段可能变成一条真的聊天消息发到频道里。
        if (command.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            error = "指令里含换行或 NUL，拒绝发送";
            return false;
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(command);

#if NATIVE_ACTION
        if (byteCount > NativeChatSender.MaxUtf8Bytes)
        {
            error = $"指令太长（{byteCount} 字节，上限 {NativeChatSender.MaxUtf8Bytes}）";
            return false;
        }

        if (NativeChatSender.IsAvailable)
        {
            if (NativeChatSender.TrySend(command, log, out var nativeError))
            {
                return true;
            }

            // 原生失败不直接判死：可能是这一版签名对不上，退到 ECommons 再试一次。
            log?.Warning("[PillionRider] 原生通道发送失败（{Reason}），改用 ECommons 重试", nativeError);
            error = nativeError;
        }
#else
        if (byteCount > 500)
        {
            error = $"指令太长（{byteCount} 字节，上限 500）";
            return false;
        }
#endif

        Probe(log);

        if (executeCommandMethod == null)
        {
            error ??= "没有可用的聊天框通道：原生 ProcessChatBoxEntry 不可用，机器上也没有 ECommons";
            return false;
        }

        try
        {
            var result = executeCommandMethod.Invoke(null, new object[] { command });

            // ExecuteCommand 的返回类型是 void，这里只是留个口子：万一某个版本改成返回 bool，
            // 也能顺带尊重一下它的结论，而不是一律当成功。
            if (result is bool ok && !ok)
            {
                error = "ECommons 的 Chat.ExecuteCommand 返回 false";
                return false;
            }

            if (log != null)
            {
                log.Information("[PillionRider] 已通过 ECommons 送入聊天框：{Command}", command);
            }

            return true;
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            error = $"{inner.GetType().Name}: {inner.Message}";
            log?.Warning("[PillionRider] 调 ECommons Chat.ExecuteCommand 失败：{Message}", inner.Message);
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            log?.Warning("[PillionRider] 调 ECommons Chat.ExecuteCommand 抛异常：{Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 探测 ECommons 的 <c>Chat.ExecuteCommand(string)</c> 在不在。
    /// 找到就缓存方法，找不到就记下原因并在 <see cref="RetryIntervalMs"/> 后再试。
    /// </summary>
    private static void Probe(IPluginLog log)
    {
        var now = Environment.TickCount64;

        if (executeCommandMethod != null)
        {
            return;
        }

        if (probeDone && now < nextProbeTick)
        {
            return;
        }

        probeDone = true;
        nextProbeTick = now + RetryIntervalMs;
        foundChatTypes.Clear();

        Type chatType = null;
        string assemblyName = null;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            var assembly = assemblies[i];
            if (assembly == null || assembly.IsDynamic)
            {
                continue;
            }

            string name;
            try
            {
                name = assembly.GetName().Name;
            }
            catch
            {
                continue;
            }

            if (name == null || name.IndexOf("ECommons", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            // 一个进程里可能同时加载了多份 ECommons（不同插件各有自己的 AssemblyLoadContext），
            // 所以不能找到第一个就 break —— 得找到那个真的有 Chat.ExecuteCommand 的。
            Type candidate;
            try
            {
                candidate = assembly.GetType(ECommonsTypeName, false);
            }
            catch
            {
                continue;
            }

            if (candidate == null)
            {
                CollectChatTypes(assembly);
                continue;
            }

            var method = FindExecuteCommand(candidate);
            if (method == null)
            {
                foundChatTypes.Add($"{name}!{candidate.FullName}（没有 {MethodName}(string)）");
                continue;
            }

            chatType = candidate;
            assemblyName = name;
            executeCommandMethod = method;
            break;
        }

        if (executeCommandMethod != null)
        {
            var isStatic = executeCommandMethod.IsStatic;
            StatusNote = $"ECommons 备用通道就绪（{assemblyName}!{chatType.FullName}.{MethodName}{(isStatic ? "" : "，实例方法")}）";
            log?.Information("[PillionRider] {Note}", StatusNote);
            return;
        }

        StatusNote = foundChatTypes.Count > 0
            ? $"找到 ECommons 但接口不匹配：{string.Join(" / ", foundChatTypes)}"
            : "没找到 ECommons（备用通道也不可用）";
    }

    /// <summary>
    /// 刷新 <see cref="StatusNote"/>，让它反映当前实际会走哪条路。
    /// 插件启动时与 <c>/pr scan</c> 里都调一次。
    /// </summary>
    public static void RefreshStatus(IPluginLog log)
    {
#if NATIVE_ACTION
        if (NativeChatSender.IsAvailable)
        {
            StatusNote = NativeChatSender.StatusNote;
            log?.Information("[PillionRider] 聊天框通道：{Note}", StatusNote);
            return;
        }
#endif
        Probe(log);
#if NATIVE_ACTION
        if (executeCommandMethod != null)
        {
            StatusNote = "原生通道不可用，已退回 " + StatusNote;
        }
        else
        {
            StatusNote = NativeChatSender.StatusNote + "；" + StatusNote;
        }
#endif
    }

    private static void CollectChatTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch
        {
            return;
        }

        if (types == null)
        {
            return;
        }

        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];
            if (type == null || type.FullName == null)
            {
                continue;
            }

            if (type.FullName.IndexOf("Automation", StringComparison.OrdinalIgnoreCase) >= 0
                && type.Name.IndexOf("Chat", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foundChatTypes.Add(type.FullName);
            }
        }
    }

    private static MethodInfo FindExecuteCommand(Type chatType)
    {
        MethodInfo best = null;

        MethodInfo[] methods;
        try
        {
            methods = chatType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        }
        catch
        {
            return null;
        }

        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method.Name != MethodName)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
            {
                continue;
            }

            // 优先静态方法：ECommons 的 Chat 是静态工具类。
            if (method.IsStatic)
            {
                return method;
            }

            best ??= method;
        }

        return best;
    }

    /// <summary>把探测状态重置，下次用到时重新找。用于插件重载或 ECommons 后加载的情况。</summary>
    public static void Reset()
    {
        probeDone = false;
        nextProbeTick = 0;
        executeCommandMethod = null;
        StatusNote = "尚未探测";
        foundChatTypes.Clear();
    }
}
