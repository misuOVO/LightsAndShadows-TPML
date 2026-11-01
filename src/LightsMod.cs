using System;
using HarmonyLib;
using tContentPatch;
using tContentPatch.ModLoad;
using tContentPatch.Patch;

namespace LightsAndShadows
{
    /// <summary>
    /// 模组入口: 加载配置目录 + 应用程序集内全部 [HarmonyPatch] 补丁类
    /// (与 ImproveGameLite 的 PondMod 同一套框架用法)
    /// </summary>
    internal class LightsMod : Mod
    {
        private const string PatchId = "LightsAndShadows.patch";
        private Harmony harmony = null;
        private static string modDir = null;
        private static readonly System.Collections.Generic.HashSet<string> loggedErrors = new System.Collections.Generic.HashSet<string>();

        /// <summary>运行期异常记录 (同一信息只记一次, 追加到 patch-errors.txt)</summary>
        internal static void LogRuntimeError(string message)
        {
            if (modDir == null || message == null) return;
            string key = message.Length > 200 ? message.Substring(0, 200) : message;
            if (!loggedErrors.Add(key)) return;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(modDir, "patch-errors.txt"),
                    "\n[运行期] " + message + "\n");
            }
            catch { }
        }

        /// <summary>诊断日志 (进世界/首帧绘制参数, 追加到 draw-log.txt, 便于核对运行状态)</summary>
        internal static void LogInfo(string message)
        {
            if (modDir == null || message == null) return;
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(modDir, "draw-log.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\n");
            }
            catch { }
        }

        public override void Load(ModObject mo)
        {
            modDir = mo?.modPath;
        }

        public override void AddPatch(IAddPatch addPatch)
        {
            harmony = new Harmony(PatchId);
            // 逐类应用补丁: 单个补丁类失败 (目标方法在当前游戏版本缺失等) 时降级跳过,
            // 保证其余功能可用; 失败明细写入模组文件夹 patch-errors.txt 便于定位
            System.Text.StringBuilder failures = new System.Text.StringBuilder();
            foreach (System.Type type in typeof(LightsMod).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (System.Exception ex)
                {
                    System.Exception e = (ex is HarmonyException && ex.InnerException != null) ? ex.InnerException : ex;
                    failures.AppendLine(type.Name + ": " + e.Message);
                }
            }
            if (failures.Length > 0 && modDir != null)
            {
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(modDir, "patch-errors.txt"),
                        "以下补丁类应用失败 (功能降级), 请反馈:\n" + failures);
                }
                catch { }
            }
        }

        public override void Unload()
        {
            LightingFX.DisposeTextures();
            harmony?.UnpatchAll(PatchId);
            harmony = null;
        }
    }
}
