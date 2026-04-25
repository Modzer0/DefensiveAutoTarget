using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using Steamworks;
using System;
using System.Collections;
using UnityEngine;

namespace DefensiveAutoTarget
{
    [BepInPlugin(PluginGUID, "Defensive Auto Target", "0.5.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.defensiveautotarget";
        private const string LobbyDataKey = "dat_mod";
        private const string LobbyDataValue = "0.5.0";

        public static ConfigEntry<KeyCode> AutoTargetKey;
        public static bool HostHasMod;
        public static Plugin Instance;
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;

            AutoTargetKey = Config.Bind(
                "Controls",
                "autoTargetMissileKey",
                KeyCode.None,
                "Key to automatically target the nearest incoming missile. Set to None to disable."
            );

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            Logger.LogInfo("Defensive Auto Target loaded.");
        }

        private static CSteamID GetCurrentLobby()
        {
            try
            {
                if (SteamLobby.instance == null)
                    return new CSteamID();

                var hostedLobby = (HostedLobbyInstance)AccessTools
                    .Field(typeof(SteamLobby), "_hostedLobby")
                    .GetValue(SteamLobby.instance);

                if (hostedLobby.IsValid)
                    return hostedLobby.Id;

                var joinedLobby = (LobbyInstance)AccessTools
                    .Field(typeof(SteamLobby), "_joinedLobby")
                    .GetValue(SteamLobby.instance);

                if (joinedLobby is PlayerLobbyInstance playerLobby)
                {
                    var prop = AccessTools.Property(typeof(PlayerLobbyInstance), "LobbyId");
                    if (prop != null)
                    {
                        var id = (CSteamID)prop.GetValue(playerLobby);
                        if (id.m_SteamID != 0UL)
                            return id;
                    }
                }
            }
            catch (Exception) { }

            return new CSteamID();
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

        // Host: advertise mod presence in lobby data when hosting
        [HarmonyPatch(typeof(NetworkManagerNuclearOption), "StartHostAsync")]
        private class HostAdvertisePatch
        {
            [HarmonyPostfix]
            private static void Postfix(NetworkManagerNuclearOption __instance)
            {
                __instance.StartCoroutine(SetLobbyData());
            }

            private static IEnumerator SetLobbyData()
            {
                for (int i = 0; i < 10; i++)
                {
                    yield return new WaitForSeconds(1f);
                    var lobbyId = GetCurrentLobby();
                    if (lobbyId.m_SteamID != 0UL)
                    {
                        SteamMatchmaking.SetLobbyData(lobbyId, LobbyDataKey, LobbyDataValue);
                        HostHasMod = true;
                        yield break;
                    }
                }
            }
        }

        // Client: check lobby data after joining to see if host has the mod
        [HarmonyPatch(typeof(SteamLobby), "TryJoinLobby")]
        private class ClientCheckPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (Instance != null)
                    Instance.StartCoroutine(CheckHostMod());
            }

            private static IEnumerator CheckHostMod()
            {
                HostHasMod = false;

                for (int i = 0; i < 10; i++)
                {
                    yield return new WaitForSeconds(1f);
                    var lobbyId = GetCurrentLobby();
                    if (lobbyId.m_SteamID != 0UL)
                    {
                        string val = SteamMatchmaking.GetLobbyData(lobbyId, LobbyDataKey);
                        HostHasMod = !string.IsNullOrEmpty(val);
                        yield break;
                    }
                }
            }
        }

        // Input patch: only fire if host has the mod
        [HarmonyPatch(typeof(PilotPlayerState), "PlayerControls")]
        private class AutoTargetMissilePatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!HostHasMod)
                    return;
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
