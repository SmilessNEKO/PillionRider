using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;
#if NATIVE_ACTION
using FFXIVClientStructs.FFXIV.Client.Game;
#endif

namespace PillionRider;

/// <summary>
/// 独立模式：直接调 <c>ActionManager.UseAction</c> 执行「共同骑乘」。
///
/// 游戏里「骑上队友的多人坐骑」是一个 <b>GeneralAction</b>（通用动作），
/// 不是普通技能也不是物品。它通过 <c>ActionManager.UseAction</c> 触发：
///
/// <code>
/// UseAction(ActionType.GeneralAction, generalActionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted)
/// </code>
///
/// 其中 <c>targetId</c> 是<b>骑手（队友）的 GameObjectId</b>，不是坐骑的 ID ——
/// 游戏会自己按目标找他的坐骑并把你放上去。
///
/// <para>
/// 为什么不用 <c>ICharacter.CurrentMount</c> 之类的高层 API：Dalamud 没有暴露
/// 「执行动作」的高层封装，动作执行一直属于 FFXIVClientStructs 的地盘。
/// </para>
///
/// <para>
/// <b>关于 generalActionId</b>：这条路线<b>不是</b>主力路线。实测国服/国际服的
/// <c>GeneralAction</c> 表里根本没有「共同骑乘」这一行（整表 44 行，全是 Auto-attack /
/// Jump / Sprint / Desynthesis / Repair / Teleport / Mount Roulette 这类），
/// 而这张表的第 5 行是 <b>Desynthesis（分解）</b> —— 拿 5 当兜底会直接打开分解窗口。
/// 所以默认兜底值是 <b>0（不执行）</b>，只有在用户用 <c>/pr ga &lt;id&gt;</c> 明确指定时才会发。
/// 真正靠谱的路线是发游戏自带的文本指令 <c>/共同骑乘</c>（见 <see cref="Plugin"/> 的 UseGameCommand）。
/// </para>
/// </summary>
internal static class RidePillionAction
{
    /// <summary>
    /// 兜底的 GeneralAction ID。<b>0 表示「不知道，不要执行」</b>。
    ///
    /// 曾经这里是 5，注释还写着「社区长期使用的 Ride Pillion ID」—— 那是错的：
    /// 5 在 GeneralAction 表里是 Desynthesis（分解），实测直接打开了分解道具窗口。
    /// </summary>
    public const uint FallbackGeneralActionId = 0;

    /// <summary>已经成功找到的行 ID；0 表示还没找到。</summary>
    private static uint discoveredId;

    /// <summary>探测是否已经得出结论（找到行，或已放弃）。</summary>
    private static bool discoveryDone;

    /// <summary>探测次数上限，防止 Excel 一直没就绪时无限重试。</summary>
    private const int MaxDiscoveryAttempts = 60;

    private static int discoveryAttempts;

    /// <summary>发现过程的说明，给 /pr scan 和设置窗口显示。</summary>
    public static string DiscoveryNote { get; private set; } = "尚未探测";

