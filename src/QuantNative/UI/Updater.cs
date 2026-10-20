using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using QuantNative.Core;
using QuantNative.Data;

namespace QuantNative.UI
{
    /// <summary>
    /// 自更新检查。
    ///
    /// 与旧版（tauri-plugin-updater + minisign 签名的 latest.json）的差别：
    /// 这里读 GitHub Releases API 比对版本号，然后**征求用户同意**后打开下载页，
    /// 不做静默替换。理由：把「联网下载 exe 并原地替换」的自动升级链做好需要签名
    /// 校验与安装器配合，成本远高于收益；对一个看盘小工具来说，「发现新版本 →
    /// 一键打开下载页」已经覆盖了真实需求。
    ///
    /// ⚠ 发布前把 UpdateRepo 改成你自己的仓库（owner/repo）；留空则始终提示「未配置更新源」。
    /// </summary>
    public static class Updater
    {
        /// <summary>GitHub 仓库（owner/repo）。留空 = 不检查更新。</summary>
        public const string UpdateRepo = "";

        public static Version CurrentVersion
        {
            get
            {
                try { return Assembly.GetEntryAssembly().GetName().Version; }
                catch { return new Version(2, 0, 0); }
            }
        }

        private sealed class ReleaseInfo
        {
            public string Tag;
            public string Name;
            public string Notes;
            public string Url;
            public Version Version;
        }

        public static async void CheckInteractive(AppHost host, Action<string> report = null)
        {
            Report(report, "检查中…");
            try
            {
                ReleaseInfo release = await CheckAsync();
                if (release == null)
                {
                    if (string.IsNullOrEmpty(UpdateRepo))
                    {
                        Report(report, "未配置更新源");
                        MessageBox.Show(
                            "当前未配置更新源（Updater.UpdateRepo 为空）。\r\n" +
                            "发布到自己的 GitHub 仓库后填上 owner/repo 即可启用检查更新。",
                            "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    Report(report, "已是最新版本");
                    MessageBox.Show("当前已是最新版本 v" + CurrentVersion + "。", "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Report(report, "发现新版本 v" + release.Version);
                string message = "发现新版本 v" + release.Version + "（当前 v" + CurrentVersion + "）。\r\n\r\n"
                    + Truncate(release.Notes, 400) + "\r\n\r\n是否打开下载页？";
                DialogResult result = MessageBox.Show(message, "检查更新",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (result == DialogResult.Yes && !string.IsNullOrEmpty(release.Url))
                {
                    Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("检查更新失败: " + ex.Message);
                Report(report, "检查更新失败");
                MessageBox.Show("检查更新失败：" + ex.Message, "检查更新",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void Report(Action<string> report, string text)
        {
            if (report != null) report(text);
        }

        /// <summary>返回 null 表示已是最新（或未配置更新源）。</summary>
        private static async Task<ReleaseInfo> CheckAsync()
        {
            if (string.IsNullOrEmpty(UpdateRepo)) return null;

            string url = "https://api.github.com/repos/" + UpdateRepo + "/releases/latest";
            string text = await Http.GetTextAsync(url, "https://github.com").ConfigureAwait(false);
            object root = Json.Parse(text);
            if (root == null) return null;

            string tag = Json.Str(Json.Get(root, "tag_name"));
            if (tag.Length == 0) return null;

            Version remote;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out remote)) return null;
            if (remote <= CurrentVersion) return null;

            ReleaseInfo info = new ReleaseInfo();
            info.Tag = tag;
            info.Version = remote;
            info.Name = Json.Str(Json.Get(root, "name"));
            info.Notes = Json.Str(Json.Get(root, "body"));
            info.Url = Json.Str(Json.Get(root, "html_url"));
            return info;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
