using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TaskbarMonitor
{
    /// <summary>
    /// Opens Windows Task Manager on the Performance page.
    ///
    /// Task Manager has no command-line switch for the start page, so this temporarily sets
    /// "DefaultStartPage" in %LOCALAPPDATA%\Microsoft\Windows\TaskManager\settings.json
    /// (the "Default start page" setting) and restores the user's value after Task Manager exits.
    /// Task Manager writes its settings back on exit, so restoring while it runs would be overwritten.
    /// If Task Manager is already running it is simply brought to front (page unchanged).
    /// </summary>
    internal static class TaskManagerLauncher
    {
        private const string Key = "DefaultStartPage";
        private const int PerformancePage = 1;
        private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(10);

        private static readonly object sync = new object();
        private static System.Threading.Timer watchTimer;
        private static DateTime launchedAt;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\TaskManager\settings.json");

        // remembers the user's original value in case this process ends before restoring
        private static string MarkerPath => Path.Combine(Path.GetTempPath(), "taskbar-monitor-taskmgr-restore.json");

        private static bool TaskManagerRunning => Process.GetProcessesByName("Taskmgr").Length > 0;

        private static string TaskManagerExe =>
            File.Exists(Path.Combine(Environment.SystemDirectory, "taskmgr.exe"))
                ? Path.Combine(Environment.SystemDirectory, "taskmgr.exe")
                : "taskmgr.exe";

        public static void OpenPerformance()
        {
            lock (sync)
            {
                try
                {
                    if (!TaskManagerRunning)
                    {
                        RestoreIfPending();
                        if (SetStartPage())
                            StartWatching();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
            Process.Start(TaskManagerExe);
        }

        /// <summary>Restore a value left over from a previous session (e.g. after a crash).</summary>
        public static void RecoverIfNeeded()
        {
            lock (sync)
            {
                try
                {
                    if (watchTimer == null && !TaskManagerRunning)
                        RestoreIfPending();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
        }

        private static bool SetStartPage()
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return false; // Windows 10 / older Task Manager: nothing to do

            var json = JObject.Parse(File.ReadAllText(path));
            var marker = new JObject { ["hadKey"] = json[Key] != null, ["value"] = json[Key] };
            File.WriteAllText(MarkerPath, marker.ToString(Formatting.None));

            json[Key] = PerformancePage;
            Write(path, json);
            return true;
        }

        private static void RestoreIfPending()
        {
            if (!File.Exists(MarkerPath)) return;
            var marker = JObject.Parse(File.ReadAllText(MarkerPath));
            var path = SettingsPath;
            if (File.Exists(path))
            {
                var json = JObject.Parse(File.ReadAllText(path));
                if (marker.Value<bool>("hadKey"))
                    json[Key] = marker["value"];
                else
                    json.Remove(Key);
                Write(path, json);
            }
            File.Delete(MarkerPath);
        }

        private static void Write(string path, JObject json)
        {
            File.WriteAllText(path, json.ToString(Formatting.None), new UTF8Encoding(false));
        }

        private static void StartWatching()
        {
            launchedAt = DateTime.Now;
            watchTimer?.Dispose();
            watchTimer = new System.Threading.Timer(_ =>
            {
                lock (sync)
                {
                    if (watchTimer == null) return;
                    if (DateTime.Now - launchedAt < StartGrace || TaskManagerRunning) return;
                    try
                    {
                        // Task Manager has exited and saved its settings: put the user's value back
                        System.Threading.Thread.Sleep(500);
                        RestoreIfPending();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                    watchTimer.Dispose();
                    watchTimer = null;
                }
            }, null, 2000, 2000);
        }
    }
}
