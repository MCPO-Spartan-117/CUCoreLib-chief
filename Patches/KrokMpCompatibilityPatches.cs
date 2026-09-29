using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using CUCoreLib.Helpers;
using CUCoreLib.Networking;
using CUCoreLib.Registries;
using HarmonyLib;
using UnityEngine;

namespace CUCoreLib.Patches
{
    // The root of all evil...
    internal static class KrokMpCompatibilityPatches
    {
        private const string KrokMpPluginGuid = "KrokoshaCasualtiesMP";
        private const string WorldChunkSyncTypeName = "KrokoshaCasualtiesMP.WorldChunkSync";
        private const string NetObjectRegistryTypeName = "KrokoshaCasualtiesMP.NetObjectRegistry";
        private const string NewObjectSystemTypeName = "KrokoshaCasualtiesMP.NewCoolerObjectPacketWriteReadSystem";
        private const string ItemSetupListenerTypeName = "KrokoshaCasualtiesMP.Item_SetupItems_Listener";
        private static bool _installed;
        private static bool _retryScheduled;
        private static bool _chunkRetryScheduled;
        private static bool _newLoaderPatched;
        private static bool _liquidRegistryPatched;
        private static List<string> _canonicalLiquidOrder;
        private static MethodInfo _serverEnsureItemNetworkRegisteredMethod;

        internal static void Install(Harmony harmony)
        {
            if (harmony != null)
            {
                var chunkType = ResolveLoadedType(WorldChunkSyncTypeName);
                KrokMpWorldChunkPatches.Install(harmony, chunkType);
                if (!KrokMpWorldChunkPatches.IsInstalled && chunkType == null && IsKrokMpExpected())
                    ScheduleChunkRetry(harmony);

                KrokMpHealPatches.Install(harmony);
            }
            if (harmony == null || _installed) return;

            if (!_newLoaderPatched)
            {
                var newObjectSystemType = ResolveLoadedType(NewObjectSystemTypeName);
                if (newObjectSystemType != null)
                {
                    var loadObjectResources = AccessTools.GetDeclaredMethods(newObjectSystemType)
                        ?.Where(method => string.Equals(method.Name, "LoadObjectResource", StringComparison.Ordinal))
                        ?.ToArray();
                    if (loadObjectResources != null)
                    {
                        foreach (var loadObjectResource in loadObjectResources)
                        {
                            harmony.Patch(loadObjectResource,
                                prefix: new HarmonyMethod(typeof(KrokMpCompatibilityPatches),
                                    nameof(LoadObjectResource_Prefix)));
                        }

                        _newLoaderPatched = loadObjectResources.Any();
                    }
                }
            }

            if (!_liquidRegistryPatched && ResolveLoadedType(ItemSetupListenerTypeName) != null)
            {
                var setupItems = AccessTools.Method(typeof(Item), "SetupItems");
                if (setupItems != null)
                {
                    harmony.Patch(setupItems,
                        postfix: new HarmonyMethod(typeof(KrokMpCompatibilityPatches),
                            nameof(RefreshLiquidRegistry_AfterItemSetup))
                        {
                            priority = Priority.Last
                        });
                    _liquidRegistryPatched = true;
                }
            }

            if (!_newLoaderPatched && !_liquidRegistryPatched)
            {
                ScheduleRetry(harmony);
                return;
            }

            _installed = true;
            RefreshLiquidRegistry();
            CUCoreLibPlugin.Log?.LogInfo("CUCoreLib, with friends!");
        }

        // KrokMP's liquid wire IDs are positional indexes into Liquids.Registry, so every peer has to
        // enumerate the same way. The host publishes its order in the multiplayer snapshot and clients
        // adopt it here; null means "this peer is the source, use its own insertion order".
        internal static void SetCanonicalLiquidOrder(List<string> order)
        {
            _canonicalLiquidOrder = order != null && order.Count > 0 ? order : null;
            RefreshLiquidRegistry();
        }

        internal static void RefreshLiquidRegistry()
        {
            try
            {
                if (Liquids.Registry == null) return;

                var listenerType = ResolveLoadedType(ItemSetupListenerTypeName);
                if (listenerType == null) return;

                var idsField = AccessTools.Field(listenerType, "LiquidIdRegistry");
                var reverseIdsField = AccessTools.Field(listenerType, "LiquidNetIdToId");
                if (idsField == null || reverseIdsField == null) return;

                if (Liquids.Registry.Count > byte.MaxValue + 1) return;

                var orderedIds = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (_canonicalLiquidOrder != null)
                    foreach (var id in _canonicalLiquidOrder)
                        if (seen.Add(id)) orderedIds.Add(id);

                foreach (var id in Liquids.Registry.Keys)
                    if (seen.Add(id)) orderedIds.Add(id);

                var count = Math.Min(orderedIds.Count, byte.MaxValue + 1);
                var ids = new Dictionary<string, byte>(count, StringComparer.Ordinal);
                var reverseIds = new string[count];
                for (var index = 0; index < count; index++)
                {
                    // Unknown IDs keep their slot so a missing mod cannot shift every later liquid.
                    if (Liquids.Registry.ContainsKey(orderedIds[index])) ids[orderedIds[index]] = (byte)index;
                    reverseIds[index] = orderedIds[index];
                }

                idsField.SetValue(null, ids);
                reverseIdsField.SetValue(null, reverseIds);
            }
            catch
            {
            }
        }

