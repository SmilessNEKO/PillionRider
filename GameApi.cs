using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
#if NATIVE_ACTION
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSCharacterModes = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
#endif

namespace PillionRider;

/// <summary>
/// 版本差异适配层 + 高频查询缓存。
///
/// 这个类同时干两件事，因为它们都是「每帧/每次轮询都要碰、又容易随版本变」的东西：
///
/// 1. <b>LocalPlayer 挂在哪</b> —— 早期在 <c>IClientState</c>，后来挪到 <c>IObjectTable</c>。
///    运行时反射探测，两个位置都试，结果缓存，编译期不依赖具体归属。
///
/// 2. <b>ObjectKind 的成员名</b> —— <c>Player</c>/<c>MountType</c>（Dalamud 风格）与
///    <c>Pc</c>/<c>Mount</c>（游戏内部 EObjectType 风格）两种命名都出现过，还可能再变。
///    所以不做枚举成员比较，而是转成字符串后按关键词宽松匹配。
///
/// 3. <b>性能</b> —— 上面两条每轮轮询要对对象表里每个对象判断一次。如果每次都
///    <c>ToString()</c> + <c>ToLowerInvariant()</c> + <c>Contains()</c>，就是每个对象
///    三次字符串分配，场上 100 个对象、2 Hz 轮询 = 每秒 600 次分配，GC 会难受。
///    这里按「枚举值」缓存判定结果：同一个 ObjectKind 只算一次，之后就是一次字典查找。
/// </summary>
internal static class GameApi
{
    private static bool localPlayerProbed;
    private static PropertyInfo localPlayerProperty;
    private static object localPlayerOwner;

    /// <summary>
    /// ObjectKind 的整数值 → 判定结果。
    /// 键用 int 而不是枚举本身：拿枚举当键每次查找都会装箱分配一个新对象，
    /// 2 Hz 轮询 × 场上 80 个对象 = 每秒 160 次分配，正好是我想省掉的那部分。
    /// </summary>
    private static readonly Dictionary<int, KindFlags> KindCache = new();

    private struct KindFlags
    {
        public bool Mount;
        public bool Player;
    }

    /// <summary>BoundByDuty / BoundByDuty56 / BoundByDuty95 等成员，按名字解析一次。</summary>
    private static ConditionFlag[] dutyFlags;
    private static bool dutyFlagsProbed;

    // ---------------------------------------------------------------- LocalPlayer

    /// <summary>取本地玩家。取不到时返回 null，调用方按未登录处理。</summary>
    public static IGameObject GetLocalPlayer(IObjectTable objectTable, IClientState clientState)
    {
        if (!localPlayerProbed)
        {
            localPlayerProbed = true;

            // 先试 IObjectTable（新版本可能把 LocalPlayer 挪到这里），再退 IClientState
            localPlayerProperty = FindLocalPlayerProperty(objectTable, out localPlayerOwner);
            if (localPlayerProperty == null)
            {
                localPlayerProperty = FindLocalPlayerProperty(clientState, out localPlayerOwner);
            }
        }

        if (localPlayerProperty == null || localPlayerOwner == null)
        {
            return null;
        }

        try
        {
            return localPlayerProperty.GetValue(localPlayerOwner) as IGameObject;
        }
        catch
        {
            return null;
        }
    }

