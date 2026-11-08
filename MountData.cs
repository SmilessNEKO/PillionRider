using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Services;

namespace PillionRider;

/// <summary>
/// 通过反射读取 Lumina 的 Mount 表，判断某个坐骑 ID 是不是多座（共同）坐骑。
/// 全部走反射：避免编译期依赖 Lumina.Excel 的具体成员形状（不同版本成员可能是字段也可能是属性）。
/// 判定依据是 <c>ExtraSeats &gt; 0</c>——FF14 里能载队友的坐骑都有额外座位。
/// </summary>
internal static class MountData
{
    /// <summary>最多尝试初始化几次（插件刚加载时 Excel 数据可能还没就绪）。</summary>
    private const int MaxAttempts = 60;

    private static int attempts;
    private static bool available;
    private static Type rowType;
    private static object mountSheet;
    private static MethodInfo getRowMethod;      // GetRowOrDefault(uint) / GetRow(uint)
    private static MethodInfo tryGetRowMethod;   // TryGetRow(uint, out T)
    private static PropertyInfo rowIndexer;      // this[uint]
    private static MemberInfo extraSeatsMember;  // ExtraSeats 属性或字段
    private static string failureReason;
    private static string loggedReason;

    /// <summary>
    /// 坐骑 ID → 是否多座。坐骑表是静态数据，一个 ID 的答案永远不会变，
    /// 所以缓存住就能把「每次轮询都反射调用一次」降到「一辈子一次」。
    /// </summary>
    private static readonly Dictionary<uint, bool> SeatCache = new();

    /// <summary>同上，但存的是表里的原始 ExtraSeats 数值，供 /pr mountinfo 显示。</summary>
    private static readonly Dictionary<uint, int> SeatCountCache = new();

    public static string FailureReason => failureReason;

    /// <summary>
    /// 坐骑表是否已经读通了。<c>false</c> 时 <see cref="GetExtraSeats"/> 一律返回 null，
    /// 调用方应当按「不知道座位数」处理，而不是当成 0。
    /// </summary>
    public static bool IsLoaded => available;

    public static void Initialize(IDataManager dataManager, IPluginLog log)
    {
        if (available || attempts >= MaxAttempts)
        {
            return;
        }

        attempts++;
        failureReason = null;

        try
        {
            if (rowType == null)
            {
                rowType = FindType("Lumina.Excel.Sheets.Mount");
                if (rowType == null)
                {
                    Fail(log, "找不到 Lumina.Excel.Sheets.Mount 类型");
                    return;
                }
            }

            if (extraSeatsMember == null)
            {
                // PropertyInfo 和 FieldInfo 没有共同的可空类型，不能直接串 ?? 。
                // 分两步找：先属性，属性没有再看字段。
                MemberInfo found = rowType.GetProperty("ExtraSeats") ?? rowType.GetProperty("ExtraSeat");

                if (found == null)
                {
                    found = rowType.GetField("ExtraSeats", BindingFlags.Public | BindingFlags.Instance)
                            ?? rowType.GetField("ExtraSeat", BindingFlags.Public | BindingFlags.Instance);
                }

                extraSeatsMember = found;

                if (extraSeatsMember == null)
                {
                    Fail(log, "Mount 行结构里没有 ExtraSeats 成员");
                    return;
                }
            }

            var getExcelSheet = FindGetExcelSheetMethod();
            if (getExcelSheet == null)
            {
                Fail(log, "找不到 IDataManager.GetExcelSheet<T> 泛型方法");
                return;
            }

            mountSheet = getExcelSheet
                .MakeGenericMethod(rowType)
                .Invoke(dataManager, new object[] { null, null });

            if (mountSheet == null)
            {
                Fail(log, "Mount 表返回 null");
                return;
            }

            var sheetType = mountSheet.GetType();

            getRowMethod = sheetType.GetMethod("GetRowOrDefault", new[] { typeof(uint) })
                           ?? sheetType.GetMethod("GetRow", new[] { typeof(uint) });

            tryGetRowMethod = sheetType.GetMethod("TryGetRow", new[] { typeof(uint), rowType.MakeByRefType() });

            if (getRowMethod == null && tryGetRowMethod == null)
            {
                rowIndexer = sheetType.GetProperty("Item", new[] { typeof(uint) });
            }

            if (getRowMethod == null && tryGetRowMethod == null && rowIndexer == null)
            {
                Fail(log, "Mount 表既没有 GetRow / TryGetRow 也没有索引器");
                return;
            }

            available = true;
            loggedReason = null;
            log.Information("[PillionRider] Mount 表已就绪（用 ExtraSeats 判定多座坐骑）。");
        }
        catch (Exception ex)
        {
            Fail(log, ex.Message);
        }
    }

