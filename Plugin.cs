using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PillionRider;

public sealed class Plugin : IDalamudPlugin
{
    /// <summary>
    /// 聊天框标签与 /xlplugins 里显示的名字。
    /// 名字跟状态栏、指令统一成 PillionRider。
    /// </summary>
    public const string PluginName = "PillionRider";

    private const string CommandName = "/pr";

    /// <summary>
    /// 右上角状态栏（DTR bar）里这个条目的标题。
    /// 它同时是用户在 Dalamud 设置里给条目排序时看到的名字，所以不能带空格歧义。
    /// </summary>
    private const string DtrTitle = "PillionRider";

    public static Configuration Config { get; private set; } = new();

    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private readonly WindowSystem windowSystem = new("PillionRider");
    private readonly ConfigWindow configWindow;

    /// <summary>右上角状态栏条目。只 Get 一次并缓存 —— 同名的 Get 会抛 ArgumentException。</summary>
    private IDtrBarEntry dtrEntry;

    /// <summary>已经触发过的 (骑手 GameObjectId, 坐骑 DataId)，用于「同一趟骑乘只触发一次」。</summary>
    private readonly HashSet<(ulong Owner, uint Mount)> triggeredSessions = new();

    /// <summary>
    /// 连续失败计数与拉黑截止时间。
    /// 存在的意义：队友的坐骑可能已经满了、或者地形/状态导致动作被吞，
    /// 这种情况下每一轮轮询都会重新挑中他、重新失败、重新报错。
    /// 记下来，失败到一定次数就先晾一会儿。
    /// </summary>
    private readonly Dictionary<(ulong Owner, uint Mount), FailureRecord> failures = new();

    private struct FailureRecord
    {
        public int Count;
        public long Until;
        public string LastError;
    }

    // ---------------------------------------------------------------- 条件位名

    // 这些都不直接写 ConditionFlag.XXX：成员名跨版本增删过，
    // 按名字解析最多退化成「这个版本没这个条件，不检查」，不会连累编译。

    /// <summary>战斗相关。InCombat 一旦成立就不动手。</summary>
    private static readonly string[] CombatFlags = { "InCombat" };

    /// <summary>
    /// 自己已经在坐骑上的各种表现。
    /// Mounted 是骑乘中，Mounted2 是某些坐骑的第二形态，
    /// InFlight 是飞行中，Mounting / Mounting71 是上坐骑动画，
    /// MountOrOrnamentTransition 是坐骑/装饰切换过渡。
    /// 这些状态下再发共同骑乘一定是白费，游戏会直接吞掉。
    /// </summary>
    private static readonly string[] MountedFlags =
    {
        "Mounted", "Mounted2", "InFlight", "Mounting", "Mounting71",
        "MountOrOrnamentTransition", "MountImmobile",
    };

    /// <summary>已经坐在别人坐骑上了。</summary>
    private static readonly string[] PillionFlags = { "RidingPillion" };

    /// <summary>
    /// 此刻不该动手的状态：施法、读条、交互、过场、载入。
    /// 这些时候发出去的动作会被静默丢弃，白消耗一次冷却。
    /// </summary>
    private static readonly string[] BusyFlags =
    {
        "Casting", "Occupied", "OccupiedInEvent", "OccupiedInQuestEvent",
        "OccupiedInCutSceneEvent", "BetweenAreas", "WatchingCutscene",
    };

    private long lastPollTick;
    private long lastTriggerTick;
    private bool warnedMountTable;
    private bool warnedMountId;

    /// <summary>
    /// 待还原的「原来那个目标」。
    ///
    /// 发指令前会把目标切到队友身上，但这不能立刻还原：<c>&lt;t&gt;</c> 是游戏在
    /// 「执行指令那一刻」解析的，而发指令是把文本排队敲进聊天框，可能排到后面的帧才执行。
    /// 还原推迟到 0.7 秒后的验证那一步（<see cref="VerifyLastAction"/>），
    /// 另有一个 3 秒截止时间兜底（见 <see cref="OnFrameworkUpdate"/>）。
    /// </summary>
    private bool targetRestorePending;
    private IGameObject targetRestoreTarget;
    private long targetRestoreAtTick;

    /// <summary>
    /// 这一轮是用户主动要的（<c>/pr now</c>、设置窗口里的「立即触发一次」），
    /// 所以 <c>AutoNotify</c> / <c>AutoNotifyError</c> 就算勾了「不向聊天框发送自动提示」
    /// 也要照常回话 —— 是你自己在问，不能装死。
    /// <c>Execute</c> 一返回就由 <see cref="ForceTriggerOnce"/> 复位。
    /// </summary>
    private bool forceChatThisRound;

    // ---- 延迟验证 ----
    // UseAction 只是把请求递给游戏，返回 true 只代表「没抛异常」。
    // 坐骑满了 / 地形不对 / 距离不够时，游戏会静默吞掉，人根本没上去。
    // 所以发出去之后隔半秒回头看一眼 RidingPillion 有没有亮，才算真的成功。
    private bool verifyPending;
    private long verifyAtTick;
    private Candidate verifyCandidate;

    /// <summary>发出动作后多久去验证（毫秒）。太短游戏还没处理完，太长会显得迟钝。</summary>
    private const int VerifyDelayMs = 700;

    /// <summary>
    /// 「位置配对」判定的最大距离（米）。骑乘时坐骑对象就叠在骑手身上，
    /// 所以这个值只要够小就能把旁边站着的人排掉，不用太精确。
    /// </summary>
    private const float ProximityRadius = 3f;

    /// <summary>
    /// 自己刚从骑乘状态下来后，等多久再开始判断（毫秒）。
    ///
    /// 现场故障就是这个时机：自己召唤坐骑、骑一会儿、下坐骑，那一瞬间
    /// <c>Mounted</c> 条件刚从 true 变 false，插件的「自己没骑乘」这道闸门
    /// 立刻放行，而此时坐骑对象、目标、位置全还在收尾，判断最容易踩空。
    ///
    /// 等一秒半，等人站稳、坐骑消散，再开始挑人。代价只是下坐骑后晚一点触发，
    /// 换来的是不会在状态切换的缝隙里发出注定失败、还会糊一屏红字的指令。
    /// </summary>
    private const int DismountGraceMs = 1500;

    /// <summary>上一轮看到的「自己是否在骑乘」，用来识别 true → false 这个下降沿。</summary>
    private bool lastSelfMounted = true;

    /// <summary>下坐骑后的静默截止时刻（Environment.TickCount64）。0 表示不在静默期。</summary>
    private long dismountGraceUntil;

    public string MountDataFailureReason => MountData.FailureReason ?? string.Empty;

    /// <summary>聊天框通道当前状态，给设置窗口显示。</summary>
    public string ChatChannelStatus => ChatSender.StatusNote;

    /// <summary>当前实际走的哪条路（原生 / ECommons 备用 / 无）。</summary>
    public string ChatChannelName => ChatSender.ChannelName;

