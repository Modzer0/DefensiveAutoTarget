using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DefensiveAutoTarget
{
    [BepInPlugin("com.defensiveautotarget", "Defensive Auto Target", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ConfigEntry<KeyCode> AutoTargetKey;
        private Harmony _harmony;

        private void Awake()
        {
            AutoTargetKey = Config.Bind(
                "Controls",
                "autoTargetMissileKey",
                KeyCode.None,
                "Key to automatically target the nearest incoming missile. Set to None to disable."
            );

            _harmony = new Harmony("com.defensiveautotarget");
            _harmony.PatchAll();

            Logger.LogInfo("Defensive Auto Target loaded.");
        }

        public static void AutoTargetNearestMissile(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.disabled || aircraft.weaponManager == null)
                return;

            MissileWarning missileWarning = aircraft.GetComponent<MissileWarning>();
            if (missileWarning == null)
                return;

            if (!missileWarning.TryGetNearestIncoming(out Missile missile))
                return;

            CombatHUD combatHud = SceneSingleton<CombatHUD>.i;

            combatHud.DeselectAll();

            if (!combatHud.MarkerExists(missile))
                combatHud.CreateMarker(missile.persistentID);

            combatHud.SelectUnit(missile);
        }

        [HarmonyPatch(typeof(PilotPlayerState), "PlayerControls")]
        private class AutoTargetMissilePatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (AutoTargetKey.Value == KeyCode.None)
                    return;
                if (!Input.GetKeyDown(AutoTargetKey.Value))
                    return;

                Aircraft aircraft = SceneSingleton<CombatHUD>.i.aircraft;
                AutoTargetNearestMissile(aircraft);
            }
        }
    }
}
