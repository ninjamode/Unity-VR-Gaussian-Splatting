#if UNITY_EDITOR
using System;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEditor;

namespace Gaussians.FourD.Inference
{
    // Inference Engine 2.6.1 creates the reaper's shared dummy buffer on first Schedule,
    // but registers its cleanup from SubsystemRegistration (Play Mode only).
    // Editor preview can allocate it without ever entering Play Mode. Register the
    // upstream cleanup at the same lifetime boundaries, without reinitializing global
    // resources or releasing buffers owned by another worker during normal use.
    // Temporary workaround: on an Inference Engine upgrade, verify Edit Mode inference
    // followed by assembly reload and Editor exit with native leak detection enabled.
    // Remove this helper and its call site once upstream handles those lifetimes correctly.
    // The version guard disables this hook on other versions; it does not confirm a fix.
    internal static class Gaussian4DInferenceEditorLifetime
    {
        static bool s_Registered;

        internal static void EnsureCleanupRegistered()
        {
            if (s_Registered) return;
            s_Registered = true;
            var assembly = typeof(Worker).Assembly;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
            if (package?.version != "2.6.1") return;
            var method = assembly.GetType("Unity.InferenceEngine.ComputeTensorDataReaper")?
                .GetMethod("CleanupStaticResources", BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                UnityEngine.Debug.LogWarning("Cannot register Inference Engine 2.6.1 Edit Mode cleanup: upstream cleanup method was not found.");
                return;
            }
            var cleanup = (Action)Delegate.CreateDelegate(typeof(Action), method);
            // Match the dependency's existing cleanup exactly. Repeated cleanup is
            // idempotent if Play Mode later registers these same boundaries as well.
            AssemblyReloadEvents.beforeAssemblyReload += cleanup.Invoke;
            EditorApplication.quitting += cleanup.Invoke;
        }
    }
}
#endif
