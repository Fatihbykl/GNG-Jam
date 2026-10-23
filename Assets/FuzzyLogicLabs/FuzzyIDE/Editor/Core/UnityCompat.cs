using System;
using System.Collections.Generic;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using Object = UnityEngine.Object;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    /// <summary>
    /// Wraps Unity APIs that changed across Unity 6 minor versions, so the rest of the code
    /// compiles warning-free on every Unity 6 release.
    /// </summary>
    public static class UnityCompat
    {
        /// <summary>Assemblies loaded in the current domain (Unity 6.5+ replaces AppDomain.GetAssemblies).</summary>
        public static IEnumerable<Assembly> GetLoadedAssemblies()
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }

        /// <summary>First loaded type with this full name, searching every loaded assembly.</summary>
        public static Type FindType(string fullName)
        {
            foreach (var asm in GetLoadedAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>On-disk path of an assembly, or null when unknown (Assembly.Location is "" in Unity 6.5+).</summary>
        public static string GetAssemblyPath(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return null;
#if UNITY_6000_5_OR_NEWER
            string path = assembly.GetLoadedAssemblyPath();
#else
            string path = assembly.Location;
#endif
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>Every object of <paramref name="type"/> in loaded scenes, inactive ones included, in no particular order.</summary>
        public static Object[] FindObjectsIncludingInactive(Type type)
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType(type, FindObjectsInactive.Include);
#elif UNITY_2022_2_OR_NEWER
            return Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType(type, true);
#endif
        }
    }
}
