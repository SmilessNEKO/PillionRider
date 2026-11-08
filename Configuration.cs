using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace PillionRider;

/// <summary>
/// 插件配置。
///
/// 这里只保留<b>用户确实需要调</b>的东西：总开关、执行方式、距离、轮询/冷却、失败退避、提示。
/// 「脱战才触发」「副本里不工作」「自己已在坐骑上不触发」「自己已在共同骑乘不触发」
/// 「施法/交互/过场不触发」「仅多座坐骑」「不碰队伍外路人」这七条是插件的<b>安全前提</b>，
/// 关掉任何一条都只会让它更容易做错事（副本里乱骑、战斗中乱动、骑上单座坐骑），
/// 所以它们被固定在 <see cref="Plugin"/> 里，既不出现在设置窗口，也没有任何指令能改。
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// 配置结构版本。<b>1</b> = 早期默认值（30 / 500 / 8000 / 3 / 60000），
    /// <b>2</b> = 现行默认值（5 / 260 / 1000 / 8 / 5000）。
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>总开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 是否优先用<b>游戏自带的文本指令</b>执行（默认 <c>true</c>）。
    ///
    /// 游戏自己就有 <c>/共同骑乘 &lt;队友&gt;</c> 这个指令（国际服是 <c>/ridepillion</c>），
    /// 玩家手动写宏就是这个：<c>/共同骑乘 &lt;2&gt;1</c>。既然游戏官方支持，
    /// 就没必要去猜 GeneralAction 的 ID —— 那个表里根本没有「共同骑乘」这一行，
    /// 猜错的后果是打开分解窗口（GeneralAction 5 = Desynthesis）。
    /// </summary>
    public bool UseGameCommand { get; set; } = true;

    /// <summary>
    /// 走游戏指令时的模板。
    ///
    /// 三个占位符：<c>{0}</c> = 队友名字、<c>{1}</c> = 小队编号（自己恒为 1，其余人依次 2、3…）、
    /// <c>{P}</c> = 游戏认的目标写法（切目标成功时是 <c>&lt;t&gt;</c>，关掉切目标时才退 <c>&lt;N&gt;</c>）。
    ///
    /// 默认 <c>/共同骑乘 {P}</c>。国服指令是 <c>/共同骑乘</c>，国际服要改成
    /// <c>/ridepillion {P}</c>。
    ///
    /// <b>为什么默认用 <c>{P}</c> 而不是名字</b>：游戏文档里这个参数叫 <c>[placeholder]</c>，
    /// 只认 <c>&lt;t&gt;</c> / <c>&lt;target&gt;</c> / <c>&lt;1&gt;-&lt;8&gt;</c> 这几种写法。
    /// 直接塞玩家名字游戏未必认（名字里有空格时更悬）。
    /// </summary>
    public string GameCommandFormat { get; set; } = "/共同骑乘 {P}";

    /// <summary>
    /// 是否走独立模式（插件自己调 <c>ActionManager.UseAction</c> 发通用动作）。
    ///
    /// 默认 <c>false</c>：<c>GeneralAction</c> 表里没有「共同骑乘」这一行（整表 44 行已逐条确认），
    /// 唯一能用的 ID 得靠用户自己查出来填，所以这条路降级为实验选项。
    /// 主力路线是 <see cref="UseGameCommand"/>。
    /// </summary>
    public bool UseNativeAction { get; set; }

    /// <summary>
    /// 独立模式下使用的 GeneralAction ID。<c>0</c> = <b>不执行</b>（不是「自动探测」）。
    /// 用 /pr ga &lt;id&gt; 设置。
    /// 注意：GeneralAction 表里<b>没有</b>「共同骑乘」这一行，这个 ID 只能靠你自己填。
    /// </summary>
    public uint GeneralActionId { get; set; }

    /// <summary>额外认定多座的坐骑 ID（逗号分隔，写在配置里，用 /pr mount &lt;id&gt; 追加）。</summary>
    public string ExtraMountIds { get; set; } = string.Empty;

    /// <summary>最大触发距离（米）。0 = 不限制。</summary>
    public float MaxDistance { get; set; } = 5f;

    /// <summary>轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 260;

    /// <summary>两次触发之间的最小冷却（毫秒）。</summary>
    public int CooldownMs { get; set; } = 1000;

    /// <summary>
    /// 同一个候选连续失败几次后，暂时不再试它（避免对着一个上不去的坐骑反复触发）。
    /// 0 = 不限制。
    /// </summary>
    public int MaxFailuresPerTarget { get; set; } = 8;

    /// <summary>失败后拉黑多久（毫秒）。</summary>
    public int FailureBackoffMs { get; set; } = 5000;

    /// <summary>
    /// 执行前先把目标切到该队友，然后用 <c>&lt;t&gt;</c> 发指令（默认 <c>true</c>，主路线）。
    ///
    /// <b>为什么默认走这条路</b>：<c>&lt;N&gt;</c> 编号靠推导 —— 规则是「自己恒为
    /// <c>&lt;1&gt;</c>、其余人依次 <c>&lt;2&gt;</c>、<c>&lt;3&gt;</c>…」（副本外队伍第一位
    /// 只会是自己），而 <c>IPartyList</c> 的物理顺序不一定如此。推导一旦出偏差，
    /// 就会出现「发出去的是 <c>&lt;1&gt;</c>、而那其实是我自己」，游戏回
    /// 「1号指定的目标名不正确」。
    /// <c>&lt;t&gt;</c> 是游戏在指令执行那一刻解析「当前选中的人」，不涉及编号语义。
    ///
    /// 关掉它就退回 <c>&lt;N&gt;</c> 编号写法（插件会挡掉 <c>&lt;1&gt;</c>）。
    /// 独立模式下不是必须的（动作自带 targetId），但保持与指令路线一致的行为。
    /// </summary>
    public bool SetTargetBeforeCommand { get; set; } = true;

    /// <summary>执行完指令后把目标还原。</summary>
    public bool RestoreTargetAfter { get; set; } = true;

    /// <summary>同一个队友在不下坐骑的情况下只触发一次。</summary>
    public bool OncePerMountSession { get; set; } = true;

    /// <summary>
    /// 不向聊天框发送<b>自动</b>提示（默认 <c>true</c>）。
    ///
    /// 勾上之后插件自己不再往聊天框打任何东西 —— 「已向 XX 发送游戏指令…」
    /// 「对 XX 已连续失败 N 次」「无法读取坐骑数据表」这类全部静默。
    /// <b>你主动发 <c>/pr</c> 命令时的回复照常显示</b>（扫描结果、设置确认、错误提示），
    /// 因为那是你自己问的。
    ///
    /// 所有自动提示仍然会写进 <c>/xllog</c>，排查时不受影响。
    /// 用 <c>/pr quiet</c> 可以随时切换。
    /// </summary>
    public bool MuteAutoChat { get; set; } = true;

    /// <summary>触发时在聊天框提示。受 <see cref="MuteAutoChat"/> 总闸控制。</summary>
    public bool NotifyInChat { get; set; } = true;

    /// <summary>输出调试日志。</summary>
    public bool DebugLog { get; set; }

    private static readonly char[] IdSeparators = { ',', ' ', ';', '|', '\n', '\r', '\t' };

    public HashSet<uint> GetExtraMountIdSet()
    {
        var set = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(this.ExtraMountIds))
        {
            return set;
        }

        foreach (var piece in this.ExtraMountIds.Split(IdSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(piece.Trim(), out var id))
            {
                set.Add(id);
            }
        }

        return set;
    }

    public void AddExtraMountId(uint id)
    {
        var set = this.GetExtraMountIdSet();
        set.Add(id);
        this.ExtraMountIds = string.Join(",", set);
        this.Save();
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    /// <summary>
    /// 把旧版本配置升到当前版本。返回 <c>true</c> 表示改过东西、调用方应当存盘。
    ///
    /// <para>
    /// <b>为什么要做这一步</b>：默认值改了（30→5 / 500→260 / 8000→1000 / 3→8 / 60000→5000），
    /// 但已有用户的配置文件里躺着的是旧默认值 —— 反序列化会把它们<b>原样读回来</b>，
    /// 于是「新默认值」对老用户完全不生效，他们会觉得这版改了个寂寞。
    /// </para>
    ///
    /// <para>
    /// <b>只搬那些「还是旧默认值」的字段</b>：值等于旧默认值，说明用户从来没动过它，
    /// 换成新默认值是安全的；值不等于旧默认值，说明是用户自己定的，一个字都不碰。
    /// </para>
    /// </summary>
    public bool Migrate()
    {
        if (this.Version >= CurrentVersion)
        {
            return false;
        }

        // v1 → v2：只替换「仍是旧默认值」的字段。
        if (Math.Abs(this.MaxDistance - 30f) < 0.001f)
        {
            this.MaxDistance = 5f;
        }

        if (this.PollIntervalMs == 500)
        {
            this.PollIntervalMs = 260;
        }

        if (this.CooldownMs == 8000)
        {
            this.CooldownMs = 1000;
        }

        if (this.MaxFailuresPerTarget == 3)
        {
            this.MaxFailuresPerTarget = 8;
        }

        if (this.FailureBackoffMs == 60000)
        {
            this.FailureBackoffMs = 5000;
        }

        this.Version = CurrentVersion;
        return true;
    }
}
