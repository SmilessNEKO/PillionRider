using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using System.Text;

namespace PillionRider;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("PillionRider###PillionRiderConfig")
    {
        this.plugin = plugin;
        this.Size = new Vector2(560, 480);

        // ImGuiCond 属于 Dalamud.Bindings.ImGui。只在首次使用时定尺寸，之后允许用户自己拖动。
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>
    /// API 13 起 ImGui 绑定的文本参数是 UTF-8 视图（<c>ImU8String</c>），官方示例一律写作
    /// <c>ImGui.Text("Example"u8)</c>。C# 的 <c>u8</c> 字面量只能写 ASCII，中文写不进去，
    /// 所以统一在这里把字符串编码成 UTF-8 字节再传。
    /// 返回 <c>byte[]</c> 而不是 <c>ReadOnlySpan&lt;byte&gt;</c>：<c>byte[]</c> 既能隐式转
    /// <c>ReadOnlySpan&lt;byte&gt;</c>（再走 user-defined 到 <c>ImU8String</c>），也能直接匹配
    /// <c>byte[]</c> 形参，两种情况都编得过。
    /// </summary>
    private static byte[] U8(string text) => Encoding.UTF8.GetBytes(text);

    public override void Draw()
    {
        var config = Plugin.Config;

        ImGui.Text(U8("PillionRider — 脱战后自动骑上队友的多人坐骑"));
        ImGui.Text(U8($"状态: {(config.Enabled ? "已启用" : "已停用")}（右上角状态栏点击也能切换）"));

        // ---- 执行方式：游戏指令（首选） ----
        // 游戏自己就有 /共同骑乘 这个文本指令，玩家手写宏用的就是它。
        // 走这条路不用猜 GeneralAction ID —— 那张表里根本没有「共同骑乘」这一行，
        // 猜错的后果是打开分解窗口（GeneralAction 5 = Desynthesis）。
        var useGameCommand = config.UseGameCommand;
        if (ImGui.Checkbox(U8("游戏指令（发 /共同骑乘，推荐）"), ref useGameCommand))
        {
            config.UseGameCommand = useGameCommand;

            // 打开它就把猜 ID 的那条路关掉，免得失败时又去点分解。
            // 关掉时不自动回落别的途径 —— 另一条是实验性的，
            // 替用户做主反而会留下一个「看起来在工作其实不行」的配置。
            if (config.UseGameCommand)
            {
                config.UseNativeAction = false;
            }

            config.Save();
        }

        if (config.UseGameCommand)
        {
            ImGui.Text(U8($"模板: {config.GameCommandFormat}"));
            ImGui.Text(U8("  {P} = 目标写法（默认展开成 <t>，就是「当前选中的目标」）"));
            ImGui.Text(U8("  {0} = 队友名，{1} = 小队序号（队伍列表位置，从 1 起算）"));
            ImGui.Text(U8("  国际服写 /ridepillion {P}"));
            ImGui.Text(U8("  模板不用改：<t> 是游戏官方支持的写法，切目标成功就必然指向对的人"));
            ImGui.Text(U8("  切不到目标时才退回序号 <N>，两条路都不通就这一轮不动"));
            ImGui.Text(U8($"聊天框通道: {this.plugin.ChatChannelStatus}"));

            // 这条不是吓唬人：ProcessCommand 发不出游戏原生指令，实测就是这么失败的。
            // 没有 ECommons 就没有「模拟打字」的能力，游戏那边收不到任何东西。
            if (!this.plugin.ChatChannelAvailable)
            {
                ImGui.Text(U8("  没有 ECommons 时指令送不进游戏 —— 装一个带 ECommons 的插件即可"));
                ImGui.Text(U8("  （AutoDuty / Questionable / Lifestream / Artisan / SomethingNeedDoing 任选其一）"));
            }
        }

        // ---- 执行方式：独立模式（猜 GeneralAction ID，默认不再使用） ----
        var native = config.UseNativeAction;
        if (ImGui.Checkbox(U8("独立模式（自己调用游戏动作 UseAction，Experimental）"), ref native))
        {
            config.UseNativeAction = native;

            // 两条路线互斥：同时开的话，指令失败会再去点动作，
            // 而 GeneralAction 表里没有「共同骑乘」，只会点出别的东西（比如分解）。
            if (config.UseNativeAction)
            {
                config.UseGameCommand = false;
            }
            else if (!config.UseGameCommand)
            {
                config.UseGameCommand = true;
            }

            config.Save();
        }

        if (config.UseGameCommand)
        {
            ImGui.Text(U8("执行: 游戏指令（主力路线）"));
        }
        else if (config.UseNativeAction)
        {
            var gaId = RidePillionAction.ResolveGeneralActionId(config.GeneralActionId);
            var gaText = gaId == 0 ? "未指定（不会执行）" : gaId.ToString();
            ImGui.Text(U8($"执行: 游戏动作 GeneralAction={gaText}"));
            ImGui.Text(U8($"探测: {RidePillionAction.DiscoveryNote}"));

            // 不用 InputInt：API 15 的 ImGui 绑定里 InputInt 的签名没在本机 XML 里，
            // SliderInt 是已经在用的、已验证能编译的那个。
            var gaInput = (int)config.GeneralActionId;
            if (ImGui.SliderInt(U8("共同骑乘动作 ID（0 = 不执行）"), ref gaInput, 0, 255))
            {
                config.GeneralActionId = gaInput < 0 ? 0u : (uint)gaInput;
                config.Save();
            }
        }
        else
        {
            ImGui.Text(U8("当前配置不会执行：勾上「游戏指令」即可"));
        }

        ImGui.Separator();

        var enabled = config.Enabled;
        if (ImGui.Checkbox(U8("启用插件"), ref enabled))
        {
            config.Enabled = enabled;
            config.Save();

            // 状态栏那行的 on/off 跟这里是同一个开关，改完要立刻同步，否则两边显示不一致。
            this.plugin.RefreshDtrBar();
        }

        // 下面这些是插件的「安全前提」，不提供开关：
        //   脱战才触发 / 副本里不工作 / 跨服小队不工作 / 自己已在坐骑上不触发 /
        //   自己已在共同骑乘不触发 / 施法·交互·过场不触发 / 仅多座坐骑 / 只认队伍里的人。
        // 关掉其中任何一条都只会让它更容易做错事，所以直接写死在 Plugin.Tick 与 PassesFilters 里，
        // 设置窗口只做一句说明，不给任何修改入口。
        // 用 Text 而不是 TextDisabled：TextDisabled 的 ImU8String 重载在本机查不到签名，Text 是已验证能编的。
        ImGui.Text(U8("触发条件（固定，不可修改）："));
        ImGui.Text(U8("  有队伍才工作 / 脱战 / 不在副本 / 非同服小队 / 自己未骑乘 / 未在共同骑乘"));
        ImGui.Text(U8("  不施法交互过场 / 队友骑多座坐骑"));
        ImGui.Text(U8("  单人时插件完全休眠：不扫对象表、不读 Excel 表、不做任何判断"));
        ImGui.Text(U8("  跨服小队（跨服组队 / 跨服团队）同样完全休眠：队友实体不在本地对象表里"));
        ImGui.Text(U8("  刚下坐骑后静默 1.5 秒：那一瞬间状态还在收尾，判断最容易踩空"));
        ImGui.Text(U8("  目标写法优先用 <t>：先把当前目标切到该队友并读回来确认，切不到才退回小队序号 <N>"));
        ImGui.Text(U8("  小队序号 = 队伍列表位置（从 1 起算），1 号也可能是队友 —— 不再假设 1 号是自己"));
        ImGui.Text(U8("  既切不到目标、又算不出序号才不发：宁可这一轮不动，也不给游戏送红字"));
        ImGui.Text(U8("  指令发出后 2 秒内，聊天框里带「出现问题」的错误提示会被自动吞掉"));
        ImGui.Text(U8("  目标要等指令执行完才还原（约 0.7 秒）：发完立刻还原会让 <t> 解析失败，反而送红字"));

        var setTarget = config.SetTargetBeforeCommand;
        if (ImGui.Checkbox(U8("触发前把目标切到该队友"), ref setTarget))
        {
            config.SetTargetBeforeCommand = setTarget;
            config.Save();
        }

        if (config.UseNativeAction)
        {
            ImGui.Text(U8("(独立模式不需要切目标：动作自带 targetId)"));
        }
        else
        {
            ImGui.Text(U8("(建议保持勾选：勾上才能用 <t>，切目标成功就完全不依赖队伍编号)"));
        }

        var restore = config.RestoreTargetAfter;
        if (ImGui.Checkbox(U8("触发后还原原目标"), ref restore))
        {
            config.RestoreTargetAfter = restore;
            config.Save();
        }

        var oncePerSession = config.OncePerMountSession;
        if (ImGui.Checkbox(U8("同一队友每次上坐骑只触发一次（没坐上会自动重试）"), ref oncePerSession))
        {
            config.OncePerMountSession = oncePerSession;
            config.Save();
        }

        var mute = config.MuteAutoChat;
        if (ImGui.Checkbox(U8("不向聊天框发送自动提示（推荐勾上）"), ref mute))
        {
            config.MuteAutoChat = mute;
            config.Save();
        }

        ImGui.Text(U8("  勾上后插件不再自己往聊天框打任何东西（已发送/连续失败/读表失败…）"));
        ImGui.Text(U8("  你主动发的 /pr 命令照常回复；所有自动提示仍然写进 /xllog"));

        var notify = config.NotifyInChat;
        if (ImGui.Checkbox(U8("触发时在聊天框提示（上面那项没勾时才有效）"), ref notify))
        {
            config.NotifyInChat = notify;
            config.Save();
        }

        var debug = config.DebugLog;
        if (ImGui.Checkbox(U8("输出调试日志"), ref debug))
        {
            config.DebugLog = debug;
            config.Save();
        }

        ImGui.Separator();

        var maxDistance = (int)config.MaxDistance;
        if (ImGui.SliderInt(U8("最大距离 (米, 0=不限)"), ref maxDistance, 0, 100))
        {
            config.MaxDistance = maxDistance;
            config.Save();
        }

        var poll = config.PollIntervalMs;
        if (ImGui.SliderInt(U8("轮询间隔 (毫秒)"), ref poll, 100, 3000))
        {
            config.PollIntervalMs = poll;
            config.Save();
        }

        var cooldown = config.CooldownMs;
        if (ImGui.SliderInt(U8("触发冷却 (毫秒)"), ref cooldown, 1000, 60000))
        {
            config.CooldownMs = cooldown;
            config.Save();
        }

        var maxFail = config.MaxFailuresPerTarget;
        if (ImGui.SliderInt(U8("同一队友连续失败几次后暂停 (0=不限制)"), ref maxFail, 0, 10))
        {
            config.MaxFailuresPerTarget = maxFail;
            config.Save();
        }

        if (config.MaxFailuresPerTarget > 0)
        {
            var backoffSec = Math.Max(1, config.FailureBackoffMs / 1000);
            if (ImGui.SliderInt(U8("失败后暂停秒数"), ref backoffSec, 5, 600))
            {
                config.FailureBackoffMs = backoffSec * 1000;
                config.Save();
            }
        }

        ImGui.Separator();

        if (ImGui.Button(U8("立即扫描一次并输出到聊天框")))
        {
            this.plugin.DumpPartyScan();
        }

        if (ImGui.Button(U8("立即触发一次")))
        {
            this.plugin.ForceTriggerOnce();
        }

        ImGui.Separator();
        ImGui.Text(U8("常用指令:"));
        ImGui.Text(U8("/pr              开关本窗口"));
        ImGui.Text(U8("/pr on | off     启用 / 停用（等同点状态栏）"));
        ImGui.Text(U8("/pr toggle       切换启停（等同点状态栏）"));
        ImGui.Text(U8("/pr now          强制触发一次"));
        ImGui.Text(U8("/pr scan         输出队伍扫描结果"));
        ImGui.Text(U8("/pr kinds        输出对象类型统计（排查用）"));
        ImGui.Text(U8("/pr chat         检查聊天框通道（ECommons）是否可用"));
        ImGui.Text(U8("/pr chatevents   列出本版本聊天框接口的事件/属性（排查错误提示拦截用）"));
        ImGui.Text(U8("/pr quiet        切换「不向聊天框发送自动提示」"));
        ImGui.Text(U8("/pr mountinfo <id>  查某个坐骑 ID 的 ExtraSeats（胖莫古力是 84）"));
        ImGui.Text(U8("/pr gcmd         开关「游戏指令」路线（/共同骑乘）"));
        ImGui.Text(U8("/pr gcmdformat <模板>  改游戏指令模板（默认 /共同骑乘 {P}）"));
        ImGui.Text(U8("/pr native       开关独立模式（自己调 UseAction，需要 ID）"));
        ImGui.Text(U8("/pr ga <id>      设置 GeneralAction ID（独立模式）"));
        ImGui.Text(U8("/pr galist       把 GeneralAction 全表写进日志"));
        ImGui.Text(U8("/pr redisc       重新探测 GeneralAction 表"));
        ImGui.Text(U8("/pr mount <id>   手动把某坐骑标为多座"));
        ImGui.Text(U8("/pr clearmount   清空手动标记"));

        if (!string.IsNullOrEmpty(config.ExtraMountIds))
        {
            ImGui.Separator();
            ImGui.Text(U8($"手动标记的多座坐骑 ID: {config.ExtraMountIds}"));
        }

        var reason = this.plugin.MountDataFailureReason;
        if (!string.IsNullOrEmpty(reason))
        {
            ImGui.Separator();
            ImGui.Text(U8($"坐骑表不可用: {reason}"));
            ImGui.Text(U8("(此时按手动名单判定：用 /pr mount <坐骑ID> 标记你确认能带人的坐骑)"));
        }
    }
}