    /// <summary>
    /// 尝试从 GeneralAction 表里找出「共同骑乘」的行 ID。
    /// 失败不抛异常，只写 DiscoveryNote。
    /// </summary>
    public static void Discover(IDataManager dataManager, IPluginLog log)
    {
        // 找到就收工；没找到则继续重试，直到超过次数上限。
        // 插件加载瞬间 Excel 表可能还没就绪，所以不能一失败就永久放弃。
        if (discoveryDone || discoveredId != 0 || discoveryAttempts >= MaxDiscoveryAttempts)
        {
            return;
        }

        discoveryAttempts++;

        try
        {
            var rowType = FindType("Lumina.Excel.Sheets.GeneralAction");
            if (rowType == null)
            {
                DiscoveryNote = "找不到 GeneralAction 表，用配置值";
                return;
            }

            var nameMember = FindNameMember(rowType);
            if (nameMember == null)
            {
                DiscoveryNote = "GeneralAction 行里没有名字列，用配置值";
                discoveryDone = true;
                return;
            }

            var sheet = OpenSheet(dataManager, rowType);
            if (sheet == null)
            {
                DiscoveryNote = "GeneralAction 表打开失败，用配置值";
                return;
            }

            var readRow = BuildRowReader(sheet, rowType);
            if (readRow == null)
            {
                DiscoveryNote = "GeneralAction 表没有可用的取行方法，用配置值";
                discoveryDone = true;
                return;
            }

            // GeneralAction 表很小，但行号不一定连续，扫的范围按表的行数来，
            // 再保底扫到 256。
            var limit = (uint)Math.Max(256, GetSheetRowCount(sheet));

            var hits = new List<string>();
            var firstName = string.Empty;

            for (uint id = 0; id < limit; id++)
            {
                object row;
                try
                {
                    row = readRow(id);
                }
                catch
                {
                    break;
                }

                if (row == null)
                {
                    continue;
                }

                var text = ReadName(nameMember, row);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (IsRidePillionName(text))
                {
                    hits.Add($"{id}:{text}");

                    // 多行匹配时保留第一行作为默认，剩下的让用户自己挑。
                    if (discoveredId == 0)
                    {
                        discoveredId = id;
                        firstName = text;
                        log.Information("[PillionRider] Ride Pillion GeneralAction ID = {Id}（{Name}）", id, text);
                    }
                }
            }

            if (hits.Count > 1)
            {
                // 表里有多行都像是「共同骑乘」，把候选都列出来让人自己挑。
                DiscoveryNote = $"表里有多行匹配（{string.Join(" / ", hits)}），默认用 {discoveredId}；可用 /pr ga <id> 指定";
                log.Warning("[PillionRider] GeneralAction 表里有多行匹配：{Hits}", string.Join(" / ", hits));
            }
            else if (hits.Count == 1)
            {
                DiscoveryNote = $"从表里找到 ID={discoveredId}（{firstName}）";
            }
            else
            {
                discoveredId = 0;
                DiscoveryNote = $"表里没找到「共同骑乘」行，用配置值；可用 /pr galist 看全表再用 /pr ga <id> 指定";
            }

            // 表能打开、行也能读，就是结论已定（哪怕一行没匹配上）。
            discoveryDone = true;
        }
        catch (Exception ex)
        {
            DiscoveryNote = "探测异常，用配置值：" + ex.Message;
        }
    }

    /// <summary>
    /// 把 <c>GeneralAction</c> 表里所有读得出名字的行都写进日志（<c>/xllog</c> 里搜 GeneralAction）。
    ///
    /// 存在的意义：自动探测靠名字匹配，万一这一版的名字既不含「共乘」也不含 pillion，
    /// 自动探测就会落回兜底的 5。这时候需要人眼在完整表里找一行真正对的 ID，
    /// 再用 <c>/pr ga &lt;id&gt;</c> 填进去。只发聊天框会被长度截断，所以走日志。
    /// </summary>
    public static void DumpRows(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            var rowType = FindType("Lumina.Excel.Sheets.GeneralAction");
            if (rowType == null)
            {
                log.Warning("[PillionRider] 找不到 Lumina.Excel.Sheets.GeneralAction（这一版可能没有这张表）");
                return;
            }

            var nameMember = FindNameMember(rowType);
            if (nameMember == null)
            {
                log.Warning("[PillionRider] GeneralAction 行里没有 Name / Text / NameString 列");
                return;
            }

            var sheet = OpenSheet(dataManager, rowType);
            if (sheet == null)
            {
                log.Warning("[PillionRider] GeneralAction 表打开失败（Excel 可能还没就绪，进游戏后再试）");
                return;
            }

            var readRow = BuildRowReader(sheet, rowType);
            if (readRow == null)
            {
                log.Warning("[PillionRider] GeneralAction 表没有可用的取行方法");
                return;
            }

            var limit = (uint)Math.Max(256, GetSheetRowCount(sheet));
            var count = 0;

            for (uint id = 0; id < limit; id++)
            {
                object row;
                try
                {
                    row = readRow(id);
                }
                catch
                {
                    break;
                }

                if (row == null)
                {
                    continue;
                }

                var text = ReadName(nameMember, row);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                count++;
                log.Information("[PillionRider] GeneralAction {Id} = {Name}", id, text);
            }

            log.Information("[PillionRider] GeneralAction 表列出完毕：{Count} 行（扫描上限 {Limit}）", count, limit);
        }
        catch (Exception ex)
        {
            log.Warning("[PillionRider] 列出 GeneralAction 表失败：{Message}", ex.Message);
        }
    }

    /// <summary>
    /// 清掉探测结论，下次 <see cref="Discover"/> 重新来一遍。
    /// 供 <c>/pr redisc</c> 用——比如换了地图后表才就绪。
    /// </summary>
    public static void ResetDiscovery()
    {
        discoveryDone = false;
        discoveredId = 0;
        discoveryAttempts = 0;
        DiscoveryNote = "尚未探测";
    }

    /// <summary>最终使用的 GeneralAction ID：手动配置优先，其次表里探测的，最后兜底值。</summary>
    public static uint ResolveGeneralActionId(uint configured)
    {
        // 手动填的优先级最高——探测表里的名字匹配可能认错行，
        // 但用户自己抄一个确定的 ID 不会错。
        if (configured != 0)
        {
            return configured;
        }

        if (discoveredId != 0)
        {
            return discoveredId;
        }

        return FallbackGeneralActionId;
    }

    /// <summary>
    /// 真正执行「骑上 targetId 的坐骑」。成功返回 true。
    /// <paramref name="targetId"/> 是<b>骑手</b>的 GameObjectId。
    /// </summary>
