using EFT;
using EFT.AssetsManager;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Reflection;
using System.Threading;
using UnityEngine;
using YetAnotherTraderMod.Client.Services;

namespace YetAnotherTraderMod.Client.Patches
{
    public sealed class TonyTextureCreateItemAsyncPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            Type[] parameters =
            {
                typeof(Item),
                typeof(ECameraType),
                typeof(IPlayer),
                typeof(bool),
                typeof(GDelegate62),
                typeof(CancellationToken)
            };

            return AccessTools.Method(
                typeof(PoolManagerClass),
                nameof(PoolManagerClass.CreateItemAsync),
                parameters);
        }

        [PatchPrefix]
        public static void Prefix(Item item, bool isAnimated)
        {
            TextureOverrideService.Instance?.OnCreateItem(item, isAnimated);
        }
    }

    public sealed class TonyTextureCreatedGameObjectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(PoolManagerClass),
                nameof(PoolManagerClass.method_2));
        }

        [PatchPostfix]
        public static void Postfix(
            GameObject __result,
            ResourceKey resourceKey,
            PoolManagerClass.PoolsCategory poolCategory)
        {
            TextureOverrideService.Instance?.OnCreatedItemGameObject(resourceKey, __result);
        }
    }

    public sealed class TonyTextureReturnToPoolPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(AssetPoolObject),
                nameof(AssetPoolObject.ReturnToPool));
        }

        [PatchPrefix]
        public static void Prefix(AssetPoolObject __instance)
        {
            TextureOverrideService.Instance?.Restore(__instance);
        }
    }

    public sealed class TonyTextureDestroyPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(AssetPoolObject),
                nameof(AssetPoolObject.OnDestroy));
        }

        [PatchPrefix]
        public static void Prefix(AssetPoolObject __instance)
        {
            TextureOverrideService.Instance?.Restore(__instance);
        }
    }

    public sealed class TonyTextureHotObjectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            Type[] parameters = { typeof(float), typeof(bool) };

            return AccessTools.Method(
                typeof(HotObject),
                nameof(HotObject.SetTemperatureToRenderer),
                parameters);
        }

        [PatchPrefix]
        public static bool Prefix(Renderer ___renderer_0)
        {
            TextureOverrideService service = TextureOverrideService.Instance;
            return service == null || !service.IsPatchedRenderer(___renderer_0);
        }
    }

    public sealed class TonyTextureRainEnablePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(RainCondensator), nameof(RainCondensator.OnEnable));
        }

        [PatchPrefix]
        public static bool Prefix(Renderer ___renderer_0)
        {
            TextureOverrideService service = TextureOverrideService.Instance;
            return service == null || !service.IsPatchedRenderer(___renderer_0);
        }
    }

    public sealed class TonyTextureRainUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(RainCondensator), nameof(RainCondensator.UpdateValues));
        }

        [PatchPrefix]
        public static bool Prefix(Renderer ___renderer_0)
        {
            TextureOverrideService service = TextureOverrideService.Instance;
            return service == null || !service.IsPatchedRenderer(___renderer_0);
        }
    }

    public sealed class TonyTextureRainDisablePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(RainCondensator), nameof(RainCondensator.OnDisable));
        }

        [PatchPrefix]
        public static bool Prefix(Renderer ___renderer_0)
        {
            TextureOverrideService service = TextureOverrideService.Instance;
            return service == null || !service.IsPatchedRenderer(___renderer_0);
        }
    }
}
