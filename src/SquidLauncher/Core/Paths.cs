using System;
using System.Diagnostics;
using System.IO;

namespace SquidLauncher.Core
{
    /// <summary>
    /// 便携优先：配置和数据都放在 exe 旁边的 SquidLauncher 文件夹里，拷走就能用。
    /// exe 所在目录不可写（比如放进了 Program Files）时退回 %APPDATA%\SquidLauncher。
    /// </summary>
    public static class Paths
    {
        public static readonly string ExePath;
        public static readonly string ExeDir;
        public static readonly string DataDir;

        static Paths()
        {
            ExePath = Process.GetCurrentProcess().MainModule.FileName;
            ExeDir = Path.GetDirectoryName(ExePath);
            var portable = Path.Combine(ExeDir, Branding.AppName);
            DataDir = CanWrite(ExeDir) ? portable : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Branding.AppName);
            MigrateLegacy(Path.GetDirectoryName(DataDir), Branding.AppName, Branding.LegacyAppName);
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>
        /// 改名后的一次性收尾：新目录还不存在、旧目录在，就把旧的整体改名过来。
        /// 只动一次（新目录一旦存在就不再碰），失败也不报错——宁可从头开始也不能启动失败。
        /// </summary>
        static void MigrateLegacy(string parent, string newName, string oldName)
        {
            try
            {
                if (string.IsNullOrEmpty(parent)) return;
                var dst = Path.Combine(parent, newName);
                var src = Path.Combine(parent, oldName);
                if (Directory.Exists(dst) || !Directory.Exists(src)) return;
                Directory.Move(src, dst);
            }
            catch (Exception ex) { Log.Warn("迁移旧数据目录失败：" + ex.Message); }
        }

        public static string Config => Path.Combine(DataDir, "config.json");
        public static string Accounts => Path.Combine(DataDir, "accounts.dat");
        public static string Cache => Ensure(Path.Combine(DataDir, "cache"));
        public static string Logs => Ensure(Path.Combine(DataDir, "logs"));
        public static string Temp => Ensure(Path.Combine(DataDir, "temp"));
        public static string JavaRuntimes => Ensure(Path.Combine(DataDir, "java"));
        public static string Tools => Ensure(Path.Combine(DataDir, "tools"));

        /// <summary>默认游戏目录：exe 旁边的 .minecraft（和 PCL 习惯一致）</summary>
        public static string DefaultGameDir => Path.Combine(ExeDir, ".minecraft");
        public static string OfficialGameDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        public static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

        static bool CanWrite(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".ol_write_test");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }
}
