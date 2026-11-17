using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.autostrutpivot
{
    /// <summary>
    /// Installs the fix the settings turn on, once, when KSP starts: from then on, no autostrut of an unlocked
    /// robotic part crosses that part's own pivot.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPFixAutoStrutPivot : MonoBehaviour
    {
        private const string HarmonyId = "com.github.lhervier.ksp.fix.autostrutpivot";

        private void Start()
        {
            Settings.Load();
            string version = typeof(KSPFixAutoStrutPivot).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Log.Info($"Version {version}, " +
                $"heaviestOnOwnSide {Settings.HeaviestOnOwnSide}, log level {Log.Level}");
            if (!Settings.HeaviestOnOwnSide)
            {
                return;
            }
            try
            {
                AutoStrutPivot.Install(new Harmony(HarmonyId));
                Log.Info("Heaviest on own side fix installed");
            }
            catch (Exception e)
            {
                Log.Error("Could not install the heaviest on own side fix, the autostrut of a robotic part can still " +
                    $"cross its pivot and block it, as without it: {e}");
            }
        }
    }
}
