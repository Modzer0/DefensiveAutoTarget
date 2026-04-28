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
    [BepInPlugin(PluginGUID, "Defensive Auto Target", "0.5.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.defensiveautotarget";
        private const string LobbyDataKey = "dat_mod";
        private const string MemberDataKey = "dat_mod";
        private const string ModVersion = "0.5.1";

        public static ConfigEntry<KeyCode> AutoTargetKey;
        public static bool ModEnabled;
        public static bool IsSinglePlayer;
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

        /// <summary>
        /// Check if every member in the lobby has set their dat_mod member data.
        /// </summary>
        private static bool AllLobbyMembersHaveMod(CSteamID lobbyId)
        {
            int count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            for (int i = 0; i < count; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i);
                string val = SteamMatchmaking.GetLobbyMemberData(lobbyId, member, MemberDataKey);
                if (string.IsNullOrEmpty(val))
                    return false;
            }
            return count > 0;
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

        // Single player: enable immediately, no lobby needed.
        // Multiplayer host: advertise in lobby data, set own member data, start polling.
        [HarmonyPatch(typeof(NetworkManagerNuclearOption), "StartHostAsync")]
        private class HostStartPatch
        {
            [HarmonyPostfix]
            private static void Postfix(NetworkManagerNuclearOption __instance, HostOptions options)
            {
                IsSinglePlayer = options.SocketType == SocketType.Offline;

                if (IsSinglePlayer)
                {
                    ModEnabled = true;
                    return;
                }

                // Multiplayer host: set lobby data + own member data, then poll
                ModEnabled = false;
                __instance.StartCoroutine(HostSetup());
            }

            private static IEnumerator HostSetup()
            {
                for (int i = 0; i < 10; i++)
                {
                    yield return new WaitForSeconds(1f);
                    var lobbyId = GetCurrentLobby();
                    if (lobbyId.m_SteamID != 0UL)
                    {
                        SteamMatchmaking.SetLobbyData(lobbyId, LobbyDataKey, ModVersion);
                        SteamMatchmaking.SetLobbyMemberData(lobbyId, MemberDataKey, ModVersion);
                        ModEnabled = AllLobbyMembersHaveMod(lobbyId);
                        yield break;
                    }
                }
            }
        }

        // Client joining: set own member data so host and others can see it.
        [HarmonyPatch(typeof(SteamLobby), "TryJoinLobby")]
        private class ClientJoinPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                IsSinglePlayer = false;
                ModEnabled = false;
                if (Instance != null)
                    Instance.StartCoroutine(ClientSetup());
            }

            private static IEnumerator ClientSetup()
            {
                for (int i = 0; i < 10; i++)
                {
                    yield return new WaitForSeconds(1f);
                    var lobbyId = GetCurrentLobby();
                    if (lobbyId.m_SteamID != 0UL)
                    {
                        // Advertise that we have the mod
                        SteamMatchmaking.SetLobbyMemberData(lobbyId, MemberDataKey, ModVersion);

                        // Check if host has the mod (lobby data)
                        string hostVal = SteamMatchmaking.GetLobbyData(lobbyId, LobbyDataKey);
                        if (string.IsNullOrEmpty(hostVal))
                        {
                            ModEnabled = false;
                            yield break;
                        }

                        // Check all members
                        ModEnabled = AllLobbyMembersHaveMod(lobbyId);
                        yield break;
                    }
                }
            }
        }

        // Re-check whenever a player spawns (joins mid-game).
        // This runs on the host only (SpawnCharacter is server-side).
        [HarmonyPatch(typeof(NetworkManagerNuclearOption), "SpawnCharacter")]
        private class PlayerSpawnPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (IsSinglePlayer)
                    return;
                if (Instance != null)
                    Instance.StartCoroutine(RecheckAfterDelay());
            }

            private static IEnumerator RecheckAfterDelay()
            {
                // Give the new player time to set their member data
                yield return new WaitForSeconds(5f);
                var lobbyId = GetCurrentLobby();
                if (lobbyId.m_SteamID != 0UL)
                    ModEnabled = AllLobbyMembersHaveMod(lobbyId);
            }
        }

        // Reset on disconnect.
        [HarmonyPatch(typeof(NetworkManagerNuclearOption), "Stop")]
        private class ResetOnDisconnectPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                ModEnabled = false;
                IsSinglePlayer = false;
            }
        }

        // Input: only fire if mod is enabled (single player or all players have it).
        [HarmonyPatch(typeof(PilotPlayerState), "PlayerControls")]
        private class AutoTargetMissilePatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!ModEnabled)
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