    private static PropertyInfo FindLocalPlayerProperty(object service, out object owner)
    {
        owner = service;

        if (service == null)
        {
            return null;
        }

        try
        {
            return service.GetType().GetProperty("LocalPlayer", BindingFlags.Public | BindingFlags.Instance);
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- ObjectKind

    private static readonly List<MemberInfo> dataIdMembers = new();
    private static readonly Dictionary<Type, MemberInfo> runtimeDataIdMembers = new();
    private static bool dataIdProbed;

    private static PropertyInfo currentMountProperty;
    private static bool currentMountProbed;
    private static readonly List<MemberInfo> currentMountIdMembers = new();

    /// <summary>
    /// 取对象的 DataId（坐骑 ID / 数据行 ID）。
    ///
    /// 为什么不直接写 <c>obj.DataId</c>：API 15 把它标成 <c>[Obsolete]</c>「Renamed to BaseId」，
    /// 直接写会刷 CS0618；而旧版本（API 13）又没有 <c>BaseId</c>。所以这里反射取，
    /// 新版本走 <c>BaseId</c>，旧版本退回 <c>DataId</c>，两边都不报警。
    ///
    /// 而且不止试一条路：实测坐骑对象经常读出 0，所以接口上的 BaseId / DataId、
    /// 运行期具体类型上的 BaseId / DataId 全都试一遍，取第一个非 0 的。
    /// </summary>
    public static uint GetDataId(IGameObject obj)
    {
        if (obj == null)
        {
            return 0;
        }

        ProbeDataIdMembers();

        for (var i = 0; i < dataIdMembers.Count; i++)
        {
            var id = ToUInt(ReadMember(dataIdMembers[i], obj));
            if (id != 0)
            {
                return id;
            }
        }

        // 接口上读不到值（或者读出来就是 0）→ 再按运行期具体类型试一次。
        // 对象表里不同 ObjectKind 的实现类不一样，接口上可能是默认实现返回 0。
        Type runtimeType;
        try
        {
            runtimeType = obj.GetType();
        }
        catch
        {
            return 0;
        }

        if (!runtimeDataIdMembers.TryGetValue(runtimeType, out var runtimeMember))
        {
            runtimeMember = FindIdMember(runtimeType);
            runtimeDataIdMembers[runtimeType] = runtimeMember;
        }

        if (runtimeMember == null)
        {
            return 0;
        }

        return ToUInt(ReadMember(runtimeMember, obj));
    }

    private static void ProbeDataIdMembers()
    {
        if (dataIdProbed)
        {
            return;
        }

        dataIdProbed = true;
        dataIdMembers.Clear();

        AddIdMember(dataIdMembers, typeof(IGameObject), "BaseId");
        AddIdMember(dataIdMembers, typeof(IGameObject), "DataId");
    }

    private static void AddIdMember(List<MemberInfo> target, Type type, string name)
    {
        var found = FindIdMember(type, name);
        if (found != null)
        {
            target.Add(found);
        }
    }

    private static MemberInfo FindIdMember(Type type)
    {
        if (type == null)
        {
            return null;
        }

        return FindIdMember(type, "BaseId") ?? FindIdMember(type, "DataId");
    }

    private static MemberInfo FindIdMember(Type type, string name)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.Instance;

            var property = type.GetProperty(name, flags);
            if (property != null)
            {
                return property;
            }

            return type.GetField(name, flags);
        }
        catch
        {
            return null;
        }
    }

    private static object ReadMember(MemberInfo member, object target)
    {
        if (member == null || target == null)
        {
            return null;
        }

