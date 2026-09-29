using System.Reflection;
using CUCoreLib.Helpers;
using CUCoreLib.Networking;
using HarmonyLib;
using UnityEngine;

namespace CUCoreLib.Patches
{
    internal static class KrokMpHealPatches // Probably not needed, might rename this to general MP references for certain actions later down
    // I wouldn't patch these functions with harmony if you were thinking about that, thanks ^^
    {
        private const string BodyExtensionsTypeName = "KrokoshaCasualtiesUtils.Util_BodyExtensions";

        private static bool _installed;
        private static bool _retryScheduled;
        private static bool _healCommandWindow;

        internal static bool IsInstalled => _installed;

        internal static void Install(Harmony harmony)
        {
            if (harmony == null || _installed) return;

            var resetHealth = ResolveResetHealth();
            if (resetHealth == null)
            {
                ScheduleRetry(harmony);
                return;
            }

            harmony.Patch(resetHealth,
                postfix: new HarmonyMethod(typeof(KrokMpHealPatches), nameof(ResetHealthPostfix)));
            _installed = true;
        }

        internal static void OpenHealWindow(bool isHealCommand)
        {
            _healCommandWindow = isHealCommand;
        }

        internal static void CloseHealWindow()
        {
            _healCommandWindow = false;
        }

        private static void ResetHealthPostfix(Body __0)
        {
            // Outside a running session, the vanilla command wrapper already reports the healed player.
            if (!_healCommandWindow || !MultiplayerBridge.IsRunning) return;

            PlayerEventPatches.NotifyHeal(__0);
        }

        private static MethodInfo ResolveResetHealth()
        {
            var type = MultiplayerApi.ResolveKrokMpType(BodyExtensionsTypeName);
            if (type == null) return null;

            var method = AccessTools.Method(type, "ResetHealth", new[] { typeof(Body), typeof(bool) });
            return method != null && method.IsStatic ? method : null;
        }

        private static void ScheduleRetry(Harmony harmony)
        {
            if (_retryScheduled || MultiplayerApi.KrokMpVersion == null) return;

            _retryScheduled = true;
            CUCoreUtils.DelayCall(1f, () =>
            {
                _retryScheduled = false;
                if (_installed) return;

                Install(harmony);
            });
        }
    }
}
