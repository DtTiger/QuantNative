using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace QuantNative.Core
{
    /// <summary>
    /// 极简日志：输出到 Debug 与数据目录下的 quant-native.log。
    /// 超过 2MB 自动滚动一次（保留一个 .1 备份），避免长期运行把磁盘写满。
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "quant-native.log");
            }
            catch
            {
                _path = null;
            }
        }

        public static string Path_ { get { return _path; } }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }

        public static void Error(string message) { Write("ERROR", message); }

        private static readonly object ThrottleGate = new object();
        private static readonly System.Collections.Generic.Dictionary<string, DateTime> ThrottledAt =
            new System.Collections.Generic.Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>
        /// 限频告警：同一个 key 在 window 内只落一条。
        /// 板块/概览接口整段不可达时，每秒一条 WARN 会把日志刷到几十 MB（实测 8000+ 条），
        /// 出问题时反而更难定位，所以按 key 做节流。
        /// </summary>
        public static void WarnOnce(string key, string message, TimeSpan window)
        {
            DateTime last;
            lock (ThrottleGate)
            {
                if (ThrottledAt.TryGetValue(key ?? "", out last) && DateTime.Now - last < window) return;
                ThrottledAt[key ?? ""] = DateTime.Now;
                // 键不会无限增长（板块代码 + 少量固定前缀），但超量时清一次也无副作用
                if (ThrottledAt.Count > 512) ThrottledAt.Clear();
            }
            Warn(message);
        }

        public static void Error(string message, Exception ex)
        {
            if (ex == null)
            {
                Write("ERROR", message);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(message).Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

            Exception inner = ex.InnerException;
            if (inner != null)
                sb.Append("  [inner] ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);

            // 只保留本项目自己的调用帧：框架内部的栈没有定位价值，还会把日志撑爆。
            string stack = ex.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                string[] lines = stack.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int shown = 0;
                for (int i = 0; i < lines.Length && shown < 8; i++)
                {
                    string line = lines[i].Trim();
                    if (line.IndexOf("QuantNative", StringComparison.Ordinal) < 0) continue;
                    sb.Append(Environment.NewLine).Append("         ").Append(line);
                    shown++;
                }
            }

            Write("ERROR", sb.ToString());
        }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            Debug.WriteLine(line);
            if (_path == null) return;

            lock (Gate)
            {
                try
                {
                    FileInfo info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string backup = _path + ".1";
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(_path, backup);
                    }
                    File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // 日志失败不能影响主流程
                }
            }
        }
    }
}