        try
        {
            return member is PropertyInfo property ? property.GetValue(target) : ((FieldInfo)member).GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static uint ToUInt(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            return Convert.ToUInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 直接问角色自己「你现在骑的是哪个坐骑」。
    ///
    /// 有些版本里坐骑对象的 <c>DataId</c> 是 0，而角色身上的 <c>CurrentMount</c> 反而带得动 ID。
    /// 全反射：<c>CurrentMount</c> 的返回类型是结构体还是类、成员叫 MountId 还是 Id，
    /// 各版本不一样，写死任何一条都可能在某个版本编译失败。
    /// </summary>
    public static bool TryGetRiderMountId(IGameObject player, out uint mountId)
    {
        mountId = 0;

        if (!(player is ICharacter character))
        {
            return false;
        }

        ProbeCurrentMount();

        if (currentMountProperty == null)
        {
            return false;
        }

        object current;
        try
        {
            current = currentMountProperty.GetValue(character);
        }
        catch
        {
            return false;
        }

        if (current == null)
        {
            return false;
        }

        for (var i = 0; i < currentMountIdMembers.Count; i++)
        {
            var id = ToUInt(ReadMember(currentMountIdMembers[i], current));
            if (id != 0)
            {
                mountId = id;
                return true;
            }
        }

        return false;
    }

    private static void ProbeCurrentMount()
    {
        if (currentMountProbed)
        {
            return;
        }

        currentMountProbed = true;
        currentMountIdMembers.Clear();

        try
        {
            currentMountProperty = typeof(ICharacter).GetProperty(
                "CurrentMount", BindingFlags.Public | BindingFlags.Instance);
        }
        catch
        {
            currentMountProperty = null;
        }

        if (currentMountProperty == null)
        {
            return;
        }

        var type = currentMountProperty.PropertyType;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            type = underlying;
        }

        // 成员名各版本可能不一样，全都收一遍，取值时取第一个非 0。
        var names = new[] { "MountId", "MountID", "Id", "RowId", "DataId", "BaseId", "Value" };
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        for (var i = 0; i < names.Length; i++)
        {
            try
            {
                var property = type.GetProperty(names[i], flags);
                if (property != null)
                {
                    currentMountIdMembers.Add(property);
                    continue;
                }

                var field = type.GetField(names[i], flags);
                if (field != null)
                {
                    currentMountIdMembers.Add(field);
                }
            }
            catch
            {
                // 单个成员取不到不影响其它候选
            }
        }
    }

    /// <summary>
    /// 取对象的 ObjectKind 名字（小写）。取不到返回 null。
    /// 只在 /pr kinds 排查时用，不参与每轮轮询。
    /// </summary>
    public static string GetKindName(IGameObject obj)
    {
        if (obj == null)
        {
            return null;
        }

        try
        {
            return obj.ObjectKind.ToString().ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 这个 ID 是不是「非网络实体」的本地对象 ID。
    ///
    /// 游戏里 <c>0xE000_0000</c> 及以上的 ID 表示客户端本地对象（不参与网络同步），
    /// 坐骑、宠物、装饰这类附着物常常挂在这个区段。它们的 <c>OwnerId</c> 也落在这个区段里，
    /// 拿它去和玩家的 <c>GameObjectId</c>（网络实体 ID）比较永远不会相等 ——
    /// 所以「坐骑对象.OwnerId → 找骑手」这条路对本地坐骑是走不通的，必须先排掉。
    /// </summary>
    public static bool IsLocalObjectId(ulong id) => id >= 0xE000_0000UL;

    /// <summary>
    /// 这个对象是不是某个角色骑着的坐骑。
    /// 宽松匹配：<c>MountType</c> / <c>Mount</c> / 任何含 "mount" 的枚举名都算。
    /// </summary>
    public static bool IsMount(IGameObject obj) => GetFlags(obj).Mount;

    /// <summary>
    /// 这个对象是不是一个玩家角色。
    /// <c>Player</c> / <c>Pc</c> / 任何含 "player" 的枚举名都算；
    /// "pc" 必须整体相等，否则会把 <c>BattleNpc</c> / <c>EventNpc</c> 也算进来。
    /// </summary>
    public static bool IsPlayer(IGameObject obj) => GetFlags(obj).Player;

    private static KindFlags GetFlags(IGameObject obj)
    {
        if (obj == null)
        {
            return default;
        }

        object raw;
        try
        {
            raw = obj.ObjectKind;
        }
        catch
        {
            return default;
        }

        if (raw == null)
        {
            return default;
        }

        // 转 int 当键，避免装箱；顺便对「枚举底层类型不是 int」的情况兜一层。
        int key;
        try
        {
            key = Convert.ToInt32(raw);
        }
        catch
        {
            key = raw.GetHashCode();
        }

        if (KindCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var flags = Classify(raw.ToString());
        KindCache[key] = flags;
        return flags;
    }

    private static KindFlags Classify(string raw)
    {
        var flags = default(KindFlags);
        if (string.IsNullOrEmpty(raw))
        {
            return flags;
        }

        var kind = raw.ToLowerInvariant();

        flags.Mount = kind.Contains("mount");

        flags.Player = kind == "pc"
                       || kind == "player"
                       || kind == "localplayer"
                       || kind.Contains("player");

        return flags;
    }

    // ---------------------------------------------------------------- 条件位按名字取

    /// <summary>
    /// 按<b>名字</b>查 <c>ConditionFlag</c>，结果缓存（含「查不到」这个结论）。
    ///
    /// 为什么不直接写 <c>ConditionFlag.Casting</c>：这些成员名在不同版本里增删过
    /// （有的带数字后缀，有的整个改名），写死任何一个都可能在某个版本上编译失败。
    /// 按名字查最多是「这个版本没有这个条件」，退化成不检查，不会连累整个插件。
    /// </summary>
    private static readonly Dictionary<string, ConditionFlag?> ConditionCache = new();

    private static bool TryResolveConditionFlag(string name, out ConditionFlag flag)
    {
        if (ConditionCache.TryGetValue(name, out var cached))
        {
            if (cached.HasValue)
            {
                flag = cached.Value;
                return true;
            }

            flag = default;
            return false;
        }

        ConditionFlag? resolved = null;
        try
        {
            if (Enum.TryParse(typeof(ConditionFlag), name, ignoreCase: false, out var value) &&
                value is ConditionFlag parsed)
            {
                resolved = parsed;
            }
        }
        catch
        {
            resolved = null;
        }

        ConditionCache[name] = resolved;

        if (resolved.HasValue)
        {
            flag = resolved.Value;
            return true;
        }

        flag = default;
        return false;
    }

    /// <summary>
    /// 这几个条件里有任意一个成立就返回 true。名字查不到的会被跳过（视为不成立）。
    /// 用于「正在施法 / 正在交互 / 正在过场」这类「此刻不该动手」的判断。
    /// </summary>
    public static bool HasAnyCondition(ICondition condition, params string[] names)
    {
        if (condition == null || names == null)
        {
            return false;
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (!TryResolveConditionFlag(names[i], out var flag))
            {
                continue;
            }

            try
            {
                if (condition[flag])
                {
                    return true;
                }
            }
            catch
            {
                // 单个条件读取失败不影响其它条件
            }
        }

        return false;
    }

    /// <summary>这些条件名里，有几个在当前版本真的存在（用于 /pr scan 显示）。</summary>
    public static int CountResolvableConditions(params string[] names)
    {
        if (names == null)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < names.Length; i++)
        {
            if (TryResolveConditionFlag(names[i], out _))
            {
                count++;
            }
        }

        return count;
    }

    // ---------------------------------------------------------------- 副本判定

    /// <summary>
    /// 解析到的「副本绑定」条件成员数量。
    /// 正常应该是 3（BoundByDuty / BoundByDuty56 / BoundByDuty95）。
    /// 如果是 0，说明这个版本的枚举里根本没有这些名字，副本过滤等于没开——
    /// /pr scan 会把数字打出来，方便判断。
    /// </summary>
    public static int DutyFlagCount
    {
        get
        {
            if (!dutyFlagsProbed)
            {
                dutyFlagsProbed = true;
                dutyFlags = ResolveDutyFlags();
            }

            return dutyFlags == null ? 0 : dutyFlags.Length;
        }
    }

    /// <summary>
    /// 自己是否被副本绑定（也就是「在副本里」）。
    ///
    /// 不用硬编码 <c>ConditionFlag.BoundByDuty</c>：这个名字带数字后缀的变体有好几个
    /// （<c>BoundByDuty56</c> / <c>BoundByDuty95</c>），跨版本还可能增删。所以按名字前缀
    /// 反射出所有 <c>BoundByDuty*</c> 成员，命中任意一个就算在副本里。
    /// </summary>
    public static bool IsInDuty(ICondition condition)
    {
        if (condition == null)
        {
            return false;
        }

        if (!dutyFlagsProbed)
        {
            dutyFlagsProbed = true;
            dutyFlags = ResolveDutyFlags();
        }

        if (dutyFlags == null || dutyFlags.Length == 0)
        {
            return false;
        }

        try
        {
            for (var i = 0; i < dutyFlags.Length; i++)
            {
                if (condition[dutyFlags[i]])
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    // ---------------------------------------------------------------- 跨服小队判定

    /// <summary>
    /// 跨服小队（跨服组队 / 跨服团队）的条件位名字。
    ///
    /// 官方文档对 <c>ParticipatingInCrossWorldPartyOrAlliance</c> 的说明是
    /// 「Unable to execute command while participating in a cross-world party or alliance.」
    /// —— 游戏自己就在这个状态下禁掉了一批指令，和我们要的判断完全同源。
    /// 仍然按名字解析而不是写死成员，理由同其它条件位。
    /// </summary>
    private static readonly string[] CrossWorldFlags = { "ParticipatingInCrossWorldPartyOrAlliance" };

    /// <summary>跨服条件位在本版本是否存在（/pr scan 显示用；0 说明只能靠世界比对兜底）。</summary>
    public static int CrossWorldFlagCount => CountResolvableConditions(CrossWorldFlags);

    /// <summary>
    /// 当前是不是<b>跨服小队</b>。
    ///
    /// 跨服时队友的角色实体不在本地的对象表里，「谁骑着哪个坐骑」读不准，
    /// <c>/共同骑乘</c> 也骑不过去，所以整条链路必须停。
    ///
    /// 判据有两条，优先级明确：
    /// 1. 条件位 <c>ParticipatingInCrossWorldPartyOrAlliance</c> —— 游戏自己的口径，最可信。
    ///    **这个成员存在时它就是唯一判据**，直接返回，不额外遍历队伍（零额外开销）。
    /// 2. 只在条件位不存在（某个版本把枚举成员删了/改名了）时才退化为「队友的服务器和自己的不一致」。
    /// </summary>
    public static bool IsCrossWorldParty(
        ICondition condition,
        IPartyList partyList,
        IObjectTable objectTable,
        IClientState clientState)
    {
        if (HasAnyCondition(condition, CrossWorldFlags))
        {
            return true;
        }

        // 条件位存在但没亮 → 确定不是跨服，不必再看世界。
        if (CrossWorldFlagCount > 0)
        {
            return false;
        }

        return HasForeignWorldMember(partyList, objectTable, clientState);
    }

    /// <summary>队伍里有没有人的所属服务器跟自己的不一样。读不出来的一律当「不知道」跳过，不误判。</summary>
    private static bool HasForeignWorldMember(
        IPartyList partyList,
        IObjectTable objectTable,
        IClientState clientState)
    {
        if (partyList == null || partyList.Length == 0)
        {
            return false;
        }

        var selfWorld = GetSelfWorldRowId(objectTable, clientState);
        if (selfWorld == 0)
        {
            // 连自己的服务器都读不出来 → 不下结论，交给条件位那条路。
            return false;
        }

        for (var i = 0; i < partyList.Length; i++)
        {
            // 用 object 接而不是 IPartyMember：下面全走反射按名字取，
            // 不需要为它引命名空间；类型跨版本挪 namespace 也不会连累这里。
            object member;
            try
            {
                member = partyList[i];
            }
            catch
            {
                continue;
            }

            if (member == null)
            {
                continue;
            }

            var world = GetRowRefRowId(ReadMemberByName(member, "World"));
            if (world == 0)
            {
                continue;
            }

            if (world != selfWorld)
            {
                return true;
            }
        }

        return false;
    }

    private static uint GetSelfWorldRowId(IObjectTable objectTable, IClientState clientState)
    {
        var local = GetLocalPlayer(objectTable, clientState);
        if (local == null)
        {
            return 0;
        }

        return GetRowRefRowId(ReadMemberByName(local, "CurrentWorld"));
    }

    /// <summary>
    /// 按名字读一个成员（先属性后字段），读不到返回 null。
    ///
    /// 这里刻意不缓存 <c>MemberInfo</c>：这三处（<c>IPartyMember.World</c>、
    /// <c>IPlayerCharacter.CurrentWorld</c>）只在跨服兜底路径上跑，
    /// 而兜底路径只在条件位缺席的版本上才会走到，频率极低。
    /// </summary>
    private static object ReadMemberByName(object target, string name)
    {
        if (target == null)
        {
            return null;
        }

        try
        {
            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            var property = type.GetProperty(name, flags);
            if (property != null)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(name, flags);
            return field == null ? null : field.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 Lumina 的 <c>RowRef&lt;T&gt;</c> 取出它的行号。
    ///
    /// <c>RowRef&lt;T&gt;</c> 是结构体，装箱后 <c>GetType()</c> 拿到的就是
    /// <c>RowRef&lt;World&gt;</c> 本身，所以直接按 <c>RowId</c> 找属性/字段即可。
    /// 找不到（不同 Lumina 版本叫别的名字）就返回 0，调用方当「不知道」处理。
    /// </summary>
    private static uint GetRowRefRowId(object rowRef)
    {
        if (rowRef == null)
        {
            return 0;
        }

        try
        {
            if (rowRef.GetType().IsPrimitive)
            {
                return Convert.ToUInt32(rowRef);
            }

            var type = rowRef.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            var property = type.GetProperty("RowId", flags);
            if (property != null)
            {
                return Convert.ToUInt32(property.GetValue(rowRef) ?? 0u);
            }

            var field = type.GetField("RowId", flags);
            if (field != null)
            {
                return Convert.ToUInt32(field.GetValue(rowRef) ?? 0u);
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- 客户端结构直读（NATIVE_ACTION）

#if NATIVE_ACTION
    /// <summary>
    /// 读一个角色在客户端结构里的「角色模式」（<c>Character.Mode</c>）。
    ///
    /// <para>
    /// <b>为什么需要这个</b>：<c>ICharacter.CurrentMount</c> 对所有<b>有坐骑</b>的人都返回非空，
    /// 但它区分不出「我是骑手」和「我是乘客」。坐在别人多人坐骑上的人，
    /// 自己的 <c>CurrentMount</c> 同样是 null（官方文档原文：「Will be null if the character
    /// doesn't have a mount.」），位置又跟坐骑完全重合 —— 于是「谁骑着这台坐骑」这件事
    /// 在高层 API 里根本答不出来。
    /// </para>
    ///
    /// <para>
    /// 客户端结构里答案是明确的：<c>Character.Mode == CharacterModes.RidingPillion</c>
    /// 的人就是乘客（FFXIVClientStructs 里这一项的注释写着「Param is the pillion seat number」，
    /// <c>ModeParam</c> 就是他的座位号）。骑手的 <c>Mode</c> 是 <c>Mounted</c>。
    /// </para>
    /// </summary>
    public static bool TryGetCharacterMode(IGameObject obj, out int mode, out int modeParam)
    {
        mode = 0;
        modeParam = 0;

        if (obj == null)
        {
            return false;
        }

        try
        {
            unsafe
            {
                var address = obj.Address;
                if (address == IntPtr.Zero)
                {
                    return false;
                }

                var character = ((CSGameObject*)address)->GetAsCharacter();
                if (character == null)
                {
                    return false;
                }

                mode = (int)character->Mode;
                modeParam = character->ModeParam;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 这个人是不是「坐在别人的共同乘骑座位上」（乘客），而不是骑手。
    ///
    /// 只认 <c>CharacterModes.RidingPillion</c>。读不到结构时返回 false（当骑手处理），
    /// 保持旧行为，不因为一个新判据读失败就把所有候选都挡掉。
    /// </summary>
    public static bool IsPillionPassenger(IGameObject obj)
    {
        if (!TryGetCharacterMode(obj, out var mode, out _))
        {
            return false;
        }

        return mode == (int)CSCharacterModes.RidingPillion;
    }

    /// <summary>
    /// 直接从客户端结构读「这个人骑的坐骑 ID」（<c>Character.Mount.MountId</c>）。
    ///
    /// <para>
    /// 这是<b>最权威</b>的一条：<c>MountContainer.MountId</c> 的类型是 <c>UInt16</c>，
    /// 骑手才有值；乘客这个字段是 0（他骑的是别人的坐骑，自己没有 MountContainer 内容）。
    /// 相比之下 <c>ICharacter.CurrentMount</c> 要走反射猜成员名，还常常读不出 ID。
    /// </para>
    ///
    /// <para>
    /// 读不到结构、或者这个字段是 0 → 返回 false，调用方继续走反射兜底。
    /// </para>
    /// </summary>
    public static bool TryGetMountedIdFromStruct(IGameObject obj, out uint mountId)
    {
        mountId = 0;

        if (obj == null)
        {
            return false;
        }

        try
        {
            unsafe
            {
                var address = obj.Address;
                if (address == IntPtr.Zero)
                {
                    return false;
                }

                var character = ((CSGameObject*)address)->GetAsCharacter();
                if (character == null)
                {
                    return false;
                }

                var raw = character->Mount.MountId;
                if (raw == 0)
                {
                    return false;
                }

                mountId = raw;
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读 <c>Character.Mount.MountedEntityIds</c>：这台坐骑上已经坐了哪些人（EntityId 列表）。
    ///
    /// 这是判断「还有没有空位」的唯一直接依据。座位总数是
    /// <c>1 + Mount.ExtraSeats</c>（骑手自己占 1 个），已坐人数就是这个列表的长度。
    /// 读不到返回空数组。
    /// </summary>
    public static uint[] GetMountedEntityIds(IGameObject rider)
    {
        if (rider == null)
        {
            return Array.Empty<uint>();
        }

        try
        {
            unsafe
            {
                var address = rider.Address;
                if (address == IntPtr.Zero)
                {
                    return Array.Empty<uint>();
                }

                var character = ((CSGameObject*)address)->GetAsCharacter();
                if (character == null)
                {
                    return Array.Empty<uint>();
                }

                var span = character->Mount.MountedEntityIds;
                if (span.Length == 0)
                {
                    return Array.Empty<uint>();
                }

                var result = new uint[span.Length];
                for (var i = 0; i < span.Length; i++)
                {
                    result[i] = span[i];
                }

                return result;
            }
        }
        catch
        {
            return Array.Empty<uint>();
        }
    }
#endif

    /// <summary>角色模式的中文说明，给 /pr scan 用。读不到返回 null。</summary>
    public static string DescribeCharacterMode(IGameObject obj)
    {
        if (!TryGetCharacterMode(obj, out var mode, out var modeParam))
        {
            return null;
        }

#if NATIVE_ACTION
        var name = Enum.IsDefined(typeof(CSCharacterModes), mode)
            ? ((CSCharacterModes)mode).ToString()
            : mode.ToString();

        return modeParam != 0 ? $"{name}/{modeParam}" : name;
#else
        return mode.ToString();
#endif
    }

    private static ConditionFlag[] ResolveDutyFlags()
    {
        try
        {
            var names = Enum.GetNames(typeof(ConditionFlag));
            var values = new List<ConditionFlag>();

            foreach (var name in names)
            {
                if (name.StartsWith("BoundByDuty", StringComparison.Ordinal))
                {
                    values.Add((ConditionFlag)Enum.Parse(typeof(ConditionFlag), name));
                }
            }

            return values.ToArray();
        }
        catch
        {
            return Array.Empty<ConditionFlag>();
        }
    }
}