        internal static void EnsureItemNetworkRegistered(GameObject item)
        {
            if (item == null || !MultiplayerBridge.IsRunning || !MultiplayerBridge.IsServer) return;

            try
            {
                if (_serverEnsureItemNetworkRegisteredMethod == null)
                {
                    var registryType = ResolveLoadedType(NetObjectRegistryTypeName);
                    _serverEnsureItemNetworkRegisteredMethod = AccessTools.Method(registryType,
                        "Server_EnsureItemIsNetworkRegistered", new[] { typeof(GameObject) });
                }

                _serverEnsureItemNetworkRegisteredMethod?.Invoke(null, new object[] { item });
            }
            catch
            {
            }
        }

        private static void RefreshLiquidRegistry_AfterItemSetup()
        {
            RefreshLiquidRegistry();
        }

        private static bool TryResolveResourcePrefab(string resourceStringId, out GameObject prefab)
        {
            prefab = null;
            if (string.IsNullOrWhiteSpace(resourceStringId)) return false;

            var normalized = resourceStringId.Trim();
            if (TryResolveCustomPrefab(normalized, out prefab)) return prefab != null;
            return false;
        }

        private static bool TryResolveCustomPrefab(string resourceStringId, out GameObject prefab)
        {
            prefab = null;
            if (string.IsNullOrWhiteSpace(resourceStringId)) return false;

            var normalized = resourceStringId.Trim();
            var baseId = SpawnIdHelpers.NormalizeSpawnId(normalized);
            var isCustomId = ItemRegistry.TryGetCustomInfo(baseId, out _) ||
                             BuildingEntityRegistry.TryGetDefinition(baseId, out _);
            if (isCustomId)
            {
                prefab = CustomInstantiate.ResolvePrefab(baseId);
                if (prefab != null) return true;
            }

            if (!normalized.StartsWith("KMPSR_", StringComparison.Ordinal)) return false;
            var stripped = normalized.Substring("KMPSR_".Length);
            var strippedBaseId = SpawnIdHelpers.NormalizeSpawnId(stripped);
            if (!ItemRegistry.TryGetCustomInfo(strippedBaseId, out _) &&
                !BuildingEntityRegistry.TryGetDefinition(strippedBaseId, out _)) return false;
            prefab = CustomInstantiate.ResolvePrefab(strippedBaseId);
            return prefab != null;
        }

        private static bool LoadObjectResource_Prefix(string resourceid, object[] __args, ref GameObject __result)
        {
            var pos = default(Vector2);
            if (__args != null && __args.Length > 1 && __args[1] is Vector2 vector)
            {
                pos = vector;
            }

            if (!TryResolveResourcePrefab(resourceid, out var prefab) ||
                prefab == null) return true;

            var instance = CustomInstantiate.PrepareInstantiatedObject(
                CustomInstantiate.InstantiateInActiveScene(prefab, pos, Quaternion.identity));
            if (instance == null)
            {
                __result = null;
                return false;
            }

            __result = instance;
            return false;
        }

        private static void ScheduleRetry(Harmony harmony)
        {
            if (_retryScheduled || !IsKrokMpExpected()) return;

            _retryScheduled = true;
            CUCoreUtils.DelayCall(1f, () =>
            {
                _retryScheduled = false;
                if (_installed) return;

                Install(harmony);
            });
        }

        private static void ScheduleChunkRetry(Harmony harmony)
        {
            if (_chunkRetryScheduled) return;

            _chunkRetryScheduled = true;
            CUCoreUtils.DelayCall(1f, () =>
            {
                _chunkRetryScheduled = false;
                if (!KrokMpWorldChunkPatches.IsInstalled)
                    KrokMpWorldChunkPatches.Install(harmony, ResolveLoadedType(WorldChunkSyncTypeName));
                if (!KrokMpWorldChunkPatches.IsInstalled && IsKrokMpExpected())
                    ScheduleChunkRetry(harmony);
            });
        }

        private static bool IsKrokMpExpected()
        {
            return Chainloader.PluginInfos.ContainsKey(KrokMpPluginGuid);
        }

        private static Type ResolveLoadedType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return null;

            return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }
    }
}
