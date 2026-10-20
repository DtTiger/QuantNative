using System;
using System.Globalization;

namespace QuantNative.Core
{
    /// <summary>
    /// 浮窗「每日显示时段」判定。
    ///
    /// 规则（纯函数，与界面无关，便于单测）：
    ///   · 起止都用本地时间的 HH:mm；解析失败时退回默认值，不会因为设置写坏而不再显示浮窗。
    ///   · 起 &lt; 止 —— 常规窗口，例如 09:15–23:30。
    ///   · 起 &gt; 止 —— 跨零点窗口，例如 22:00–02:00，会自动拆成两段。
    ///   · 起 == 止 —— 视为「全天显示」，避免用户把两端拖成同一个值后浮窗彻底消失。
    ///
    /// 判定精确到秒，边界时刻（正好等于起/止）按「已进入窗口 / 已离开窗口」处理，
    /// 与用户直觉一致：到点就显示，到点就隐藏。
    /// </summary>
    public static class TickerSchedule
    {
        public const string DefaultFrom = "09:15";
        public const string DefaultTo = "23:30";

        /// <summary>解析 "9:5" / "09:05" / "0905" / "9:05:30"，失败返回 false。</summary>
        public static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            string raw = (text ?? "").Trim();
            if (raw.Length == 0) return false;

            string[] parts = raw.Split(':');
            int hour, minute, second = 0;
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
                if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
            }
            else if (parts.Length == 3)
            {
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
                if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
                if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out second)) return false;
            }
            else if (raw.Length == 4)
            {
                if (!int.TryParse(raw.Substring(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
                if (!int.TryParse(raw.Substring(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
            }
            else return false;

            if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return false;
            time = new TimeSpan(hour, minute, second);
            return true;
        }

        /// <summary>把用户输入规整成 HH:mm；无法解析时返回 fallback。</summary>
        public static string Normalize(string text, string fallback)
        {
            TimeSpan time;
            if (!TryParseTime(text, out time)) return fallback;
            return Format(time);
        }

        public static string Format(TimeSpan time)
        {
            return time.Hours.ToString("00", CultureInfo.InvariantCulture) + ":"
                + time.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>字符串版判定（内部做解析与兜底）。</summary>
        public static bool IsWithin(string from, string to, DateTime now)
        {
            TimeSpan start, end;
            if (!TryParseTime(from, out start)) start = ParseOr(DefaultFrom);
            if (!TryParseTime(to, out end)) end = ParseOr(DefaultTo);
            return IsWithin(start, end, now.TimeOfDay);
        }

        /// <summary>核心判定。</summary>
        public static bool IsWithin(TimeSpan from, TimeSpan to, TimeSpan now)
        {
            Trim(ref now);
            if (from == to) return true;                  // 全天显示
            if (from < to) return now >= from && now < to; // 常规窗口
            return now >= from || now < to;                // 跨零点窗口
        }

        /// <summary>
        /// 距离窗口下一次切换（进入或离开）还有多少秒。
        /// 界面用它决定「什么时候再检查一次」，避免每秒空转。
        /// </summary>
        public static int SecondsUntilChange(string from, string to, DateTime now)
        {
            TimeSpan start, end;
            if (!TryParseTime(from, out start)) start = ParseOr(DefaultFrom);
            if (!TryParseTime(to, out end)) end = ParseOr(DefaultTo);
            if (start == end) return 3600;

            TimeSpan current = now.TimeOfDay;
            Trim(ref current);

            int best = int.MaxValue;
            best = Math.Min(best, SecondsUntil(start, current));
            best = Math.Min(best, SecondsUntil(end, current));
            if (best == int.MaxValue || best < 1) best = 1;
            return best;
        }

        /// <summary>下一次切换的日历时间（用于设置页提示「下次切换 23:30」）。</summary>
        public static DateTime NextChangeAt(string from, string to, DateTime now)
        {
            return now.AddSeconds(SecondsUntilChange(from, to, now));
        }

        /// <summary>供设置页显示的一行说明。</summary>
        public static string Describe(string from, string to)
        {
            TimeSpan start, end;
            if (!TryParseTime(from, out start)) start = ParseOr(DefaultFrom);
            if (!TryParseTime(to, out end)) end = ParseOr(DefaultTo);
            if (start == end) return "起止相同 · 全天显示";
            if (start < end) return Format(start) + " – " + Format(end) + " 显示，其余时间自动隐藏";
            return Format(start) + " – 次日 " + Format(end) + " 显示（跨零点）";
        }

        private static TimeSpan ParseOr(string text)
        {
            TimeSpan time;
            return TryParseTime(text, out time) ? time : TimeSpan.Zero;
        }

        private static void Trim(ref TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            if (time >= TimeSpan.FromDays(1)) time = time - TimeSpan.FromDays(1);
        }

        private static int SecondsUntil(TimeSpan target, TimeSpan current)
        {
            double delta = target.TotalSeconds - current.TotalSeconds;
            if (delta == 0) return 1;          // 正好卡在边界：立刻再检查一次，让状态翻转
            if (delta < 0) delta += 86400;     // 今天的这个点已经过了 → 明天
            if (delta < 1) delta = 1;
            return (int)Math.Round(delta);
        }
    }
}
