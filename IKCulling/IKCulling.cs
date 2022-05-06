using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BaseX;
using FrooxEngine;
using FrooxEngine.FinalIK;
using HarmonyLib;
using NeosModLoader;

namespace IKCulling
{
    public class IKCulling : NeosMod
    {
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> AutoSaveConfig =
            new ModConfigurationKey<bool>("AutoSaveConfig", "If true the Config gets saved after every change.", () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> DisableAfkUser =
            new ModConfigurationKey<bool>("DisableAfkUser", "Disable User not in the World.", () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> DisableIkWithoutUser =
            new ModConfigurationKey<bool>("DisableIkWithoutUser", "Disable Ik's without active user.", () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> Enabled =
            new ModConfigurationKey<bool>("Enabled", "IkCulling Enabled.", () => true);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> Fov = new ModConfigurationKey<float>("Fov",
            "Field of view used for IkCulling, can be between 1 and -1.",
            () => 0.5f, false, v => v <= 1f && v >= -1f);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> FovTransitionRange =
            new ModConfigurationKey<float>("FovTransitionRange", "Range beyond the field of view, in which updates will be progressively slowed.",
                () => .1f, false, v => v >= 0f && v <= 1f);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> MaxViewRange =
            new ModConfigurationKey<float>("MaxViewRange", "Maximal view range where IkCulling is always enabled.", () => 30);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> MaxViewTransitionRange =
            new ModConfigurationKey<float>("MaxViewTransitionRange", "Range beyond the maximum viewing distance, in which updates will be progressively slowed.",
                () => 5, false, v => v >= 0);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<float> MinCullingRange =
            new ModConfigurationKey<float>("MinCullingRange",
                "Minimal range for IkCulling, useful in front of a mirror.",
                () => 4);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<int> MinUserCount =
            new ModConfigurationKey<int>("MinUserCount", "Min amount of active users in the world to enable ik culling.",
                () => 3);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> UseOtherUserScale =
            new ModConfigurationKey<bool>("UseOtherUserScale",
                "Should the other user's scale be used for Distance check.", () => false);

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> UseUserScale =
            new ModConfigurationKey<bool>("UseUserScale", "Should user scale be used for Distance check.", () => false);

        public static ModConfiguration Config;

        private static readonly ConditionalWeakTable<VRIKAvatar, FullBodyCalibrator> _calibrators =
            new ConditionalWeakTable<VRIKAvatar, FullBodyCalibrator>();

        private static readonly ConditionalWeakTable<SyncObject, VRIKAvatar> ikSolvers = new ConditionalWeakTable<SyncObject, VRIKAvatar>();
        private static readonly ConditionalWeakTable<VRIKAvatar, ProgressiveUpdateData> ikUpdateData = new ConditionalWeakTable<VRIKAvatar, ProgressiveUpdateData>();

        private static bool _disableAfkUser = true;

        private static bool _disableIkWithoutUser = true;

        private static bool _enabled = true;

        private static float _fov = 0.7f;

        private static float _fovTransitionRange = .1f;

        private static float _maxViewRange = 30;

        private static float _maxViewTransitionRange = 5;

        private static float _minCullingRange = 4;

        private static int _minUserCount = 1;

        private static int _useOtherUserScale;

        private static int _useUserScale;

        public override string Author => "KyuubiYoru";

        public override string Link => "https://github.com/KyuubiYoru/IkCulling";

        public override string Name => "IKCulling";

        public override string Version => "1.4.0";

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("net.KyuubiYoru.IkCulling");
            harmony.PatchAll();

            Config = GetConfiguration();
            Config.OnThisConfigurationChanged += RefreshConfigState;

            Config.Save(true);

            RefreshConfigState();
        }

        internal static float getProgressiveDeltaTime(SyncObject instance)
        {
            //if (!ikSolvers.TryGetValue(instance, out var solver) || !ikUpdateData.TryGetValue(solver, out var updateData))
            //return instance.Time.Delta;
            // Msg("Updating " + instance.ReferenceID + " at progress " + ikUpdateData.GetOrCreateValue(ikSolvers.GetOrCreateValue(instance)).UpdateProgress + " with dt=" + ikUpdateData.GetOrCreateValue(ikSolvers.GetOrCreateValue(instance)).Delta);

            return ikUpdateData.GetOrCreateValue(ikSolvers.GetOrCreateValue(instance)).Delta;
        }

        private void RefreshConfigState(ConfigurationChangedEvent configurationChangedEvent = null)
        {
            _enabled = Config.GetValue(Enabled);
            _disableAfkUser = Config.GetValue(DisableAfkUser);
            _disableIkWithoutUser = Config.GetValue(DisableIkWithoutUser);
            _minUserCount = Config.GetValue(MinUserCount);
            _useUserScale = Config.GetValue(UseUserScale) ? 1 : 0;
            _useOtherUserScale = Config.GetValue(UseOtherUserScale) ? 1 : 0;
            _fov = Config.GetValue(Fov);
            _fovTransitionRange = MathX.Clamp(_fov - Config.GetValue(FovTransitionRange), -1, 1);
            _minCullingRange = Config.GetValue(MinCullingRange);
            _maxViewRange = Config.GetValue(MaxViewRange);
            _maxViewTransitionRange = _maxViewRange + Config.GetValue(MaxViewTransitionRange);

            if (Config.GetValue(AutoSaveConfig) || Equals(configurationChangedEvent?.Key, AutoSaveConfig))
                Config.Save(true);
        }

        [HarmonyPatch(typeof(FullBodyCalibrator))]
        public class FullBodyCalibratorPath
        {
            [HarmonyPostfix]
            [HarmonyPatch("OnAttach")]
            private static void OnAttachPostfix(FullBodyCalibrator __instance)
            {
                var vrikAvatar = Traverse.Create(__instance).Field("_platformBody").Field("_vrIkAvatar").GetValue<SyncRef<VRIKAvatar>>();
                vrikAvatar.OnTargetChange += reference => _calibrators.Add(reference, null);
            }
        }

        [HarmonyPatch(typeof(VRIKAvatar))]
        public class IkCullingPatch
        {
            private static float getUpdateAmount(VRIKAvatar __instance)
            {
                if (!_enabled // IKCulling is Disabled
                 || __instance.IsUnderLocalUser // Always Update local IK
                 || __instance.Slot.World.UserCount < _minUserCount // Too few Users in World
                 || _calibrators.TryGetValue(__instance, out _)) // Fullbody Avatar Calibrator IK
                    return 1;

                if (!__instance.Enabled // IK Disabled
                 || __instance.LocalUser.HeadDevice == HeadOutputDevice.Headless // No IK for Headless
                 || (_disableIkWithoutUser && !__instance.IsEquipped) // Disable for empty Avatars
                 || (_disableAfkUser && __instance.Slot.ActiveUser != null && !__instance.Slot.ActiveUser.IsPresentInWorld)) // Disable for AFK Users
                    return 0;

                float3 playerPos = __instance.Slot.World.LocalUserViewPosition;
                floatQ playerViewRot = __instance.Slot.World.LocalUserViewRotation;
                float3 ikPos = __instance.HeadProxy.GlobalPosition;

                float3 dirToIk = (ikPos - playerPos).Normalized;
                float3 viewDir = playerViewRot * float3.Forward;

                float dot = MathX.Dot(dirToIk, viewDir);
                float dist = MathX.Distance(playerPos, ikPos);

                var localScale = __instance.LocalUserRoot.GlobalScale;
                dist *= (2 * localScale) / ((2 - _useUserScale) * localScale);

                var otherScale = __instance.Slot.ActiveUser?.Root.GlobalScale ?? 1;
                dist *= (2 * otherScale) / ((2 - _useOtherUserScale) * otherScale);

                if (dist <= _minCullingRange || (dist <= _maxViewRange && dot >= _fov))
                    return 1;

                return MathX.Clamp01(MathX.Remap(dist, _maxViewTransitionRange, _maxViewRange, 0, 1))
                        * MathX.Clamp01(MathX.Remap(dot, _fovTransitionRange, _fov, 0, 1));
            }

            [HarmonyPostfix]
            [HarmonyPatch("OnAwake")]
            private static void OnAwakePostfix(VRIKAvatar __instance)
            {
                ikUpdateData.Add(__instance, new ProgressiveUpdateData());

                __instance.RunInUpdates(0, () =>
                {
                    var solver = __instance.IK.Target.Solver;

                    ikSolvers.Add(solver, __instance);
                    ikSolvers.Add(solver.spine, __instance);
                    ikSolvers.Add(solver.leftArm, __instance);
                    ikSolvers.Add(solver.rightArm, __instance);
                    ikSolvers.Add(solver.leftLeg, __instance);
                    ikSolvers.Add(solver.rightLeg, __instance);
                    ikSolvers.Add(solver.locomotion, __instance);
                });
            }

            [HarmonyPostfix]
            [HarmonyPatch("OnCommonUpdate")]
            private static void OnCommonUpdatePostfix(VRIKAvatar __instance)
            {
                var updateData = ikUpdateData.GetOrCreateValue(__instance);
                Msg("Postfix with progress: " + updateData.UpdateProgress);

                if (updateData.UpdateProgress >= 1)
                {
                    updateData.UpdateProgress = 0;
                    updateData.Delta = 0;
                }
            }

            [HarmonyPrefix]
            [HarmonyPatch("OnCommonUpdate")]
            private static bool OnCommonUpdatePrefix(VRIKAvatar __instance)
            {
                var updateData = ikUpdateData.GetOrCreateValue(__instance);
                var updateAmount = getUpdateAmount(__instance);
                updateData.UpdateProgress += updateAmount;
                Msg("Changing update progress by " + updateAmount + " to " + updateData.UpdateProgress);
                updateData.Delta += __instance.Time.Delta;

                return updateData.UpdateProgress >= 1;
            }
        }
    }
}