using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using Object = UnityEngine.Object;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public sealed class LiveTypeHint
    {
        public Type Type;
        public Object Target;
        public Object[] Instances = Array.Empty<Object>();
        public bool FromSelection;
    }

    public sealed class LiveValueResult
    {
        public static readonly LiveValueResult Empty = new LiveValueResult();

        /// <summary>Formatted value per 0-based line of a field / auto-property.</summary>
        public readonly Dictionary<int, string> Values = new Dictionary<int, string>();
        /// <summary>Instance information per line of a type declaration.</summary>
        public readonly Dictionary<int, LiveTypeHint> Types = new Dictionary<int, LiveTypeHint>();
    }

    /// <summary>
    /// Reads field and auto-property values of the open script's live objects by reflection, without touching the
    /// user's source (unlike Watch Live's injected tracker calls). In Play Mode the target is the selected object, the
    /// instance the user picked, or the first instance found; in Edit Mode only a selected object is inspected.
    /// </summary>
    public static class LiveValueProvider
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const double InstanceCacheSeconds = 1.0;

        private static readonly Dictionary<Type, Object> s_PinnedTargets = new Dictionary<Type, Object>();
        private static readonly Dictionary<Type, (Object[] instances, double time)> s_InstanceCache = new Dictionary<Type, (Object[], double)>();
        private static readonly Dictionary<string, Assembly> s_Assemblies = new Dictionary<string, Assembly>();

        /// <summary>Show this instance for its type until it is destroyed or another one is pinned.</summary>
        public static void Pin(Type type, Object target)
        {
            if (type == null) return;
            if (target == null) s_PinnedTargets.Remove(type);
            else s_PinnedTargets[type] = target;
        }

        public static LiveValueResult Collect(string assetPath, List<ScriptMember> members)
        {
            if (members == null || members.Count == 0 || string.IsNullOrEmpty(assetPath)) return LiveValueResult.Empty;
            var assembly = FindAssembly(assetPath);
            if (assembly == null) return LiveValueResult.Empty;

            bool playing = EditorApplication.isPlaying;
            var result = new LiveValueResult();
            foreach (var group in members.Where(m => m.Kind != ScriptMemberKind.Method).GroupBy(m => m.TypeName))
            {
                Type type;
                try { type = assembly.GetType(group.Key); }
                catch (Exception) { continue; }
                if (type == null || type.ContainsGenericParameters) continue;

                var hint = ResolveTarget(type, playing);
                object instance = hint?.Target;
                // Edit Mode without a selected instance: nothing meaningful to show (statics hold defaults anyway).
                if (instance == null && !playing) continue;

                foreach (var member in group)
                {
                    if (member.Kind == ScriptMemberKind.Type)
                    {
                        if (hint != null) result.Types[member.Line] = hint;
                        continue;
                    }
                    if (!member.IsStatic && instance == null) continue;
                    if (TryRead(type, member, instance, out object value))
                        result.Values[member.Line] = LiveValueFormatter.Format(value);
                }
            }
            return result;
        }

        private static LiveTypeHint ResolveTarget(Type type, bool playing)
        {
            if (!typeof(Object).IsAssignableFrom(type)) return null;

            Object selected = null;
            if (typeof(Component).IsAssignableFrom(type))
            {
                var go = Selection.activeGameObject;
                if (go != null) selected = go.GetComponent(type);
            }
            else if (Selection.activeObject != null && type.IsInstanceOfType(Selection.activeObject))
            {
                selected = Selection.activeObject;
            }

            if (!playing)
                return selected != null ? new LiveTypeHint { Type = type, Target = selected, Instances = new[] { selected }, FromSelection = true } : null;

            var instances = GetInstances(type);
            Object target = selected;
            if (target == null && s_PinnedTargets.TryGetValue(type, out var pinned) && pinned != null) target = pinned;
            if (target == null && instances.Length > 0) target = instances[0];
            return new LiveTypeHint { Type = type, Target = target, Instances = instances, FromSelection = selected != null };
        }

        private static Object[] GetInstances(Type type)
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_InstanceCache.TryGetValue(type, out var cached) && now - cached.time < InstanceCacheSeconds) return cached.instances;

            Object[] instances;
            if (typeof(Component).IsAssignableFrom(type))
            {
                instances = UnityCompat.FindObjectsIncludingInactive(type);
                // Stable order for the instance picker.
                Array.Sort(instances, (a, b) => string.CompareOrdinal(a.name, b.name));
            }
            else
            {
                instances = Resources.FindObjectsOfTypeAll(type);
            }
            s_InstanceCache[type] = (instances, now);
            return instances;
        }

        private static bool TryRead(Type type, ScriptMember member, object instance, out object value)
        {
            value = null;
            try
            {
                string fieldName = member.Kind == ScriptMemberKind.AutoProperty ? "<" + member.Name + ">k__BackingField" : member.Name;
                var field = type.GetField(fieldName, Flags);
                if (field == null) return false; // added in the editor but not compiled yet
                value = field.GetValue(field.IsStatic ? null : instance);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Assembly FindAssembly(string assetPath)
        {
            string name;
            try { name = CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath); }
            catch (Exception) { return null; }
            if (string.IsNullOrEmpty(name)) return null;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);

            if (s_Assemblies.TryGetValue(name, out var assembly)) return assembly;
            assembly = UnityCompat.GetLoadedAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (assembly != null) s_Assemblies[name] = assembly;
            return assembly;
        }
    }

    /// <summary>Short, single-line value text for inline hints.</summary>
    public static class LiveValueFormatter
    {
        public const int MaxLength = 60;

        public static string Format(object value)
        {
            string text;
            try { text = FormatCore(value, 0); }
            catch (Exception) { text = "?"; }
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > MaxLength ? text.Substring(0, MaxLength - 1) + "\u2026" : text;
        }

        private static string FormatCore(object value, int depth)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return "\"" + s + "\"";
                case bool b: return b ? "true" : "false";
                case char c: return "'" + c + "'";
                case float f: return Number(f);
                case double d: return d.ToString("0.###", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
                case Vector2 v: return $"({Number(v.x)}, {Number(v.y)})";
                case Vector3 v: return $"({Number(v.x)}, {Number(v.y)}, {Number(v.z)})";
                case Vector4 v: return $"({Number(v.x)}, {Number(v.y)}, {Number(v.z)}, {Number(v.w)})";
                case Vector2Int v: return $"({v.x}, {v.y})";
                case Vector3Int v: return $"({v.x}, {v.y}, {v.z})";
                case Quaternion q: return "euler" + FormatCore(q.eulerAngles, depth);
                case Color col: return $"RGBA({Number(col.r)}, {Number(col.g)}, {Number(col.b)}, {Number(col.a)})";
                case Object uo: return uo != null ? $"{uo.name} ({uo.GetType().Name})" : "null";
                case IFormattable formattable when value.GetType().IsPrimitive: return formattable.ToString(null, CultureInfo.InvariantCulture);
                case ICollection collection: return FormatCollection(collection, depth);
            }

            var type = value.GetType();
            var toString = type.GetMethod("ToString", Type.EmptyTypes);
            if (toString != null && toString.DeclaringType != typeof(object) && toString.DeclaringType != typeof(ValueType))
                return value.ToString();
            return "{" + FriendlyName(type) + "}";
        }

        private static string Number(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FormatCollection(ICollection collection, int depth)
        {
            var type = collection.GetType();
            string name = type.IsArray ? FriendlyName(type.GetElementType()) + "[" + collection.Count + "]" : FriendlyName(type) + "(" + collection.Count + ")";
            if (collection.Count == 0 || depth > 0 || collection is IDictionary) return name;

            var parts = new List<string>();
            foreach (var item in collection)
            {
                if (parts.Count == 4) { parts.Add("\u2026"); break; }
                parts.Add(FormatCore(item, depth + 1));
            }
            return name + " [" + string.Join(", ", parts) + "]";
        }

        public static string FriendlyName(Type type)
        {
            if (type == null) return "?";
            if (!type.IsGenericType) return type.Name;
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }
    }
}
