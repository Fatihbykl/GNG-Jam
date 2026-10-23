using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>
    /// Advances Unity's script compilation pipeline on demand. While assembly reload is locked, Unity starts a
    /// requested compilation only during its own focus-change refresh, which no public API triggers (a synchronous
    /// AssetDatabase.Refresh does not). Ticking the internal pipeline once per editor update while a compilation we
    /// asked for is pending or running starts it immediately. Completion is still observed through the public
    /// CompilationPipeline events, which the pipeline raises from inside this tick.
    /// If the internal API isn't found (another Unity version), <see cref="Available"/> is false and nothing happens.
    /// </summary>
    public static class CompilationTicker
    {
        // UnityEditor.Scripting.ScriptCompilation.CompileStatus
        public const int Idle = 0, Compiling = 1, CompilationStarted = 2, CompilationFailed = 3, CompilationComplete = 4;

        private static MethodInfo s_Tick;
        private static object s_Options;
        private static bool s_Initialized;
        private static bool s_Failed;

        public static bool Available
        {
            get
            {
                Initialize();
                return s_Tick != null && s_Options != null && !s_Failed;
            }
        }

        /// <summary>One pipeline step; returns the CompileStatus value, or -1 when unavailable.</summary>
        public static int Tick()
        {
            if (!Available) return -1;
            try
            {
                var target = EditorUserBuildSettings.activeBuildTarget;
                object result = s_Tick.Invoke(null, new object[] { s_Options, target, ActiveSubtarget(target), Array.Empty<string>(), false });
                return result is IConvertible ? Convert.ToInt32(result) : -1;
            }
            catch (Exception ex)
            {
                // Signature changed: stop trying instead of failing every frame.
                s_Failed = true;
                Debug.LogWarning($"[FuzzyIDE] Compilation pipeline tick unavailable ({(ex.InnerException ?? ex).Message}); compilations will start when Unity refreshes.");
                return -1;
            }
        }

        private static void Initialize()
        {
            if (s_Initialized) return;
            s_Initialized = true;
            try
            {
                var editorAssembly = typeof(CompilationPipeline).Assembly;
                var compilationInterface = editorAssembly.GetType("UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface");
                if (compilationInterface == null) return;
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                s_Tick = compilationInterface.GetMethod("TickCompilationPipeline", flags);
                if (s_Tick == null || s_Tick.GetParameters().Length != 5) { s_Tick = null; return; }

                var assembliesType = editorAssembly.GetType("UnityEditor.Compilation.AssembliesType") ?? typeof(AssembliesType);
                var getOptions = compilationInterface.GetMethod("GetAdditionalEditorScriptCompilationOptions", flags, null, new[] { assembliesType }, null);
                if (getOptions != null)
                    s_Options = getOptions.Invoke(null, new[] { Enum.Parse(assembliesType, "Editor") });
                if (s_Options == null)
                    s_Options = compilationInterface.GetMethod("GetAdditionalEditorScriptCompilationOptions", flags, null, Type.EmptyTypes, null)?.Invoke(null, null);
            }
            catch (Exception)
            {
                s_Tick = null;
                s_Options = null;
            }
        }

        private static int ActiveSubtarget(BuildTarget target)
        {
            try
            {
                var method = typeof(EditorUserBuildSettings).GetMethod("GetActiveSubtargetFor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return method != null ? Convert.ToInt32(method.Invoke(null, new object[] { target })) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