    /// <summary>聊天框通道能不能用。不能用时游戏原生指令送不进去，只能退化到 ProcessCommand。</summary>
    public bool ChatChannelAvailable => ChatSender.IsAvailable;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IPartyList partyList,
        ITargetManager targetManager,
        ICommandManager commandManager,
        IChatGui chatGui,
        IFramework framework,
        IDataManager dataManager,
        IDtrBar dtrBar,
        IPluginLog log)
    {
        PluginInterface = pluginInterface;
        this.clientState = clientState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.framework = framework;
        this.dataManager = dataManager;
        this.log = log;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // 老版本的配置里 UseNativeAction 默认是 true（那时候以为 GeneralAction 里有「共同骑乘」，
        // 结果 5 是分解 —— 点开分解道具窗口的就是它）。现在游戏指令是主力路线，
        // 两者同时为 true 会让失败回退又去点分解，所以在加载时把旧的独立模式关掉。
        // 用户真要用独立模式，设置里重新勾一下就行（勾的时候会自动关掉游戏指令）。
        if (Config.UseGameCommand && Config.UseNativeAction)
        {
            Config.UseNativeAction = false;
            Config.Save();
            log.Information("[PillionRider] 已自动关闭旧的独立模式（GeneralAction 路线），改用游戏指令");
        }

        // 插件加载的瞬间 Excel 数据可能还没就绪，这里先试一次；之后 Tick 里还会重试。
        MountData.Initialize(dataManager, log);

        // 顺便探测「共同骑乘」这个 GeneralAction 的行 ID（只在走独立模式时才有意义 ——
        // 那张表里其实没有这一行，探测多半会报「没找到」，留在日志里当证据）。
        if (Config.UseNativeAction)
        {
            RidePillionAction.Discover(dataManager, log);
        }

        this.configWindow = new ConfigWindow(this);
        this.windowSystem.AddWindow(this.configWindow);

        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigWindow;

        // 注册主界面回调。没有这个，/xlplugins 的校验会报
        // "The plugin does not register a main UI callback" 警告。
        pluginInterface.UiBuilder.OpenMainUi += this.ToggleConfigWindow;

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "PillionRider：/pr 设置 | on | off | toggle | now | scan | kinds | chat | chatevents | quiet | mountinfo <坐骑ID> | gcmd | gcmdformat <模板> | native | ga <id> | galist | redisc | mount <id> | clearmount",
            ShowInHelp = true,
        });

        this.framework.Update += this.OnFrameworkUpdate;

        this.SetupDtrBar(dtrBar);

        // 自动隐藏「共同乘乘」指令自己产生的红色错误提示。
        // 游戏那边没法阻止它生成红字，只能在聊天框层面拦下来。
        // 装不上也不影响任何其它功能。
        ChatErrorSuppressor.Install(chatGui, this.log);

        // 提前探一次聊天框通道，让日志里直接有结论（用户排查时不用先敲 /pr scan）。
        // 原生 ProcessChatBoxEntry 优先，拿不到再退 ECommons。
        ChatSender.RefreshStatus(this.log);
        this.log.Information("[PillionRider] 聊天框通道可用={Available}（{Channel}）：{Note}",
            ChatSender.IsAvailable, ChatSender.ChannelName, ChatSender.StatusNote);

        this.log.Information(
            "[PillionRider] 已加载，执行方式={Mode}，{Detail}",
            Config.UseGameCommand
                ? "游戏指令"
                : Config.UseNativeAction ? "独立模式" : "未启用任何途径",
            Config.UseGameCommand
                ? $"模板={Config.GameCommandFormat}"
                : Config.UseNativeAction
                    ? $"GeneralAction={RidePillionAction.ResolveGeneralActionId(Config.GeneralActionId)}（{RidePillionAction.DiscoveryNote}）"
                    : "都是关的");
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.commandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleConfigWindow;
        this.windowSystem.RemoveAllWindows();

        // 状态栏条目要自己收尸，否则插件卸载后那个「PillionRider: on」
        // 会一直挂在那儿，点了还会回调到已经死掉的实例。
        this.dtrEntry?.Remove();
        this.dtrEntry = null;

        // 聊天框拦截器同理：不退订的话，它会一直挂在聊天框事件上。
        ChatErrorSuppressor.Uninstall();
    }

    /// <summary>
    /// 在右上角的服务器信息栏挂一个「PillionRider: on / off」，点它就切换启用状态。
    ///
    /// 为什么不写在 OnFrameworkUpdate 里：<see cref="IDtrBar.Get"/> 对同名条目会抛
    /// <c>ArgumentException</c>，每次轮询都 Get 一次必然炸。所以这里只拿一次、存起来，
    /// 之后只改 <c>Text</c> / <c>Tooltip</c>。
    /// </summary>
    private void SetupDtrBar(IDtrBar dtrBar)
    {
        try
        {
            // 先落到 object 上再 as：不同版本 Get 的返回类型可能是 IDtrBarEntry，也可能是
            // DtrBarEntry（甚至别的实现）。直接 as 到无继承关系的类型会 CS0039 编不过，
            // 经 object 中转则任何引用类型都能安全转换。
            object entry = dtrBar.Get(DtrTitle, new SeString());
            this.dtrEntry = entry as IDtrBarEntry;

            if (this.dtrEntry == null)
            {
                this.log.Warning("[PillionRider] 状态栏条目类型不认识，快捷入口不可用（其它功能正常）");
                return;
            }
        }
        catch (Exception ex)
        {
            // 拿不到就算了 —— 状态栏只是锦上添花的快捷入口，不能因为它在启动阶段崩掉。
            this.log.Warning(ex, "[PillionRider] 无法创建状态栏条目，快捷键入口不可用（其它功能正常）");
            this.dtrEntry = null;
            return;
        }

        // API 15 的 OnClick 是 Action<DtrInteractionEvent>，不是无参 Action。
        // 用隐式类型的 lambda 接，就不用把 DtrInteractionEvent 这个名字写进签名，
        // 少一处对类型名的硬依赖。
        this.dtrEntry.OnClick = _ => this.ToggleEnabled();

        this.RefreshDtrBar();
    }

    /// <summary>点击状态栏条目时切换总开关。</summary>
    private void ToggleEnabled()
    {
        Config.Enabled = !Config.Enabled;
        Config.Save();
        this.RefreshDtrBar();
        // 点状态栏图标算「用户直接操作」而不是「用户敲命令」：
        // 条目文本当场就从 on 变成 off 了，聊天框不必再复述一遍。
        // 所以这里走 AutoNotify，静音时只进日志。
        this.AutoNotify(Config.Enabled ? "已启用" : "已停用");
    }

    /// <summary>把当前状态刷到状态栏上。文本格式固定为 <c>PillionRider: on|off</c>。</summary>
    public void RefreshDtrBar()
    {
        if (this.dtrEntry == null)
        {
            return;
        }

        this.dtrEntry.Text = Config.Enabled ? "PillionRider: on" : "PillionRider: off";
        this.dtrEntry.Tooltip = Config.Enabled
            ? "PillionRider 已启用\n点击停用，输入 /pr 打开设置"
            : "PillionRider 已停用\n点击启用，输入 /pr 打开设置";
    }

    private void ToggleConfigWindow()
    {
        this.configWindow.IsOpen = !this.configWindow.IsOpen;
    }

    /// <summary>
    /// 统一走三参数重载，避免依赖可选参数的默认值（不同 Dalamud 版本处理不完全一致）。
    /// 第二个参数是聊天标签，Dalamud 会自动加上 [标签] 前缀，所以正文里不再重复插件名。
    /// </summary>
    private void Notify(string message) => this.chatGui.Print(message, PluginName, null);

    private void NotifyError(string message) => this.chatGui.PrintError(message, PluginName, null);

    /// <summary>
    /// <b>插件自己发起</b>的提示（不是用户敲命令要的）。
    ///
    /// 这类消息一律先进 <c>/xllog</c>；只有没勾「不向聊天框发送自动提示」时才同时打到聊天框。
    /// 这样默认状态下聊天框是干净的，但排查时日志里一条都不少。
    /// </summary>
    private void AutoNotify(string message)
    {
        this.log.Information("[PillionRider] {Message}", message);

        if (!Config.MuteAutoChat || this.forceChatThisRound)
        {
            this.Notify(message);
        }
    }

    /// <summary>插件自己发起的警告/错误，规则同 <see cref="AutoNotify"/>。</summary>
    private void AutoNotifyError(string message)
    {
        this.log.Warning("[PillionRider] {Message}", message);

        if (!Config.MuteAutoChat || this.forceChatThisRound)
        {
            this.NotifyError(message);
        }
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        // 目标还原的兜底：正常路径在 VerifyLastAction 里还原。
        // 万一那次验证没排上（中途停用插件、掉登录、换角色），这里按截止时间兜住 ——
        // 绝不能把用户的目标永久改掉。所以它要放在「插件已停用」判断之前。
        if (this.targetRestorePending && Environment.TickCount64 >= this.targetRestoreAtTick)
        {
            this.RestoreTargetNow();
        }

        if (!Config.Enabled || !this.clientState.IsLoggedIn)
        {
            return;
        }

        // 验证不依赖轮询节流：它有自己的时间点，且很轻（只读一个条件位）。
        var stamp = Environment.TickCount64;

        if (this.verifyPending && stamp >= this.verifyAtTick)
        {
            this.verifyPending = false;
            try
            {
                this.VerifyLastAction();
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "[PillionRider] 验证异常");
            }
        }

        if (stamp - this.lastPollTick < Math.Max(100, Config.PollIntervalMs))
        {
            return;
        }

        this.lastPollTick = stamp;

        try
        {
            this.Tick(stamp);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PillionRider] 轮询异常");
        }
    }

    /// <summary>
    /// 发出指令/动作后隔一会儿回头看：自己是不是真的坐在别人坐骑上了。
    ///
    /// 不管走的是游戏指令还是 <c>UseAction</c>，同步返回成功都
    /// ≠ 真的上去了。队友的坐骑已经满员、地形不平、距离差一点点，游戏都会静默忽略，
    /// 接口层面看不出任何异常。只有回头看 <c>RidingPillion</c> 这个条件位才分得清
    /// 「请求发出去了」和「人真的上去了」。
    /// </summary>
    private void VerifyLastAction()
    {
        // 指令这会儿已经执行完了，才把目标还原回去。
        // 早先是在发完指令的同一步就还原的，那会让还没执行的 <t> 解析失败 ——
        // 现场那句「"<t>"出现问题：1号指定的目标名不正确。」就是这么来的。
        this.RestoreTargetNow();

        var candidate = this.verifyCandidate;
        var riding = GameApi.HasAnyCondition(this.condition, PillionFlags);

        if (riding)
        {
            // 真的上去了 → 清掉失败记录，这个队友恢复正常。
            this.ClearFailure(candidate.OwnerId, candidate.MountId);

            if (Config.DebugLog)
            {
                this.log.Information(
                    "[PillionRider] 验证通过：已乘上 {Name} 的坐骑 {Mount}",
                    candidate.Name,
                    candidate.MountId);
            }

            return;
        }

        // 没上去 → 撤回「本趟已触发过」的记号，否则 OncePerMountSession 会把它永久锁死，
        // 明明没上去却再也不会重试了。
        if (Config.OncePerMountSession)
        {
            this.triggeredSessions.Remove((candidate.OwnerId, candidate.MountId));
        }

        this.RecordFailure(candidate, "动作发出去了但没坐上去（没骑上）");

        this.log.Warning(
            "[PillionRider] 指令/动作发出去了但没坐上 {Name} 的坐骑 {Mount}：可能是坐骑已满 / 距离不够 / 地形不允许 / 目标不对（当前途径 {Mode}）",
            candidate.Name,
            candidate.MountId,
            Config.UseGameCommand
                ? $"游戏指令 {Config.GameCommandFormat}"
                : $"GeneralAction={RidePillionAction.ResolveGeneralActionId(Config.GeneralActionId)}");
    }

    /// <summary>安排一次延迟验证。</summary>
    private void ScheduleVerify(Candidate candidate)
    {
        this.verifyCandidate = candidate;
        this.verifyPending = true;
        this.verifyAtTick = Environment.TickCount64 + VerifyDelayMs;
    }

    private void Tick(long now)
    {
        // 执行途径都关掉了（游戏指令、独立模式）→ 什么都干不了，直接不扫对象表。
        // 不加这条的话，每轮都会挑中一个候选、进 Execute、报一次「本次跳过」，白白刷屏。
        if (!Config.UseGameCommand && !Config.UseNativeAction)
        {
            return;
        }

        // 单人（没有队伍）→ 整个插件休眠。
        //
        // 这条放在最前面，因为它是最省的一道闸：没有队伍就没有候选对象，
        // 后面那些都白算 —— 包括 MountData.Initialize（会反射打开 Excel 坐骑表）
        // 和 RidePillionAction.Discover（会扫整张 GeneralAction 表找动作 ID）。
        // 一个人打怪、采集、跑图的时候，这些开销一次都不该产生。
        //
        // 顺便也把「路人骑着多人坐骑，我要不要凑上去」这个念头彻底掐掉了：
        // 本插件只对队伍里的人生效。
        if (this.partyList.Length == 0)
        {
            return;
        }

        // 跨服小队（跨服组队 / 跨服团队）→ 同样整个停。
        //
        // 为什么必须停：跨服时队友的角色实体根本不在本地对象表里，坐骑 ID 读不出来，
        // 就算读出来，`/共同骑乘` 也只能对<b>同服</b>的队友生效 —— 强行发出去只会
        // 换来一条「对象不存在」或者干脆静默失败，然后被 VerifyLastAction 记成失败、
        // 拉黑 60 秒，白白污染失败记录。
        // 判据取自游戏自己的条件位 ParticipatingInCrossWorldPartyOrAlliance
        // （官方说明就是「跨服队伍中无法执行指令」），和我们要的判断完全同源。
        if (GameApi.IsCrossWorldParty(this.condition, this.partyList, this.objectTable, this.clientState))
        {
            return;
        }

        // 坐骑表在插件加载瞬间可能还没准备好，这里做有限次重试。
        // Initialize 内部自带 available / 尝试次数上限的短路，重复调用是廉价的。
        MountData.Initialize(this.dataManager, this.log);

        if (Config.UseNativeAction)
        {
            RidePillionAction.Discover(this.dataManager, this.log);
        }

        if (this.condition[ConditionFlag.BetweenAreas] ||
            this.condition[ConditionFlag.WatchingCutscene] ||
            this.condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            return;
        }

        // 副本里直接不干活：连对象表都不扫。
        // BoundByDuty 在进本瞬间就为 true，早于战斗状态，所以能挡住「副本内非战斗但队友骑着坐骑」的情况。
        if (GameApi.IsInDuty(this.condition))
        {
            return;
        }

        if (GameApi.HasAnyCondition(this.condition, CombatFlags))
        {
            return;
        }

        // 自己已经在坐骑上 / 已经在共同骑乘 / 正在上坐骑动画里 —— 一律不动手。
        // 这一步放在最前面（扫对象表之前），因为它是最常见的「本来就不该触发」的情况，
        // 也是最省的一步：只读几个条件位，不碰对象表。
        //
        // 注意这里不能直接 `if (mounted) return;` 了事：下坐骑的那一瞬间
        // Mounted 刚从 true 掉到 false，闸门立刻放行，而这时坐骑对象、目标、
        // 位置全在收尾，判断最容易踩空（现场报错就发生在这个时刻）。
        // 所以先记录下降沿，再按下坐骑的时刻起算一小段静默期。
        var selfMountedNow = GameApi.HasAnyCondition(this.condition, MountedFlags);

        if (this.lastSelfMounted && !selfMountedNow)
        {
            this.dismountGraceUntil = now + DismountGraceMs;

            if (Config.DebugLog)
            {
                this.log.Information(
                    "[PillionRider] 检测到下坐骑，静默 {Grace} 毫秒后再开始判断",
                    DismountGraceMs);
            }
        }

        this.lastSelfMounted = selfMountedNow;

        if (selfMountedNow)
        {
            return;
        }

        // 下坐骑后的静默期还没过 → 这一轮不判断，也顺手把候选记录清干净，
        // 免得下一次触发用的是「下坐骑瞬间拍下的旧快照」。
        if (this.dismountGraceUntil != 0)
        {
            if (now < this.dismountGraceUntil)
            {
                return;
            }

            this.dismountGraceUntil = 0;
        }

        if (GameApi.HasAnyCondition(this.condition, PillionFlags))
        {
            return;
        }

        // 施法 / 读条 / 交互 / 过场：这时候发的动作会被游戏静默丢掉。
        if (GameApi.HasAnyCondition(this.condition, BusyFlags))
        {
            return;
        }

        // 队伍人数在上面已经挡过了（没队伍直接 return），这里不用再查一次。
        var mountsByOwner = this.CollectMounts();

        // 队友下坐骑后清掉记录，这样他再次上坐骑还能重新触发
        if (this.triggeredSessions.Count > 0)
        {
            this.triggeredSessions.RemoveWhere(
                key => !mountsByOwner.TryGetValue(key.Owner, out var live) || live != key.Mount);
        }

        // 拉黑到期的失败记录也一并清掉，否则字典会随长时间游玩一直长
        if (this.failures.Count > 0)
        {
            this.PruneFailures(now, mountsByOwner);
        }

        var candidate = this.FindCandidate(mountsByOwner);
        if (candidate == null)
        {
            return;
        }

        if (now - this.lastTriggerTick < Math.Max(500, Config.CooldownMs))
        {
            return;
        }

        this.lastTriggerTick = now;
        this.Execute(candidate.Value);
    }

    /// <summary>
    /// 清掉已经没意义的失败记录：队友下坐骑了，或者拉黑时间已过。
    ///
    /// 注意 <c>Until == 0</c> 表示「还没累计到上限，没进拉黑期」，这种记录不能删 ——
    /// 删了计数就永远从 0 开始，连续失败限制等于没开。
    /// </summary>
    private void PruneFailures(long now, Dictionary<ulong, uint> mountsByOwner)
    {
        List<(ulong Owner, uint Mount)> remove = null;

        foreach (var kv in this.failures)
        {
            var stillMounted = mountsByOwner.TryGetValue(kv.Key.Owner, out var live) && live == kv.Key.Mount;
            var backoffExpired = kv.Value.Until != 0 && now >= kv.Value.Until;

            if (!stillMounted || backoffExpired)
            {
                remove ??= new List<(ulong Owner, uint Mount)>();
                remove.Add(kv.Key);
            }
        }

        if (remove == null)
        {
            return;
        }

        foreach (var key in remove)
        {
            this.failures.Remove(key);
        }
    }

    private struct Candidate
    {
        public ulong OwnerId;
        public uint MountId;
        public string Name;
        public IGameObject GameObject;

        /// <summary>该队友在队伍里的序号（1-8），拿不到就是 0。游戏宏的 &lt;N&gt; 占位符对应用它。</summary>
        public int PartyIndex;
    }

    /// <summary>扫描场上所有角色，返回 骑手 GameObjectId → 坐骑 DataId。</summary>
    private Dictionary<ulong, uint> CollectMounts()
    {
        var local = GameApi.GetLocalPlayer(this.objectTable, this.clientState);
        var selfId = local != null ? local.GameObjectId : 0UL;

        var result = new Dictionary<ulong, uint>();

        // 主路径：直接问角色「你在骑哪个坐骑」。
        // 这条路比「坐骑对象反查骑手」可靠得多 —— 实测坐骑对象的 OwnerId 常常是
        // 0xE0000000（客户端本地对象，不是网络实体），根本反查不出骑手是谁。
        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var obj = this.objectTable[idx];

            // 对象表的空槽位会返回 null。原来这里先读 obj.GameObjectId 再判空，
            // 于是 /pr scan 和每次轮询都在这一行抛 NullReferenceException ——
            // 表现就是「设置窗口点立即扫描报错」外加「自动逻辑永远不触发」。
            if (obj == null)
            {
                continue;
            }

            if (obj.GameObjectId == selfId)
            {
                continue;
            }

            if (!GameApi.IsPlayer(obj))
            {
                continue;
            }

            if (GameApi.TryGetRiderMountId(obj, out var riderMountId))
            {
                // 读到 0 视同没读到：0 不是坐骑 ID，是「读不出来」。
                // 放它进去会一路传到 PassesFilters，被当成「没读到所以放开多座判定」，
                // 于是单座坐骑也能当候选，最后发出注定失败的指令。
                if (riderMountId != 0)
                {
                    result[obj.GameObjectId] = riderMountId;
                }
            }
        }

        // 兜底路径：有些版本 CurrentMount 结构体里没有可读的 ID，
        // 那就反过来用坐骑对象的 OwnerId 关联。两种办法互补，谁有值用谁。
        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var obj = this.objectTable[idx];
            if (obj == null)
            {
                continue;
            }

            if (!GameApi.IsMount(obj))
            {
                continue;
            }

            if (obj.OwnerId == 0 || obj.OwnerId == selfId || GameApi.IsLocalObjectId(obj.OwnerId))
            {
                continue;
            }

            if (result.ContainsKey(obj.OwnerId))
            {
                continue;
            }

            var mountId = GameApi.GetDataId(obj);
            if (mountId != 0)
            {
                result[obj.OwnerId] = mountId;
            }
        }

        // 第三路径：位置配对。
        // 前面两条都读不出东西时（现场就是这样：坐骑对象 OwnerId=0xE0000000、DataId=0，
        // 角色身上的 CurrentMount 也拿不到 ID），只剩「谁跟坐骑站在一起」这一条线索。
        // 骑乘时坐骑对象的位置就是骑手的位置，所以「离这台坐骑最近的玩家」就是骑手。
        //
        // 这条路只在最后兜底，所以额外要求坐骑 ID 必须读得出来 —— 见下面 pairedMountId 那段。
        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var mountObj = this.objectTable[idx];
            if (mountObj == null)
            {
                continue;
            }

            if (!GameApi.IsMount(mountObj))
            {
                continue;
            }

            var mountPos = mountObj.Position;
            ulong bestId = 0;
            var bestDistance = ProximityRadius;

            for (var j = 0; j < this.objectTable.Length; j++)
            {
                var candidate = this.objectTable[j];
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.GameObjectId == selfId || !GameApi.IsPlayer(candidate))
                {
                    continue;
                }

                if (result.ContainsKey(candidate.GameObjectId))
                {
                    continue;
                }

                var distance = Vector3.Distance(mountPos, candidate.Position);
                if (distance > bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestId = candidate.GameObjectId;
            }

            if (bestId == 0)
            {
                continue;
            }

            // 读不到坐骑 ID 就不收这个候选。
            //
            // 这条路只在「前两条都读不出来」时才走，本来就不可靠；而 0 会一路传到
            // PassesFilters 里被当成「没读到所以放开多座判定」，于是单座坐骑也会放进来。
            // 现场就是这么报错的：队友骑单人坐骑、下坐骑，坐骑对象还在原地消散，
            // 位置配对把人和这台「已经没 ID 的坐骑」凑成一对，插件就发了一条指令出去。
            var pairedMountId = GameApi.GetDataId(mountObj);
            if (pairedMountId == 0)
            {
                continue;
            }

            result[bestId] = pairedMountId;

            if (Config.DebugLog)
            {
                this.log.Information(
                    "[PillionRider] 位置配对：玩家 {PlayerId} 骑着坐骑对象（距离 {Distance:F1} 米，坐骑 ID {MountId}）",
                    bestId, bestDistance, pairedMountId);
            }
        }

        return result;
    }

    /// <summary>
    /// 队伍成员的名字与实体 ID 快照。
    /// 不用 <c>IPartyMember.GameObject</c>：那属性每次访问都会去遍历对象表，而且跨版本命名有差异；
    /// 用名字 + EntityId 双路匹配更稳。
    /// </summary>
    private sealed class PartySnapshot
    {
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<uint> EntityIds = new();

        /// <summary>
        /// 名字 → 游戏文本指令认的小队编号（<c>/共同骑乘 &lt;N&gt;</c> 里那个 N）。
        ///
        /// <b>算法：自己恒为 &lt;1&gt;，其余人按 <c>IPartyList</c> 的顺序依次 &lt;2&gt;、&lt;3&gt;……</b>
        ///
        /// 为什么不是「<c>IPartyList</c> 下标 + 1」：<c>IPartyList</c> 是 GroupManager
        /// 内存里的队员数组顺序（大致是入队顺序），而游戏 UI 的小队列表和 <c>&lt;N&gt;</c>
        /// 占位符用的是「自己永远排第一」那套编号 —— 用户确认过：<b>副本外，队伍的第一个人
        /// 只会是自己</b>（除非有特殊设置）。
        ///
        /// 现场那条报错正是这个差异造成的：B 的 <c>IPartyList</c> 是
        /// <c>[时花, 蕾米娜大福(自己)]</c>，旧代码按「下标 + 1」算，于是时花 = <c>&lt;1&gt;</c>。
        /// 可游戏的 <c>&lt;1&gt;</c> 是 B 自己 —— 发 <c>/共同骑乘 &lt;1&gt;</c> 等于对自己用，
        /// 游戏回「1号指定的目标名不正确」。
        /// </summary>
        public readonly Dictionary<string, int> NameToIndex = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>EntityId → 游戏文本指令认的小队编号（同上，自己恒为 1）。</summary>
        public readonly Dictionary<uint, int> EntityToIndex = new();

        /// <summary>IPartyList 里是否包含自己。</summary>
        public bool ContainsSelf;

        /// <summary>自己在 <c>IPartyList</c> 里的原始位置（1 起算）。0 = 没认出来。仅供 /pr scan 展示。</summary>
        public int SelfRawSlot;
    }

    /// <summary>
    /// 收集队伍成员，并算出每个人在游戏文本指令里的 <c>&lt;N&gt;</c> 编号。
    ///
    /// <b>编号规则：自己恒为 &lt;1&gt;，其余人按 <c>IPartyList</c> 顺序依次 &lt;2&gt;、&lt;3&gt;……</b>
    /// （用户确认：副本外队伍的第一位只会是自己，除非有特殊设置）。
    ///
    /// 这跟「<c>IPartyList</c> 下标 + 1」不是一回事 —— 现场实测 2 人小队里，
    /// 自己 <c>IPartyList</c> 排第 2 位，队友落在这个位置的第 1 格，
    /// 旧代码把它当成 <c>&lt;1&gt;</c> 发出去，而游戏的 <c>&lt;1&gt;</c> 是自己，
    /// 于是回「1号指定的目标名不正确」。
    ///
    /// 主路线仍然是「切目标 + <c>&lt;t&gt;</c>」（见 <see cref="BuildGameCommand"/>），
    /// 这个编号用于：① <c>/pr scan</c> 展示；② 用户主动关掉「切目标」时的兜底写法；
    /// ③ 自己恒为 1 这条约束本身能挡掉「对自己发指令」。
    /// </summary>
    private PartySnapshot CollectParty()
    {
        var snapshot = new PartySnapshot();

        var local = GameApi.GetLocalPlayer(this.objectTable, this.clientState);
        var selfEntityId = local != null ? local.EntityId : 0u;
        var selfName = local != null && local.Name != null ? local.Name.ToString() : null;

        var entries = new List<(string Name, uint EntityId, int RawIndex)>();

        for (var i = 0; i < this.partyList.Length; i++)
        {
            var member = this.partyList[i];
            if (member == null)
            {
                continue;
            }

            var name = member.Name != null ? member.Name.ToString() : null;
            var entityId = member.EntityId;

            if (!string.IsNullOrEmpty(name))
            {
                snapshot.Names.Add(name);
            }

            if (entityId != 0)
            {
                snapshot.EntityIds.Add(entityId);
            }

            entries.Add((name, entityId, i));
        }

        // 先认出「哪个 entry 是自己」—— 后面的编号完全取决于它。
        int selfRawIndex = -1;

        foreach (var entry in entries)
        {
            var sameEntity = selfEntityId != 0 && entry.EntityId == selfEntityId;
            var sameName = !string.IsNullOrEmpty(selfName) &&
                           !string.IsNullOrEmpty(entry.Name) &&
                           string.Equals(entry.Name, selfName, StringComparison.OrdinalIgnoreCase);

            if (sameEntity || sameName)
            {
                selfRawIndex = entry.RawIndex;
                snapshot.ContainsSelf = true;
                snapshot.SelfRawSlot = entry.RawIndex + 1;
                break;
            }
        }

        // 编号：自己恒为 1，其余人按 IPartyList 顺序依次 +1。
        //
        // 认不出自己时（本地玩家还没加载好、或者 IPartyList 不含自己）退化成
        // 「下标 + 1」—— 至少是个连续的编号，但那种情况下大概率也不该发指令。
        var next = 2;

        foreach (var entry in entries)
        {
            int slot;

            if (selfRawIndex >= 0 && entry.RawIndex == selfRawIndex)
            {
                slot = 1;
            }
            else
            {
                slot = next;
                next++;
            }

            if (!string.IsNullOrEmpty(entry.Name) && !snapshot.NameToIndex.ContainsKey(entry.Name))
            {
                snapshot.NameToIndex[entry.Name] = slot;
            }

            if (entry.EntityId != 0 && !snapshot.EntityToIndex.ContainsKey(entry.EntityId))
            {
                snapshot.EntityToIndex[entry.EntityId] = slot;
            }
        }

        return snapshot;
    }

    private static bool IsPartyMember(PartySnapshot snapshot, IGameObject obj)
    {
        // 1) 游戏自己的队伍标记最可信（不依赖名字是否重复，也不要求队友在视野里）
        if (obj is ICharacter character &&
            (character.StatusFlags & StatusFlags.PartyMember) != 0)
        {
            return true;
        }

        if (snapshot.Names.Count == 0 && snapshot.EntityIds.Count == 0)
        {
            return false;
        }

        // 2) 名字匹配（大小写不敏感）
        var name = obj.Name != null ? obj.Name.ToString() : null;
        if (!string.IsNullOrEmpty(name) && snapshot.Names.Contains(name))
        {
            return true;
        }

        // 3) 实体 ID 匹配
        return obj.EntityId != 0 && snapshot.EntityIds.Contains(obj.EntityId);
    }

    private Candidate? FindCandidate(Dictionary<ulong, uint> mountsByOwner, bool ignoreSessionRecord = false)
    {
        var local = GameApi.GetLocalPlayer(this.objectTable, this.clientState);
        if (local == null || mountsByOwner.Count == 0)
        {
            return null;
        }

        var localPos = local.Position;
        var extraIds = Config.GetExtraMountIdSet();
        var party = this.CollectParty();

        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var obj = this.objectTable[idx];
            if (obj == null)
            {
                continue;
            }

            if (!GameApi.IsPlayer(obj))
            {
                continue;
            }

            if (obj.GameObjectId == local.GameObjectId)
            {
                continue;
            }

            if (!mountsByOwner.TryGetValue(obj.GameObjectId, out var mountId))
            {
                continue;
            }

            // 只认队伍里的人。队伍外的路人骑多人坐骑的情况，多半跟你没关系，
            // 冒然往上凑只会把人吓一跳（而且人家也未必想带你）。
            if (!IsPartyMember(party, obj))
            {
                continue;
            }

            if (!this.PassesFilters(obj, mountId, localPos, extraIds, ignoreSessionRecord))
            {
                continue;
            }

            return new Candidate
            {
                OwnerId = obj.GameObjectId,
                MountId = mountId,
                Name = obj.Name != null ? obj.Name.ToString() : "队友",
                GameObject = obj,
                PartyIndex = ResolvePartyIndex(party, obj),
            };
        }

        return null;
    }

    /// <summary>
    /// 查这个角色在游戏文本指令里的队伍编号（1-8）。先按 EntityId，再按名字，都没有返回 0。
    ///
    /// 返回值 0 表示「算不出来」，调用方会改用 <c>&lt;t&gt;</c>。
    /// 返回 1 表示「这个人就是自己」（见 <see cref="CollectParty"/> 的编号规则）——
    /// 对自己发 <c>/共同骑乘 &lt;1&gt;</c> 只会换来一句「1号指定的目标名不正确」，
    /// 所以调用方必须把 1 挡掉。
    /// </summary>
    private static int ResolvePartyIndex(PartySnapshot party, IGameObject obj)
    {
        if (obj.EntityId != 0 && party.EntityToIndex.TryGetValue(obj.EntityId, out var byEntity))
        {
            return byEntity;
        }

        var name = obj.Name != null ? obj.Name.ToString() : null;
        if (!string.IsNullOrEmpty(name) && party.NameToIndex.TryGetValue(name, out var byName))
        {
            return byName;
        }

        return 0;
    }

    /// <summary>
    /// 最后一招：直接拿 <c>IPartyList[i].GameObject</c> 和对象比 <c>GameObjectId</c>。
    ///
    /// 前两层（EntityId、名字）都匹配不上时才走这条。名字匹配会因为 SeString
    /// 的排版差异而偶尔失手（比如带装饰字符），而 GameObjectId 是硬 ID，对上了就一定对。
    ///
    /// 代价：<c>IPartyMember.GameObject</c> 内部会遍历对象表（官方注释写了「it should be used with care」），
    /// 所以只在真正要发指令前兜底调用一次，不进每轮轮询。
    ///
    /// 编号算法和 <see cref="CollectParty"/> 完全一致：自己恒为 <c>&lt;1&gt;</c>，
    /// 其余人按列表顺序依次 <c>&lt;2&gt;</c>、<c>&lt;3&gt;</c>……（<b>不是</b>下标 + 1，
    /// 那样会把自己的位置算成别人的编号）。
    /// </summary>
    private int ResolvePartyIndexByGameObject(ulong gameObjectId)
    {
        // 先拿一份权威编号表，再只负责「找出这个 GameObjectId 对应队伍里的哪一格」。
        var party = this.CollectParty();

        for (var i = 0; i < this.partyList.Length; i++)
        {
            var member = this.partyList[i];
            if (member == null)
            {
                continue;
            }

            IGameObject memberObject;
            try
            {
                memberObject = member.GameObject;
            }
            catch
            {
                continue;
            }

            if (memberObject == null || memberObject.GameObjectId != gameObjectId)
            {
                continue;
            }

            if (member.EntityId != 0 && party.EntityToIndex.TryGetValue(member.EntityId, out var byEntity))
            {
                return byEntity;
            }

            var name = member.Name != null ? member.Name.ToString() : null;
            if (!string.IsNullOrEmpty(name) && party.NameToIndex.TryGetValue(name, out var byName))
            {
                return byName;
            }

            return 0;
        }

        return 0;
    }

    /// <summary>
    /// 候选人的队伍位置：先用快照里算好的，算不出来（0）再补两次。
    /// 只用于展示与「关掉切目标」时的兜底写法 —— 主路线是切目标 + <c>&lt;t&gt;</c>。
    /// </summary>
    private int ResolveCandidatePartyIndex(Candidate candidate)
    {
        if (candidate.PartyIndex >= 1)
        {
            return candidate.PartyIndex;
        }

        var partyIndex = 0;
        var party = this.CollectParty();

        if (candidate.GameObject != null)
        {
            partyIndex = ResolvePartyIndex(party, candidate.GameObject);
        }

        if (partyIndex < 1)
        {
            partyIndex = this.ResolvePartyIndexByGameObject(candidate.OwnerId);
        }

        return partyIndex;
    }

    /// <summary>
    /// 把当前目标切到候选队友身上，并读回来确认真的切成了。
    ///
    /// 为什么要读回来：<c>&lt;t&gt;</c> 是游戏在「执行指令那一刻」解析的。
    /// 目标没切成功（对象刚好消失、被别的插件抢走）时它会解析失败，
    /// 游戏回一句「"&lt;t&gt;"出现问题：1号指定的目标名不正确。」—— 现场那句红字就是这么来的。
    /// 与其发出去赌一把，不如先确认；确认不了就放弃这一轮。
    ///
    /// <b>这是主路径</b>（见 <see cref="Execute"/>）：<c>&lt;N&gt;</c> 编号跟游戏自己认的
    /// 小队编号对不上（现场发 <c>&lt;1&gt;</c> 被回「1号指定的目标名不正确」），
    /// 所以改成「切目标 + <c>&lt;t&gt;</c>」。切目标有副作用，所以只在真的要用 <c>&lt;t&gt;</c> 时切。
    ///
    /// 注意这里不还原目标：还原推迟到指令执行完之后（见 <see cref="RestoreTargetNow"/>）。
    /// </summary>
    private bool TrySwitchTarget(Candidate candidate)
    {
        var target = candidate.GameObject;

        if (target == null)
        {
            return false;
        }

        try
        {
            this.targetManager.Target = target;

            var now = this.targetManager.Target;

            if (now != null && now.GameObjectId == candidate.OwnerId)
            {
                return true;
            }

            this.log.Warning(
                "[PillionRider] 切目标没成功（想要 {Name} goId={Want}，当前 {Now}），这一轮不执行",
                candidate.Name,
                candidate.OwnerId,
                now != null ? now.GameObjectId.ToString() : "无目标");

            return false;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[PillionRider] 切目标时异常，这一轮不执行");
            return false;
        }
    }

    /// <summary>
    /// 把目标还原成发指令之前那个（只会还原一次）。
    ///
    /// 触发点有两个：<see cref="VerifyLastAction"/>（正常路径，约 0.7 秒后）和
    /// <see cref="OnFrameworkUpdate"/> 里的 3 秒截止时间兜底。
    /// </summary>
    private void RestoreTargetNow()
    {
        if (!this.targetRestorePending)
        {
            return;
        }

        this.targetRestorePending = false;

        try
        {
            this.targetManager.Target = this.targetRestoreTarget;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[PillionRider] 还原目标失败（不影响其它功能）");
        }

        this.targetRestoreTarget = null;
    }

    /// <summary>
    /// 拼出要发给游戏的 /共同骑乘 指令。
    ///
    /// 游戏自己就有这个文本指令（国际服 <c>/ridepillion</c>，国服 <c>/共同骑乘</c>），
    /// 参数是目标占位符加座位号 1-7，玩家手写宏就是 <c>/共同骑乘 &lt;2&gt; 1</c>。
    /// 官方支持的占位符就两种写法：
    ///   <c>&lt;t&gt;</c> / <c>&lt;target&gt;</c> —— 「当前选中的目标」
    ///   <c>&lt;1&gt;</c>-<c>&lt;8&gt;</c>      —— 「小队列表里的第 N 位队员」（自己恒为 1）
    ///
    /// <b>首选 &lt;t&gt;</b>：先把目标切到队友身上（切完读回来确认过），
    /// 再用「当前目标」这种写法发指令。这条路不依赖编号语义。
    ///
    /// <b>兜底 &lt;N&gt;</b>：只在用户主动关掉「切目标」时才用（<see cref="Configuration.SetTargetBeforeCommand"/>
    /// 为 false）。编号规则是「自己恒为 &lt;1&gt;、其余人依次」，见 <see cref="CollectParty"/>。
    ///
    /// 两条都不成立就返回空串，让上层按「这一轮放弃」处理 —— 宁可不发，也不要红字。
    /// </summary>
    private string BuildGameCommand(Candidate candidate, bool useTargetPlaceholder)
    {
        var format = (Config.GameCommandFormat ?? string.Empty).Trim();
        if (format.Length == 0)
        {
            format = "/共同骑乘 {P}";
        }

        var name = candidate.Name ?? string.Empty;

        string placeholder;

        if (useTargetPlaceholder)
        {
            // 主路径：目标已经切到本人身上了（切的时候读回来确认过），<t> 必然解析到这个人。
            placeholder = "<t>";
        }
        else if (!Config.SetTargetBeforeCommand && candidate.PartyIndex >= 2)
        {
            // 用户关掉了切目标，退回编号写法。
            //
            // 这里要求 >= 2：编号 1 恒为自己（见 CollectParty），
            // 对着自己发 /共同骑乘 <1> 只会换来一句「1号指定的目标名不正确」。
            placeholder = "<" + candidate.PartyIndex + ">";
        }
        else
        {
            // 切目标失败（对象刚好消失、被别的插件抢走）→ 这一轮不发，下一轮再试。
            return string.Empty;
        }

        var index = candidate.PartyIndex >= 1 ? candidate.PartyIndex.ToString() : "?";

        var hadName = format.IndexOf("{0}", StringComparison.Ordinal) >= 0;
        var hadIndex = format.IndexOf("{1}", StringComparison.Ordinal) >= 0;
        var hadPlaceholder = format.IndexOf("{P}", StringComparison.Ordinal) >= 0;

        var command = format.Replace("{0}", name).Replace("{1}", index).Replace("{P}", placeholder);

        // 模板里一个占位符都没留（用户写的是裸「/共同骑乘」）→ 自己补目标。
        if (!hadName && !hadIndex && !hadPlaceholder)
        {
            command = command + " " + placeholder;
        }

        command = command.Trim();

        if (!command.StartsWith('/'))
        {
            command = "/" + command;
        }

        return command;
    }

    private bool PassesFilters(
        IGameObject rider, uint mountId, Vector3 localPos, HashSet<uint> extraIds, bool ignoreSessionRecord)
    {
        if (Config.MaxDistance > 0f &&
            Vector3.Distance(localPos, rider.Position) > Config.MaxDistance)
        {
            return false;
        }

        // 只认多座坐骑。单座坐骑本来就没法带人，往上凑只会白触发一次。
        // 表读不出来时退化成「按手动名单（/pr mount）判定」，名单里没有就一律不触发 ——
        // 宁可不动，也不要对着单座坐骑反复试。
        //
        // 坐骑 ID 读到 0 一样要拒绝。早先这里写的是「0 是没读到、不是单座，一律放行」，
        // 结果是最大的一个漏洞：
        //   队友骑着单人坐骑 → 下坐骑 → 坐骑对象还在原地消散、DataId 读成 0 →
        //   0 被放行 → 对着一个已经没坐骑的人发出 /共同骑乘 → 游戏回红字。
        // 「0 = 不知道」的正确处理不是「那就试试」，而是「不知道就别动」。
        if (mountId == 0)
        {
            if (!this.warnedMountId)
            {
                this.warnedMountId = true;
                this.log.Information("[PillionRider] 读到坐骑 ID=0（读不出来），跳过该候选；用 /pr scan 可以看到是谁");
            }

            return false;
        }

        {
            var multi = MountData.IsMultiSeat(mountId);

            if (multi == null)
            {
                if (!this.warnedMountTable)
                {
                    this.warnedMountTable = true;
                    this.AutoNotifyError("无法读取坐骑数据表，暂按手动名单判定；可用 /pr mount <坐骑ID> 手动标记多座坐骑");
                }

                if (!extraIds.Contains(mountId))
                {
                    return false;
                }
            }
            else if (multi == false && !extraIds.Contains(mountId))
            {
                // 单座坐骑带不了人。这也是「队友骑单人坐骑下坐骑时被误触发」的另一半原因：
                // 坐骑对象在消散途中仍然带着正确的 DataId，只是这台坐骑本来就只有一个座位。
                return false;
            }
        }

        if (!ignoreSessionRecord &&
            Config.OncePerMountSession &&
            this.triggeredSessions.Contains((rider.GameObjectId, mountId)))
        {
            return false;
        }

        // 这个候选最近连续失败过，还在拉黑期里 → 先晾着，别对着它反复撞墙。
        // MaxFailuresPerTarget <= 0 表示用户关掉了这个保护。
        // ignoreSessionRecord 是手动 `/pr now` 走的路子：用户明确要求再试一次，就绕过拉黑。
        if (!ignoreSessionRecord &&
            Config.MaxFailuresPerTarget > 0 &&
            this.failures.TryGetValue((rider.GameObjectId, mountId), out var failRecord) &&
            failRecord.Count >= Config.MaxFailuresPerTarget &&
            Environment.TickCount64 < failRecord.Until)
        {
            return false;
        }

        return true;
    }

    private void Execute(Candidate candidate)
    {
        // 前置守卫：执行途径全关了就没法干活。
        // 放在切目标之前，否则会切了目标再中途返回，把用户的当前目标白白搞掉。
        if (!Config.UseGameCommand && !Config.UseNativeAction)
        {
            this.AutoNotifyError("执行途径全关了，本次跳过（/pr gcmd 开游戏指令，/pr native 开独立模式）");
            return;
        }

        // 再确认一次「这个人现在真的还在坐骑上」。
        //
        // 候选是从上一轮扫描的快照里挑的，而真正发指令之间隔着切目标、拼模板
        // 这些步骤。如果对方在这几十毫秒里下了坐骑（或者下坐骑的是自己，
        // 整张坐骑表都被重算过），发出去的就是一条对空目标的指令 ——
        // 游戏会回一句红字，而角色什么都不会发生。
        var liveMounts = this.CollectMounts();
        if (!liveMounts.TryGetValue(candidate.OwnerId, out var liveMountId) || liveMountId != candidate.MountId)
        {
            this.log.Information(
                "[PillionRider] 候选 {Name} 的坐骑状态已变（快照 {Snapshot}，当前 {Live}），本次不执行",
                candidate.Name,
                candidate.MountId,
                liveMounts.TryGetValue(candidate.OwnerId, out var now2) ? now2.ToString() : "已下坐骑");
            return;
        }

        // 兜底：自己绝不可能骑自己的坐骑。这一条本该由 FindCandidate 挡住，
        // 但「自己」这个判断依赖反射拿到 LocalPlayer，任何一环出问题都可能漏过来，
        // 而漏过来的后果是游戏糊一屏红字 —— 值得在最后一刻再挡一次。
        var localNow = GameApi.GetLocalPlayer(this.objectTable, this.clientState);
        if (localNow != null && candidate.OwnerId == localNow.GameObjectId)
        {
            this.log.Warning("[PillionRider] 候选是自己，已跳过（不可能骑自己的坐骑）");
            return;
        }

        // 小队编号仍然算一遍：它是 /pr scan 里最有用的一条排查线索，
        // 也是用户主动关掉「切目标」时唯一能用的兜底写法。
        // 编号 1 = 自己（见 CollectParty），所以 <2 就等于「算不出一个能对别人用的编号」。
        var partyIndex = this.ResolveCandidatePartyIndex(candidate);
        candidate.PartyIndex = partyIndex;

        if (partyIndex == 1)
        {
            this.log.Warning("[PillionRider] 算出来的编号是 <1>（那是自己），这一轮不发指令");
            return;
        }

        var previousTarget = this.targetManager.Target;
        var targetSwitched = false;

        // 主路径：把目标切到队友身上，然后发 <t>。
        //
        // 为什么主路线是 <t> 而不是 <N>：<N> 的编号规则是「自己恒为 <1>、其余人依次」，
        // 而 IPartyList 的物理顺序不一定如此（现场 B 的 IPartyList 是 [时花, 自己]，
        // 若按位置算就会把时花错算成 <1> = 自己）。<t> 由游戏在执行那一刻解析
        // 「当前选中的人」，不涉及编号语义。
        //
        // 切目标确实有副作用（掉目标、打断操作），所以：
        //   1) 切完立刻读回来确认真的切成了（TrySwitchTarget 里做）；
        //   2) 指令执行完再还原（见下面 targetRestorePending 那段）；
        //   3) 用户可以在设置里关掉这条路（SetTargetBeforeCommand=false），
        //      那时才退回 <N> 编号写法。
        // 独立模式的 UseAction 自带 targetId，任何情况下都不需要切目标。
        if (Config.UseGameCommand &&
            Config.SetTargetBeforeCommand &&
            !Config.UseNativeAction)
        {
            targetSwitched = this.TrySwitchTarget(candidate);
        }

        if (targetSwitched && Config.RestoreTargetAfter)
        {
            // 目标要等指令真的执行完才能还原。
            //
            // 这里不能发完立刻还原：<t> 是游戏在「执行指令那一刻」解析的，
            // 而指令是排队敲进聊天框的，可能排到后面的帧才执行。
            // 早先「发完马上还原目标」的写法会让 <t> 解析成已经换回去的那个目标/空目标，
            // 游戏就回「"<t>"出现问题：1号指定的目标名不正确。」—— 现场那句红字的真正成因。
            // 改成推迟到 0.7 秒后的验证那一步还原（VerifyLastAction / RestoreTargetNow）。
            this.targetRestorePending = true;
            this.targetRestoreTarget = previousTarget;
            this.targetRestoreAtTick = Environment.TickCount64 + 3000;
        }

        // 提前把指令拼出来：拼不出来（既没有效序号、又没切到目标）就趁还没发消息之前退出，
        // 免得白掉一次目标、多打一条日志。
        string prebuiltCommand = null;

        if (Config.UseGameCommand)
        {
            prebuiltCommand = BuildGameCommand(candidate, targetSwitched);

            if (string.IsNullOrEmpty(prebuiltCommand))
            {
                this.log.Warning("[PillionRider] 拼不出有效指令（目标没切过去，且没有可用的队伍序号兜底），本次不执行");
                this.RestoreTargetNow();
                return;
            }
        }

        var ok = true;
        var what = string.Empty;

        if (Config.UseGameCommand)
        {
            // 主力路线：发游戏自带的 /共同骑乘 指令。
            // 这条指令游戏官方就有（玩家手动写宏用的就是它），不需要猜 GeneralAction ID，
            // 自己调 UIModule.ProcessChatBoxEntry 把它敲进聊天框（零第三方依赖），
            // 而且猜 ID 猜错会打开分解窗口（GeneralAction 5）。
            var command = prebuiltCommand ?? BuildGameCommand(candidate, targetSwitched);

            if (string.IsNullOrEmpty(command))
            {
                // BuildGameCommand 只在「目标没切过去、又不许用序号兜底」时返回空串，
                // 而上面已经挡过一次。真走到这里说明状态在这一瞬间又变了 —— 不发，别给游戏送红字。
                this.log.Warning("[PillionRider] 拼不出有效指令（目标没切过去，也没有可用的队伍序号兜底），本次不执行");
                this.RestoreTargetNow();
                return;
            }

            this.log.Information("[PillionRider] 发送游戏指令：{Command}", command);

            // 先把「吞错误」窗口打开：错误提示是游戏执行指令那一刻就产生的，
            // 必须在发出去之前就武装好。窗口只有 2 秒，只拦带「出现问题」这类特征的
            // 聊天消息，其它消息（战况、拾取提示……）一律放行。
            ChatErrorSuppressor.Arm();

            // 关键：这里必须走「模拟聊天框输入」，不能走 ICommandManager.ProcessCommand。
            // ProcessCommand 只查 Dalamud 自己注册过的指令表，/共同骑乘 是游戏原生指令，
            // 它查不到就直接返回 false，游戏那边一个字都没收到 —— 表现就是聊天框一路
            // 打印「已发送游戏指令」而角色纹丝不动。
            // ChatSender 主路径直调 UIModule.ProcessChatBoxEntry（就是 ECommons
            // Chat.ExecuteCommand 内部干的那件事），备用路径才反射 ECommons。
            var sent = ChatSender.TrySend(command, this.log, out var sendError);

            if (sent)
            {
                ok = true;
                what = $"游戏指令 {command}";
            }
            else
            {
                // 两条聊天通道都不通时的退路：仍然试一下 ProcessCommand。
                // 有些版本/环境下 Dalamud 会把未知指令转交出去，能成就是白捡；
                // 成不了也有上面的日志留证据，不会静默失败。
                var dispatched = this.commandManager.ProcessCommand(command);
                this.log.Warning(
                    "[PillionRider] 聊天框通道不可用（{Error}），退化为 ProcessCommand，返回 {Dispatched}",
                    sendError,
                    dispatched);

                // 退化时不能当成功：ProcessCommand 对游戏原生指令基本等于无效操作。
                // 结论交给 700ms 后的验证，这里保持 true 只是为了别把「发送提示」变成
                // 「执行失败」的误导文案 —— 真正的失败判定在 VerifyLastAction 里。
                ok = true;
                what = $"游戏指令 {command}（聊天框通道不可用，已退化派发）";
            }
        }
        else if (Config.UseNativeAction)
        {
            // 次选：直接调游戏的 UseAction（GeneralAction 表里没有「共同骑乘」，
            // 所以这个 ID 只能靠用户用 /pr ga <id> 填，默认不给。）
            var actionId = RidePillionAction.ResolveGeneralActionId(Config.GeneralActionId);

            if (actionId == 0)
            {
                ok = false;
                what = "没有可用的 GeneralAction ID";
                this.log.Warning("[PillionRider] 没有可用的 GeneralAction ID，跳过独立模式");
            }
            else
            {
                ok = RidePillionAction.TryExecute(candidate.OwnerId, actionId, this.log, out var error);
                what = $"游戏动作 GeneralAction={actionId}";

                if (!ok)
                {
                    this.log.Warning("[PillionRider] 独立模式执行失败：{Error}", error);
                }
            }
        }

        if (!ok)
        {
            this.AutoNotifyError($"执行失败：{what}");
        }

        if (ok && Config.OncePerMountSession)
        {
            this.triggeredSessions.Add((candidate.OwnerId, candidate.MountId));
        }

        // 目标还原推迟到验证那一步（VerifyLastAction → RestoreTargetNow）：
        // 现在立刻还原会把「还没执行完的 <t>」打回原形，游戏就回一句目标名不正确。
        if (!ok)
        {
            // 记一次失败：连续失败到上限就先晾一会儿，别每轮都重试。
            this.RestoreTargetNow();
            this.RecordFailure(candidate, what);
            return;
        }

        // 调用成功不代表人真的上去了 —— UseAction 只是把请求交给游戏，
        // 坐骑满了 / 距离不够 / 地形不对时游戏会静默忽略。
        // 所以安排一次延迟验证：半秒后看 RidingPillion 有没有真的亮起来。
        this.ScheduleVerify(candidate);

        // 坐骑 ID 可能是 0（读不到），这时候别在聊天框里写「坐骑 0」，容易以为是 bug。
        var mountText = candidate.MountId == 0 ? "坐骑未知" : $"坐骑 {candidate.MountId}";

        // 游戏指令这条路当场不知道成没成（0.7 秒后才验），所以措辞不能写死「已骑上」。
        var message = Config.UseGameCommand
            ? $"已向 {candidate.Name} 发送 {what}（{mountText}），0.7 秒后回查是否真的坐上"
            : $"已对 {candidate.Name} 执行 {what}（{mountText}）";

        // AutoNotify 自己会把这条写进 /xllog，所以这里不用再补一条 DebugLog。
        // NotifyInChat 是「要不要提示」的总意愿，MuteAutoChat 是「自动提示别进聊天框」的总闸，
        // 两个都满足才真打到聊天框。
        if (Config.NotifyInChat)
        {
            this.AutoNotify(message);
        }
        else
        {
            this.log.Information("[PillionRider] {Message}", message);
        }
    }

    /// <summary>记一次失败。达到上限后写入拉黑截止时间。</summary>
    private void RecordFailure(Candidate candidate, string what)
    {
        var key = (candidate.OwnerId, candidate.MountId);
        this.failures.TryGetValue(key, out var record);

        record.Count++;
        record.LastError = what;

        if (Config.MaxFailuresPerTarget > 0 && record.Count >= Config.MaxFailuresPerTarget)
        {
            record.Until = Environment.TickCount64 + Math.Max(1000, Config.FailureBackoffMs);

            this.AutoNotifyError(
                $"对 {candidate.Name} 已连续失败 {record.Count} 次，先暂停 {Math.Max(1, Config.FailureBackoffMs / 1000)} 秒再试");
        }

        this.failures[key] = record;
    }

    /// <summary>清掉某个候选的失败记录（验证通过时用）。</summary>
    private void ClearFailure(ulong ownerId, uint mountId)
    {
        this.failures.Remove((ownerId, mountId));
    }

    public void ForceTriggerOnce()
    {
        // 单人时自动逻辑是休眠的（Tick 一进来就 return），但手动 `/pr now` 仍然允许跑一遍，
        // 只是提前把「没队伍」这个原因说清楚，省得人对着 /pr scan 的输出猜。
        if (this.partyList.Length == 0)
        {
            this.NotifyError("当前没有队伍（单人状态下插件不工作），先组队再试");
            return;
        }

        // 跨服小队同样不做任何计算 —— 手动触发也不例外。
        // 手动放行只会得到一次注定失败的指令，还要被记成「失败一次」，反而污染记录。
        if (GameApi.IsCrossWorldParty(this.condition, this.partyList, this.objectTable, this.clientState))
        {
            this.NotifyError("当前是跨服小队（跨服组队 / 跨服团队），插件只处理同服队伍，本次跳过");
            return;
        }

        try
        {
            var mountsByOwner = this.CollectMounts();
            var candidate = this.FindCandidate(mountsByOwner, ignoreSessionRecord: true);

            if (candidate == null)
            {
                this.NotifyError("当前没有符合条件的队友");
                return;
            }

            this.lastTriggerTick = Environment.TickCount64;

            // 用户主动要的一次，聊天框必须回话 —— 即使勾了「不向聊天框发送自动提示」。
            this.forceChatThisRound = true;

            try
            {
                this.Execute(candidate.Value);
            }
            finally
            {
                this.forceChatThisRound = false;
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PillionRider] 手动触发时异常");
            this.NotifyError($"触发异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void DumpPartyScan()
    {
        // 这个方法是从设置窗口的按钮直接调进来的，一旦抛异常，
        // 整个窗口会变成「An error occurred while rendering this window」并卡住 ——
        // 现场就是这样：CollectMounts 里 `objectTable[idx]` 拿到 null 槽位后解引用，炸在 UI 线程上。
        // 里面已经逐处判空了，这里再兜一层，保证「扫描出问题」最多是少打几行字，不至于把窗口拆掉。
        try
        {
            this.DumpPartyScanCore();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[PillionRider] 扫描时异常");
            this.NotifyError($"扫描异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DumpPartyScanCore()
    {
        var local = GameApi.GetLocalPlayer(this.objectTable, this.clientState);
        if (local == null)
        {
            this.NotifyError("未登录");
            return;
        }

        this.Notify($"队伍人数: {this.partyList.Length}（{(this.partyList.Length == 0 ? "单人，插件休眠中" : "有队伍，正常工作")}）");
        this.Notify($"副本中: {(GameApi.IsInDuty(this.condition) ? "是（插件会停止工作）" : "否")}　副本条件成员数: {GameApi.DutyFlagCount}");

        // 跨服小队：这是「组着队却完全不动」最常见的原因之一，必须显式打出来。
        var crossWorld = GameApi.IsCrossWorldParty(this.condition, this.partyList, this.objectTable, this.clientState);
        this.Notify($"跨服小队: {(crossWorld ? "是（插件会停止工作，只处理同服队伍）" : "否")}　跨服条件成员数: {GameApi.CrossWorldFlagCount}");

        // 自己此刻的状态：这是「为什么没触发」最常见的原因，
        // 与其让人猜，不如每条都显式打出来。
        var selfMounted = GameApi.HasAnyCondition(this.condition, MountedFlags);
        var selfPillion = GameApi.HasAnyCondition(this.condition, PillionFlags);
        var selfBusy = GameApi.HasAnyCondition(this.condition, BusyFlags);
        var selfCombat = GameApi.HasAnyCondition(this.condition, CombatFlags);

        this.Notify($"自己: 战斗中={Bool(selfCombat)} 已骑乘={Bool(selfMounted)} 已在共同乘骑={Bool(selfPillion)} 忙碌中={Bool(selfBusy)}");
        this.Notify($"条件位可用数: 骑乘 {GameApi.CountResolvableConditions(MountedFlags)}/{MountedFlags.Length}　共同骑乘 {GameApi.CountResolvableConditions(PillionFlags)}/{PillionFlags.Length}　忙碌 {GameApi.CountResolvableConditions(BusyFlags)}/{BusyFlags.Length}");

        if (Config.UseGameCommand)
        {
            this.Notify($"执行方式: 游戏指令 {Config.GameCommandFormat}（{{P}}=目标占位符 {{0}}=队友名 {{1}}=队伍序号）");
            this.Notify("目标写法: 优先切目标 + <t>（切完读回来确认）；关掉切目标时才退队伍序号 <N> 兜底");
            this.Notify($"聊天框通道: {ChatSender.ChannelName}　{ChatSender.StatusNote}");
            this.Notify($"错误提示拦截: {(ChatErrorSuppressor.IsInstalled ? "已启用" : "未启用")}　已拦截 {ChatErrorSuppressor.SuppressedCount} 条");
            this.Notify($"　拦截状态: {ChatErrorSuppressor.StatusNote}");
        }
        else if (Config.UseNativeAction)
        {
            var gaId = RidePillionAction.ResolveGeneralActionId(Config.GeneralActionId);
            var gaText = gaId == 0
                ? "未指定（独立模式不会执行，用 /pr ga <id> 填）"
                : gaId.ToString();
            this.Notify($"执行方式: 独立（游戏动作 GeneralAction={gaText}）　探测: {RidePillionAction.DiscoveryNote}");
        }
        else
        {
            this.Notify("执行方式: 全关，不会执行（/pr gcmd 开游戏指令）");
        }

        var mounts = this.CollectMounts();
        if (mounts.Count == 0)
        {
            this.Notify("没有从任何角色身上读到坐骑");
        }

        // 队伍编号：自己恒为 <1>，其余人按 IPartyList 顺序依次 <2>、<3>……
        // 把整份列表连原始位置一起打出来 —— 排查时能看到「IPartyList 顺序」和「游戏编号」
        // 是两套东西，不用再猜。
        var party = this.CollectParty();
        var selfText = party.ContainsSelf
            ? $"自己 = <1>（在 IPartyList 里排第 {party.SelfRawSlot} 位）"
            : "自己不在 IPartyList 里（编号退化为按下标 +1）";
        this.Notify($"队伍编号: {selfText}");

        for (var i = 0; i < this.partyList.Length; i++)
        {
            var member = this.partyList[i];
            if (member == null)
            {
                continue;
            }

            var memberName = member.Name != null ? member.Name.ToString() : "(无名字)";

            var gameSlot = 0;
            if (!string.IsNullOrEmpty(memberName))
            {
                party.NameToIndex.TryGetValue(memberName, out gameSlot);
            }

            var slotText = gameSlot >= 1 ? $"<{gameSlot}>" : "<?>";
            var selfMark = party.ContainsSelf && i + 1 == party.SelfRawSlot ? "（自己）" : string.Empty;

            this.Notify($"队伍列表[{i}] = {slotText} {memberName}{selfMark} EntityId={member.EntityId}");
        }

        var playerIndex = 0;

        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var obj = this.objectTable[idx];
            if (obj == null)
            {
                continue;
            }

            if (!GameApi.IsPlayer(obj))
            {
                continue;
            }

            var name = obj.Name != null ? obj.Name.ToString() : "(无名字)";
            var goId = obj.GameObjectId;

            var mountText = "无";
            if (mounts.TryGetValue(goId, out var mountId))
            {
                var multi = MountData.IsMultiSeat(mountId);
                var multiText = multi == true ? "多座" : multi == false ? "单座" : "未知";
                mountText = $"{mountId}（{multiText}）";
            }
            else if (GameApi.TryGetRiderMountId(obj, out var rawId))
            {
                mountText = $"CurrentMount={rawId}（未进候选表）";
            }

            var partyText = IsPartyMember(party, obj) ? "队" : "-";

            this.Notify($"玩家#{playerIndex} {name} 骑乘={mountText} 队伍={partyText} goId={goId}");
            playerIndex++;
        }

        // 原始坐骑对象也打一份：DataId 和 OwnerId 是排查「反查不到骑手」的关键。
        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var obj = this.objectTable[idx];
            if (obj == null)
            {
                continue;
            }

            if (!GameApi.IsMount(obj))
            {
                continue;
            }

            var mountId = GameApi.GetDataId(obj);
            var multi = MountData.IsMultiSeat(mountId);
            var multiText = multi == true ? "多座" : multi == false ? "单座" : "未知";

            this.Notify($"坐骑对象: DataId={mountId} OwnerId={obj.OwnerId} OwnerIsLocal={Bool(GameApi.IsLocalObjectId(obj.OwnerId))} 座位判定={multiText}");
        }

        this.DumpKinds();
    }

    /// <summary>把 true/false 打成 是/否，聊天框里读着顺眼些。</summary>
    private static string Bool(bool value) => value ? "是" : "否";

    /// <summary>
    /// 打印对象表里出现过的 ObjectKind 名字与数量。
    /// 这是「宽松匹配对不上」时的唯一可靠排查手段：把这里打印出的真实枚举名
    /// 拿去和 <c>GameApi.IsMount</c> / <c>IsPlayer</c> 的匹配规则对照即可。
    /// </summary>
    public void DumpKinds()
    {
        var counts = new Dictionary<string, int>();

        for (var idx = 0; idx < this.objectTable.Length; idx++)
        {
            var obj = this.objectTable[idx];
            var kind = GameApi.GetKindName(obj) ?? "(null)";
            counts.TryGetValue(kind, out var n);
            counts[kind] = n + 1;
        }

        if (counts.Count == 0)
        {
            this.Notify("对象表为空");
            return;
        }

        var parts = new List<string>();
        foreach (var pair in counts)
        {
            parts.Add($"{pair.Key}={pair.Value}");
        }

        this.Notify($"对象类型统计: {string.Join(", ", parts)}");
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (head)
        {
            case "":
                this.ToggleConfigWindow();
                break;

            case "on":
                Config.Enabled = true;
                Config.Save();
                this.RefreshDtrBar();
                this.Notify("已启用");
                break;

            case "off":
                Config.Enabled = false;
                Config.Save();
                this.RefreshDtrBar();
                this.Notify("已停用");
                break;

            case "toggle":
                this.ToggleEnabled();
                break;

            case "now":
                this.ForceTriggerOnce();
                break;

            case "scan":
                this.DumpPartyScan();
                break;

            case "kinds":
                this.DumpKinds();
                break;

            case "chat":
                // 排查用：聊天框通道到底在不在、走的哪条路。
                // 插件加载顺序不确定，ECommons 可能晚于我们加载，所以这里顺手重置再探一次。
                ChatSender.Reset();
                ChatSender.RefreshStatus(this.log);
                this.Notify($"聊天框通道: {(ChatSender.IsAvailable ? "可用" : "不可用")}　通道={ChatSender.ChannelName}　{ChatSender.StatusNote}");
                break;

            case "chatevents":
                // 排查用：这个 Dalamud 版本里聊天框接口到底长什么样。
                // 国服 dev 目录一个 .xml 都没有，事件名/签名查不到 —— 只能现场问运行时。
                // 输出同时写进 /xllog（聊天框太长时以日志为准）。
                var dump = ChatErrorSuppressor.DescribeChatGui(this.chatGui);

                this.log.Information("[PillionRider] 聊天框接口自省:\n{Dump}", dump);

                foreach (var line in dump.Split('\n'))
                {
                    var trimmed = line.Trim();

                    if (trimmed.Length > 0)
                    {
                        this.Notify(trimmed);
                    }
                }

                break;

            case "quiet":
                // 聊天框干不干净，一句话就能切。
                // 注意这个回复本身走 Notify（不是 AutoNotify）—— 是你主动问的，必须回。
                Config.MuteAutoChat = !Config.MuteAutoChat;
                Config.Save();
                this.Notify(Config.MuteAutoChat
                    ? "已静音：插件不再自己往聊天框打提示（/pr 命令的回复照常显示，日志照常写）"
                    : "已恢复：插件会自动往聊天框打提示");
                break;

            case "gcmd":
                // 主力路线的总开关：发游戏自带的 /共同骑乘 指令
                Config.UseGameCommand = !Config.UseGameCommand;
                if (Config.UseGameCommand)
                {
                    // 打开它就没必要再走猜 ID 的独立模式，顺手关掉，免得失败时又去点分解。
                    Config.UseNativeAction = false;
                }

                Config.Save();
                this.Notify(Config.UseGameCommand
                    ? $"已启用游戏指令路线：{Config.GameCommandFormat}"
                    : "已关闭游戏指令路线（若独立模式也没开，插件不会执行任何动作，只是空转）");
                break;

            case "gcmdformat":
                if (rest.Length == 0)
                {
                    this.Notify($"当前游戏指令模板: {Config.GameCommandFormat}（{{P}} = 目标占位符，{{0}} = 队友名，{{1}} = 队伍序号）");
                }
                else
                {
                    Config.GameCommandFormat = rest;
                    Config.UseGameCommand = true;
                    Config.UseNativeAction = false;
                    Config.Save();
                    this.Notify($"游戏指令模板已设为: {rest}（已启用游戏指令路线）");
                }

                break;

            case "native":
                Config.UseNativeAction = !Config.UseNativeAction;

                // 独立模式和游戏指令互斥：两者同时开，指令失败时会回退去点动作，
                // 而 GeneralAction 表里没有「共同骑乘」，只会点出别的东西（比如分解）。
                if (Config.UseNativeAction)
                {
                    Config.UseGameCommand = false;
                }
                else if (!Config.UseGameCommand)
                {
                    Config.UseGameCommand = true;
                }

                Config.Save();
                this.Notify(Config.UseNativeAction
                    ? "已切到独立模式（自己调用 UseAction，需要 /pr ga <id> 填动作 ID）"
                    : $"已切回游戏指令（{Config.GameCommandFormat}）");
                break;

            case "ga":
                if (rest.Length == 0)
                {
                    var current = RidePillionAction.ResolveGeneralActionId(Config.GeneralActionId);
                    this.Notify($"当前 GeneralAction ID: {current}（{RidePillionAction.DiscoveryNote}）");
                }
                else if (uint.TryParse(rest, out var gaId))
                {
                    Config.GeneralActionId = gaId;
                    Config.Save();
                    this.Notify($"GeneralAction ID 已设为: {gaId}");
                }
                else
                {
                    this.NotifyError("用法: /pr ga <数字ID>");
                }

                break;

            case "redisc":
                RidePillionAction.ResetDiscovery();
                RidePillionAction.Discover(this.dataManager, this.log);
                this.Notify($"已重新探测：{RidePillionAction.DiscoveryNote}");
                break;

            case "galist":
                // 自动探测靠名字匹配，名字对不上就落回兜底的 5。
                // 这时候得有人在完整表里挑一行，所以把全表倒进日志（/xllog 搜 GeneralAction）。
                RidePillionAction.DumpRows(this.dataManager, this.log);
                this.Notify("已把 GeneralAction 全表写入日志，打开 /xllog 搜 “GeneralAction” 查看；找到合适的行后用 /pr ga <id> 填进去");
                break;

            case "mount":
                if (uint.TryParse(rest, out var id))
                {
                    Config.AddExtraMountId(id);
                    this.Notify($"已标记多座坐骑: {id}");
                }
                else
                {
                    this.NotifyError("用法: /pr mount <坐骑ID>");
                }

                break;

            case "mountinfo":
                // 排查用：直接查某个坐骑 ID 在表里的 ExtraSeats 原始值。
                // 「胖莫古力 = 84，ExtraSeats=1」这种事实在游戏外能查到，
                // 但在游戏里能自己确认一遍会省掉很多来回。
                if (!uint.TryParse(rest, out var infoId))
                {
                    this.NotifyError("用法: /pr mountinfo <坐骑ID>（例如胖莫古力是 84）");
                    break;
                }

                var seats = MountData.GetExtraSeats(infoId);
                var seatsText = seats == null
                    ? $"读不到（{this.MountDataFailureReason}）"
                    : $"{seats.Value}（{(seats.Value > 0 ? "多座" : "单座")}）";
                this.Notify($"坐骑 {infoId}: ExtraSeats={seatsText}");
                break;

            case "clearmount":
                Config.ExtraMountIds = string.Empty;
                Config.Save();
                this.Notify("已清空手动标记");
                break;

            default:
                this.NotifyError($"未知参数: {head}");
                break;
        }
    }
}
