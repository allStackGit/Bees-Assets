using System.Runtime.InteropServices;
using UnityEngine;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Installs the browser viewport repair from inside the WebGL runtime bundle.
    /// This is deliberately independent of generated index.html so deployments that replace
    /// Build artifacts without replacing the host page still receive the responsive boundary.
    /// </summary>
    internal static class WebGlViewportRuntimeGuard
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void BeesInstallResponsiveViewport();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            BeesInstallResponsiveViewport();
#endif
        }
    }
}