#if NATIVE_ACTION
    public static unsafe bool TryExecute(ulong targetId, uint generalActionId, IPluginLog log, out string error)
    {
        error = null;

        if (generalActionId == 0)
        {
            // 0 的意思是「不知道用哪个动作」，不是「用兜底」。
            // 以前这里会退回 FallbackGeneralActionId（那时是 5 = 分解），
            // 结果插件去点了分解道具 —— 宁可什么都不做，也不要发一个猜出来的动作。
            error = "没有可用的 GeneralAction ID：GeneralAction 表里没有「共同骑乘」这一行，请用 /pr ga <id> 指定，或改用游戏指令（/pr gcmd）";
            return false;
        }

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
            {
                error = "ActionManager 实例为空（可能还没进游戏）";
                return false;
            }

            // 参数顺序：动作类型、动作 ID、目标 ID、extraParam、执行模式、连招 ID、范围选择输出。
            // - 动作类型用运行时解析，不写死枚举成员名（跨版本可能改名）。
            // - extraParam 只对物品有意义，动作用 0。
            // - 执行模式用 default（= 0），不写具名成员，同理。
            // - 最后一个 bool* 传 null，表示不关心「是否被转成范围选择模式」。
            manager->UseAction(
                ResolveGeneralActionType(),
                generalActionId,
                targetId,
                0,
                default,
                0,
                null);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ActionType generalActionType;
    private static bool generalActionTypeResolved;

    /// <summary>
    /// 取 <c>ActionType</c> 里代表「通用动作」的那个值。
    ///
    /// 不写 <c>ActionType.GeneralAction</c>：这个枚举的成员名在 FFXIVClientStructs 里
    /// 没有 XML 文档，无法在编译前确认它到底叫什么（也确认不了有没有被改过名）。
    /// 按名字找，找不到再退数字。
    /// </summary>
    private static ActionType ResolveGeneralActionType()
    {
        if (generalActionTypeResolved)
        {
            return generalActionType;
        }

        generalActionTypeResolved = true;

        try
        {
            foreach (var name in Enum.GetNames(typeof(ActionType)))
            {
                if (name.IndexOf("GeneralAction", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    generalActionType = (ActionType)Enum.Parse(typeof(ActionType), name);
                    return generalActionType;
                }
            }
        }
        catch
        {
            // 落到下面的数字兜底
        }

        // 游戏内部 ActionType 的排列里，通用动作长期是 5。
        generalActionType = (ActionType)5;
        return generalActionType;
    }
#else
    /// <summary>
    /// 编译时没拿到 FFXIVClientStructs.dll，独立模式不可用。
    /// 这里保留同名签名，让上层代码不用加条件编译。
    /// </summary>
    public static bool TryExecute(ulong targetId, uint generalActionId, IPluginLog log, out string error)
    {
        error = "构建时未找到 FFXIVClientStructs.dll，独立模式不可用；请改用命令模式（/pr cmd）";
        return false;
    }
#endif

    // ---------------------------------------------------------------- 反射小工具

    private static bool IsRidePillionName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();

        // 英文客户端 / 数据表英文名
        if (lower.Contains("pillion"))
        {
            return true;
        }

        // 中文客户端（国服）。只认「共同骑乘 / 共乘」，不用「搭乘」——
        // 那个词太泛，会误命中别的通用动作。
        if (text.Contains("共同骑乘") || text.Contains("共乘"))
        {
            return true;
        }

        // 日文客户端
        return text.Contains("相乗");
    }

    private static MemberInfo FindNameMember(Type rowType)
    {
        foreach (var name in new[] { "Name", "Text", "NameString" })
        {
            var property = rowType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                return property;
            }

            var field = rowType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                return field;
            }
        }

        return null;
    }

    private static string ReadName(MemberInfo member, object row)
    {
        object value;
        try
        {
            value = member is PropertyInfo property ? property.GetValue(row) : ((FieldInfo)member).GetValue(row);
        }
        catch
        {
            return null;
        }

        if (value == null)
        {
            return null;
        }

        // Lumina 的文本列可能是 SeString / ReadOnlySeString / 普通 string，统一走 ToString()
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static object OpenSheet(IDataManager dataManager, Type rowType)
    {
        foreach (var method in typeof(IDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "GetExcelSheet" ||
                !method.IsGenericMethodDefinition ||
                method.GetGenericArguments().Length != 1)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 2 || parameters[1].ParameterType != typeof(string))
            {
                continue;
            }

            try
            {
                return method.MakeGenericMethod(rowType).Invoke(dataManager, new object[] { null, null });
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static Func<uint, object> BuildRowReader(object sheet, Type rowType)
    {
        var sheetType = sheet.GetType();

        var getRow = sheetType.GetMethod("GetRowOrDefault", new[] { typeof(uint) })
                     ?? sheetType.GetMethod("GetRow", new[] { typeof(uint) });

        if (getRow != null)
        {
            return id =>
            {
                try
                {
                    return getRow.Invoke(sheet, new object[] { id });
                }
                catch
                {
                    return null;
                }
            };
        }

        var tryGetRow = sheetType.GetMethod("TryGetRow", new[] { typeof(uint), rowType.MakeByRefType() });
        if (tryGetRow != null)
        {
            return id =>
            {
                var args = new object[] { id, null };
                try
                {
                    return tryGetRow.Invoke(sheet, args) is bool ok && ok ? args[1] : null;
                }
                catch
                {
                    return null;
                }
            };
        }

        var indexer = sheetType.GetProperty("Item", new[] { typeof(uint) });
        if (indexer != null)
        {
            return id =>
            {
                try
                {
                    return indexer.GetValue(sheet, new object[] { id });
                }
                catch
                {
                    return null;
                }
            };
        }

        return null;
    }

    /// <summary>
    /// 尽力取表的行数；取不到就返回 0，让调用方用保底值。
    /// </summary>
    private static int GetSheetRowCount(object sheet)
    {
        try
        {
            var countProperty = sheet.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (countProperty != null)
            {
                return Convert.ToInt32(countProperty.GetValue(sheet));
            }

            var lengthProperty = sheet.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
            if (lengthProperty != null)
            {
                return Convert.ToInt32(lengthProperty.GetValue(sheet));
            }
        }
        catch
        {
            // 忽略，用保底值
        }

        return 0;
    }

    private static Type FindType(string fullName)
    {
        var direct = Type.GetType(fullName + ", Lumina.Excel");
        if (direct != null)
        {
            return direct;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.FullName == fullName)
                {
                    return type;
                }
            }
        }

        return null;
    }
}