    /// <summary>同一个原因只打一次警告，避免每次轮询都刷屏。</summary>
    private static void Fail(IPluginLog log, string reason)
    {
        failureReason = reason;

        if (loggedReason == reason)
        {
            return;
        }

        loggedReason = reason;
        log.Warning("[PillionRider] 坐骑表初始化失败：{Reason}", reason);
    }

    /// <summary>
    /// true = 多座（可以共同骑乘）；false = 单座；null = 无法判定。
    /// </summary>
    public static bool? IsMultiSeat(uint mountId)
    {
        // ID 为 0 不是「单座坐骑」，而是「压根没读到坐骑 ID」。
        // 早先这里会被查成 Mount 表第 0 行的 ExtraSeats=0，于是判成单座，
        // 候选被静默过滤掉 —— 现场就是这样把胖莫古力（ExtraSeats=1）挡掉的。
        if (mountId == 0)
        {
            return null;
        }

        if (SeatCache.TryGetValue(mountId, out var cached))
        {
            return cached;
        }

        var seats = GetExtraSeats(mountId);
        if (seats == null)
        {
            return null;
        }

        var result = seats.Value > 0;
        SeatCache[mountId] = result;
        return result;
    }

    /// <summary>
    /// 读某个坐骑的 ExtraSeats（额外座位数）。读不到返回 null。
    /// 单独抽出来是为了给 <c>/pr mountinfo</c> 用：判多座之外，还想让人能直接看到表里的原始数值。
    /// </summary>
    public static int? GetExtraSeats(uint mountId)
    {
        if (!available || mountSheet == null || extraSeatsMember == null)
        {
            return null;
        }

        if (SeatCountCache.TryGetValue(mountId, out var cached))
        {
            return cached;
        }

        try
        {
            var row = ReadRow(mountId);
            if (row == null)
            {
                return null;
            }

            var value = extraSeatsMember is PropertyInfo property
                ? property.GetValue(row)
                : ((FieldInfo)extraSeatsMember).GetValue(row);

            if (value == null)
            {
                return null;
            }

            var seats = Convert.ToInt32(value);
            SeatCountCache[mountId] = seats;
            return seats;
        }
        catch
        {
            return null;
        }
    }

    private static object ReadRow(uint mountId)
    {
        if (getRowMethod != null)
        {
            return getRowMethod.Invoke(mountSheet, new object[] { mountId });
        }

        if (tryGetRowMethod != null)
        {
            var args = new object[] { mountId, null };
            var ok = tryGetRowMethod.Invoke(mountSheet, args);

            if (ok is bool success && success)
            {
                return args[1];
            }

            return null;
        }

        if (rowIndexer != null)
        {
            return rowIndexer.GetValue(mountSheet, new object[] { mountId });
        }

        return null;
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

    private static MethodInfo FindGetExcelSheetMethod()
    {
        var candidates = new List<MethodInfo>();

        foreach (var method in typeof(IDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "GetExcelSheet" ||
                !method.IsGenericMethodDefinition ||
                method.GetGenericArguments().Length != 1)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 2 && parameters[1].ParameterType == typeof(string))
            {
                candidates.Add(method);
            }
        }

        return candidates.Count > 0 ? candidates[0] : null;
    }
}
