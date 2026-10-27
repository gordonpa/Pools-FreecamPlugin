using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.Rendering;
using HarmonyLib;
using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;

namespace FreecamPlugin
{
    [BepInPlugin("com.doubao.poolsfreecam", "POOLS Freecam", "1.1.0")]
    public class Plugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        internal static bool FreecamOn = false;
        internal static Camera NewCam;
        internal static Camera OldCam;
        internal static bool OldCamWasEnabled;
        internal static FPSController PlayerController;
        internal static bool PlayerControllerWasEnabled;
        private static Il2CppSystem.Action<ScriptableRenderContext, Camera> RenderCallback;
        private static bool ReadingFreecamInput;
        private static bool MouseInputErrorLogged;
        private static bool RenderCallbackRegistered;
        private static bool RenderCallbackSeen;
        private static float FallbackMouseY;
        internal static float Yaw = 0f;
        internal static float Pitch = 0f;
        internal static bool F1WasDown = false;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("=== POOLS Freecam Plugin v1.1.0 loaded! ===");
            var harmony = new HarmonyLib.Harmony("com.doubao.freecam");
            harmony.PatchAll();
            try
            {
                RenderCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>(OnBeginCameraRendering));
                RenderPipelineManager.beginCameraRendering += RenderCallback;
                RenderCallbackRegistered = true;
                Log.LogInfo("Harmony patched; render callback registered. Press F1.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Freecam] Could not register render callback; using Input.GetAxis fallback: {ex.Message}");
            }
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!RenderCallbackSeen)
            {
                RenderCallbackSeen = true;
                Log.LogInfo("[Freecam] Render callback is active.");
            }

            bool f1Down = (GetAsyncKeyState(0x70) & 0x8000) != 0;
            if (f1Down && !F1WasDown)
            {
                if (FreecamOn) InputGetAxisPatch.DisableFreecam();
                else InputGetAxisPatch.EnableFreecam();
            }
            F1WasDown = f1Down;

            if (!FreecamOn || NewCam == null || camera != NewCam) return;

            float mouseX;
            float mouseY;
            try
            {
                ReadingFreecamInput = true;
                mouseX = Input.GetAxis("Mouse X");
                mouseY = Input.GetAxis("Mouse Y");
            }
            catch (Exception ex)
            {
                if (!MouseInputErrorLogged)
                {
                    Log.LogError($"[Freecam] Could not read mouse input before rendering: {ex}");
                    MouseInputErrorLogged = true;
                }
                return;
            }
            finally
            {
                ReadingFreecamInput = false;
            }

            UpdateFreecamCamera(mouseX, mouseY);
        }

        private static void UpdateFreecamCamera(float mouseX, float mouseY)
        {
            if (NewCam == null) return;

            Yaw += mouseX * 2f;
            Pitch = Mathf.Clamp(Pitch - mouseY * 2f, -90f, 90f);
            NewCam.transform.eulerAngles = new Vector3(Pitch, Yaw, 0f);

            float speed = (GetAsyncKeyState(0x10) & 0x8000) != 0 ? 20f : 5f;
            float x = 0f, y = 0f, z = 0f;
            if ((GetAsyncKeyState(0x57) & 0x8000) != 0) z += 1f;
            if ((GetAsyncKeyState(0x53) & 0x8000) != 0) z -= 1f;
            if ((GetAsyncKeyState(0x41) & 0x8000) != 0) x -= 1f;
            if ((GetAsyncKeyState(0x44) & 0x8000) != 0) x += 1f;
            if ((GetAsyncKeyState(0x45) & 0x8000) != 0) y += 1f;
            if ((GetAsyncKeyState(0x51) & 0x8000) != 0) y -= 1f;

            Vector3 move = NewCam.transform.right * x + NewCam.transform.up * y + NewCam.transform.forward * z;
            if (move.sqrMagnitude > 0.0001f)
                NewCam.transform.position += move.normalized * speed * Time.deltaTime;
        }

        [HarmonyPatch(typeof(Input), "GetAxis", new Type[] { typeof(string) })]
        class InputGetAxisPatch
        {
            static void Postfix(ref float __result, string axisName)
            {
                if (ReadingFreecamInput) return;

                if (!RenderCallbackRegistered)
                {
                    if (axisName == "Mouse Y" && FreecamOn)
                        FallbackMouseY = __result;

                    if (axisName == "Mouse X")
                    {
                        bool f1Down = (GetAsyncKeyState(0x70) & 0x8000) != 0;
                        if (f1Down && !F1WasDown)
                        {
                            if (FreecamOn) DisableFreecam();
                            else EnableFreecam();
                        }
                        F1WasDown = f1Down;

                        if (FreecamOn && NewCam != null)
                        {
                            UpdateFreecamCamera(__result, FallbackMouseY);
                            FallbackMouseY = 0f;
                        }
                    }
                }

                if (!FreecamOn) return;
                if (axisName == "Horizontal" || axisName == "Vertical" ||
                    axisName == "Mouse X" || axisName == "Mouse Y" ||
                    axisName == "Mouse ScrollWheel" || axisName == "Jump" || axisName == "Fire1")
                    __result = 0f;
            }

            internal static void EnableFreecam()
            {
                Camera source = Camera.main;
                if (source == null)
                {
                    Log.LogWarning("[Freecam] Cannot enable: Camera.main was not found.");
                    return;
                }

                OldCamWasEnabled = source.enabled;
                FPSController playerController = FPSController.Instance;
                bool playerControllerWasEnabled = playerController != null && playerController.enabled;
                GameObject cameraObject = null;
                try
                {
                    cameraObject = new GameObject("FreecamCamera");
                    Camera freecam = cameraObject.AddComponent<Camera>();
                    freecam.depth = source.depth + 10f;
                    freecam.fieldOfView = source.fieldOfView;
                    freecam.nearClipPlane = source.nearClipPlane;
                    freecam.farClipPlane = source.farClipPlane;
                    freecam.clearFlags = source.clearFlags;
                    freecam.cullingMask = source.cullingMask;
                    freecam.rect = source.rect;
                    freecam.backgroundColor = source.backgroundColor;
                    freecam.orthographic = source.orthographic;
                    freecam.orthographicSize = source.orthographicSize;
                    cameraObject.transform.position = source.transform.position;
                    cameraObject.transform.rotation = source.transform.rotation;

                    Vector3 euler = cameraObject.transform.eulerAngles;
                    Yaw = euler.y;
                    Pitch = euler.x;
                    OldCam = source;
                    NewCam = freecam;
                    source.enabled = false;
                    if (playerController != null)
                    {
                        PlayerController = playerController;
                        PlayerControllerWasEnabled = playerControllerWasEnabled;
                        playerController.enabled = false;
                        Log.LogInfo($"[Freecam] Disabled player controller on {playerController.name}");
                    }
                    else
                    {
                        Log.LogWarning("[Freecam] FPSController.Instance was not found; player movement may remain active.");
                    }
                    FreecamOn = true;
                    Log.LogInfo("[Freecam] ON");
                }
                catch (Exception ex)
                {
                    if (source != null) source.enabled = OldCamWasEnabled;
                    if (playerController != null) playerController.enabled = playerControllerWasEnabled;
                    if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
                    OldCam = null;
                    NewCam = null;
                    OldCamWasEnabled = false;
                    PlayerController = null;
                    PlayerControllerWasEnabled = false;
                    FreecamOn = false;
                    Log.LogError($"[Freecam] Failed to enable: {ex}");
                }
            }

            internal static void DisableFreecam()
            {
                FreecamOn = false;
                if (OldCam != null) OldCam.enabled = OldCamWasEnabled;
                if (PlayerController != null) PlayerController.enabled = PlayerControllerWasEnabled;
                if (NewCam != null) UnityEngine.Object.Destroy(NewCam.gameObject);
                OldCam = null;
                NewCam = null;
                OldCamWasEnabled = false;
                PlayerController = null;
                PlayerControllerWasEnabled = false;
                FallbackMouseY = 0f;
                Log.LogInfo("[Freecam] OFF");
            }
        }

        [HarmonyPatch(typeof(Input), "GetAxisRaw", new Type[] { typeof(string) })]
        class InputGetAxisRawPatch
        {
            static void Postfix(ref float __result, string axisName)
            {
                if (FreecamOn)
                {
                    if (axisName == "Horizontal" || axisName == "Vertical" ||
                        axisName == "Mouse X" || axisName == "Mouse Y" ||
                        axisName == "Mouse ScrollWheel" || axisName == "Jump" || axisName == "Fire1")
                    {
                        __result = 0f;
                    }
                }
            }
        }
    }
}

