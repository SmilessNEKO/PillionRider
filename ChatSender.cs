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
/// <b>为什么要借 ECommons</b>：模拟聊天框输入要拿到游戏的
/// <c>UIModule.ProcessChatBoxEntry</c>，那是个没有导出符号的内部函数，
/// 必须靠特征码扫描定位 —— 属于 FFXIVClientStructs 那层的手艺。
/// ECommons 里的 <c>ECommons.Automation.Chat.ExecuteCommand(string)</c>
/// 干的正是这件事，文档原文是
/// 「Executes command as if it was typed in chat box.」。
/// ECommons 是社区事实标准库，机器上装了 AutoDuty / Questionable / Lifestream /
/// SomethingNeedDoing / Artisan / BOCCHI 这类插件就一定有它（实测本机 19 份）。
/// </para>
///
/// <para>
/// <b>为什么用反射而不是直接引用 ECommons.dll</b>：ECommons 是跟着各个插件
/// 各自带一份的第三方库，不同插件带的版本不一样（本机同时存在 19 份，
/// 版本还各不相同）。编译期硬引用哪一份都是赌博：用户在游戏里实际加载的可能是
/// 另一份、版本不同、甚至签名改了。反射调用只要「类型和这个签名的方法在」就能用，
/// 找不到就退化到 ProcessCommand 并明确告诉用户缺什么，不会连累整个插件编译/加载。
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

    /// <summary>ECommons 通道是否已经就绪。</summary>
    public static bool IsAvailable
    {
        get
        {
            Probe(null);
            return executeCommandMethod != null;
        }
    }

    /// <summary>
    /// 真正把一行指令送进游戏聊天框。<paramref name="command"/> 必须以 <c>/</c> 开头。
    /// 成功返回 true；失败时 <paramref name="error"/> 说明原因。
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

        // ECommons 的 ExecuteCommand 对没带斜杠的文本会抛 ArgumentException，这里先兜住。
        if (!command.StartsWith("/", StringComparison.Ordinal))
        {
            command = "/" + command;
        }

        Probe(log);

        if (executeCommandMethod == null)
        {
            error = "没有可用的聊天框通道（ECommons 未加载）：游戏原生指令没法送进游戏";
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
            StatusNote = $"ECommons 通道就绪（{assemblyName}!{chatType.FullName}.{MethodName}{(isStatic ? "" : "，实例方法")}）";
            log?.Information("[PillionRider] {Note}", StatusNote);
            return;
        }

        StatusNote = foundChatTypes.Count > 0
            ? $"找到 ECommons 但接口不匹配：{string.Join(" / ", foundChatTypes)}"
            : "没找到 ECommons（需要装一个带 ECommons 的插件，比如 AutoDuty / Questionable / Lifestream / Artisan）";
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
