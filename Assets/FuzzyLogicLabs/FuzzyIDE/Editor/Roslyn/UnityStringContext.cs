using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn
{
    public enum UnityStringKind { None, Tag, Layer, SortingLayer, Scene, InputAxis, AnimatorState, AnimatorParameter, AnimatorName, ResourcePath, ShaderName }

    /// <summary>
    /// Names that only exist in the Unity project (Tag Manager, Build Settings, animator controllers...). Captured on
    /// the main thread by <see cref="Core.UnityProjectData"/> and read by analysis threads; never mutated once published.
    /// </summary>
    public sealed class UnityProjectSnapshot
    {
        public static readonly UnityProjectSnapshot Empty = new UnityProjectSnapshot();

        public string[] Tags = Array.Empty<string>();
        public string[] Layers = Array.Empty<string>();
        public string[] SortingLayers = Array.Empty<string>();
        /// <summary>Scenes enabled in Build Settings: names and asset paths (both are accepted by LoadScene).</summary>
        public string[] SceneNames = Array.Empty<string>();
        public string[] ScenePaths = Array.Empty<string>();
        public string[] InputAxes = Array.Empty<string>();
        public string[] AnimatorStates = Array.Empty<string>();
        public string[] AnimatorParameters = Array.Empty<string>();
        /// <summary>Resources.Load paths: relative to a Resources folder, without extension.</summary>
        public string[] ResourcePaths = Array.Empty<string>();
        public string[] Shaders = Array.Empty<string>();

        public string[] GetValues(UnityStringKind kind)
        {
            switch (kind)
            {
                case UnityStringKind.Tag: return Tags;
                case UnityStringKind.Layer: return Layers;
                case UnityStringKind.SortingLayer: return SortingLayers;
                case UnityStringKind.Scene: return SceneNames;
                case UnityStringKind.InputAxis: return InputAxes;
                case UnityStringKind.AnimatorState: return AnimatorStates;
                case UnityStringKind.AnimatorParameter: return AnimatorParameters;
                case UnityStringKind.AnimatorName: return AnimatorStates.Concat(AnimatorParameters).Distinct().ToArray();
                case UnityStringKind.ResourcePath: return ResourcePaths;
                case UnityStringKind.ShaderName: return Shaders;
                default: return Array.Empty<string>();
            }
        }
    }

    public struct UnityStringIssue
    {
        public Microsoft.CodeAnalysis.Text.TextSpan Span;
        public UnityStringKind Kind;
        public string Value;
        public string Message;
    }

    /// <summary>
    /// Recognises string literals passed to Unity APIs that take project names (CompareTag, LayerMask.NameToLayer,
    /// SceneManager.LoadScene, Animator.Play, Input.GetAxis, Resources.Load, Shader.Find...).
    /// </summary>
    public static class UnityStringContext
    {
        public const string DiagnosticId = "FZU001";

        private struct ApiRule
        {
            public UnityStringKind Kind;
            public string[] Types;   // containing types accepted when the call binds; null = any
            public bool AnyArgument; // params string[] (LayerMask.GetMask)
        }

        private static readonly Dictionary<string, ApiRule> s_Methods = new Dictionary<string, ApiRule>
        {
            { "CompareTag", Rule(UnityStringKind.Tag, "Component", "GameObject") },
            { "FindWithTag", Rule(UnityStringKind.Tag, "GameObject") },
            { "FindGameObjectWithTag", Rule(UnityStringKind.Tag, "GameObject") },
            { "FindGameObjectsWithTag", Rule(UnityStringKind.Tag, "GameObject") },
            { "NameToLayer", Rule(UnityStringKind.Layer, "LayerMask") },
            { "GetMask", new ApiRule { Kind = UnityStringKind.Layer, Types = new[] { "LayerMask" }, AnyArgument = true } },
            { "NameToID", Rule(UnityStringKind.SortingLayer, "SortingLayer") },
            { "GetLayerValueFromName", Rule(UnityStringKind.SortingLayer, "SortingLayer") },
            { "LoadScene", Rule(UnityStringKind.Scene, "SceneManager", "EditorSceneManager") },
            { "LoadSceneAsync", Rule(UnityStringKind.Scene, "SceneManager") },
            { "UnloadSceneAsync", Rule(UnityStringKind.Scene, "SceneManager") },
            { "GetSceneByName", Rule(UnityStringKind.Scene, "SceneManager") },
            { "GetAxis", Rule(UnityStringKind.InputAxis, "Input") },
            { "GetAxisRaw", Rule(UnityStringKind.InputAxis, "Input") },
            { "GetButton", Rule(UnityStringKind.InputAxis, "Input") },
            { "GetButtonDown", Rule(UnityStringKind.InputAxis, "Input") },
            { "GetButtonUp", Rule(UnityStringKind.InputAxis, "Input") },
            { "Play", Rule(UnityStringKind.AnimatorState, "Animator") },
            { "PlayInFixedTime", Rule(UnityStringKind.AnimatorState, "Animator") },
            { "CrossFade", Rule(UnityStringKind.AnimatorState, "Animator") },
            { "CrossFadeInFixedTime", Rule(UnityStringKind.AnimatorState, "Animator") },
            { "SetTrigger", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "ResetTrigger", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "SetBool", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "GetBool", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "SetFloat", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "GetFloat", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "SetInteger", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "GetInteger", Rule(UnityStringKind.AnimatorParameter, "Animator") },
            { "StringToHash", Rule(UnityStringKind.AnimatorName, "Animator") },
            { "Load", Rule(UnityStringKind.ResourcePath, "Resources") },
            { "LoadAll", Rule(UnityStringKind.ResourcePath, "Resources") },
            { "LoadAsync", Rule(UnityStringKind.ResourcePath, "Resources") },
            { "Find", Rule(UnityStringKind.ShaderName, "Shader") },
        };

        // Member names whose string value is a project name: x.tag == "Player", renderer.sortingLayerName = "UI".
        private static readonly Dictionary<string, UnityStringKind> s_Properties = new Dictionary<string, UnityStringKind>
        {
            { "tag", UnityStringKind.Tag },
            { "sortingLayerName", UnityStringKind.SortingLayer },
        };

        private static ApiRule Rule(UnityStringKind kind, params string[] types) => new ApiRule { Kind = kind, Types = types };

        /// <summary>The string literal at <paramref name="position"/> (caret inside the quotes) and what it names.</summary>
        public static UnityStringKind GetContext(SyntaxNode root, SemanticModel model, int position, out LiteralExpressionSyntax literal)
        {
            literal = null;
            if (root == null || position <= 0 || position > root.FullSpan.End) return UnityStringKind.None;

            var token = root.FindToken(position - 1);
            if (!token.IsKind(SyntaxKind.StringLiteralToken)) return UnityStringKind.None;
            // Caret must be after the opening quote and before the closing one (an unterminated literal runs to its end).
            bool terminated = token.Text.Length >= 2 && token.Text.EndsWith("\"") && !token.ContainsDiagnostics;
            if (position <= token.SpanStart || (terminated && position >= token.Span.End))
                return UnityStringKind.None;
            if (token.Text.StartsWith("@")) return UnityStringKind.None;

            literal = token.Parent as LiteralExpressionSyntax;
            return literal == null ? UnityStringKind.None : Classify(literal, model);
        }

        public static UnityStringKind Classify(LiteralExpressionSyntax literal, SemanticModel model)
        {
            if (literal == null || !literal.IsKind(SyntaxKind.StringLiteralExpression)) return UnityStringKind.None;

            var parent = literal.Parent;
            if (parent is ArgumentSyntax argument && argument.Parent is ArgumentListSyntax args && args.Parent is InvocationExpressionSyntax invocation)
            {
                string name = GetMethodName(invocation.Expression);
                if (name == null || !s_Methods.TryGetValue(name, out var rule)) return UnityStringKind.None;
                if (!rule.AnyArgument && args.Arguments.IndexOf(argument) != 0) return UnityStringKind.None;
                if (!IsFromTypes(invocation, model, rule.Types)) return UnityStringKind.None;
                return rule.Kind;
            }

            // gameObject.tag == "Player", "Player" != other.tag
            if (parent is BinaryExpressionSyntax binary && (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)))
            {
                var other = binary.Left == literal ? binary.Right : binary.Left;
                return PropertyKind(other);
            }

            // renderer.sortingLayerName = "UI"
            if (parent is AssignmentExpressionSyntax assignment && assignment.Right == literal)
                return PropertyKind(assignment.Left);

            return UnityStringKind.None;
        }

        private static UnityStringKind PropertyKind(ExpressionSyntax expression)
        {
            string name = expression is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.ValueText
                : expression is IdentifierNameSyntax id ? id.Identifier.ValueText : null;
            return name != null && s_Properties.TryGetValue(name, out var kind) ? kind : UnityStringKind.None;
        }

        private static string GetMethodName(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case MemberAccessExpressionSyntax ma: return ma.Name.Identifier.ValueText;
                case IdentifierNameSyntax id: return id.Identifier.ValueText;
                case GenericNameSyntax g: return g.Identifier.ValueText;
                case MemberBindingExpressionSyntax mb: return mb.Name.Identifier.ValueText;
                default: return null;
            }
        }

        // Without a model (or when the call doesn't bind, e.g. mid-typing) the method name alone decides; a bound call
        // must belong to one of the Unity types so that e.g. a user's own Find(string) isn't treated as Shader.Find.
        private static bool IsFromTypes(InvocationExpressionSyntax invocation, SemanticModel model, string[] types)
        {
            if (model == null || types == null) return true;
            ISymbol symbol;
            try
            {
                var info = model.GetSymbolInfo(invocation);
                symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            }
            catch (ArgumentException)
            {
                return true; // node from a different tree than the model
            }
            if (symbol == null) return true;
            for (var type = symbol.ContainingType; type != null; type = type.BaseType)
            {
                if (types.Contains(type.Name)) return true;
            }
            return false;
        }

        /// <summary>Literals naming something the snapshot doesn't know. Kinds whose list is empty are never reported.</summary>
        public static List<UnityStringIssue> Validate(SyntaxNode root, SemanticModel model, UnityProjectSnapshot snapshot)
        {
            var issues = new List<UnityStringIssue>();
            if (root == null || snapshot == null) return issues;

            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                var kind = Classify(literal, model);
                if (kind == UnityStringKind.None || kind == UnityStringKind.ShaderName) continue; // shaders: too many sources to be sure

                string value = literal.Token.ValueText;
                if (string.IsNullOrEmpty(value)) continue;
                if (IsKnown(kind, value, snapshot)) continue;

                var known = snapshot.GetValues(kind);
                if (known.Length == 0) continue;

                string message = DescribeMissing(kind, value);
                string suggestion = ClosestMatch(value, known);
                if (suggestion != null) message += $" Did you mean \"{suggestion}\"?";
                issues.Add(new UnityStringIssue { Span = literal.Span, Kind = kind, Value = value, Message = message });
            }
            return issues;
        }

        public static bool IsKnown(UnityStringKind kind, string value, UnityProjectSnapshot snapshot)
        {
            switch (kind)
            {
                case UnityStringKind.Scene:
                    // LoadScene accepts the name, the asset path ("Assets/Scenes/Main.unity") or the path without extension.
                    return snapshot.SceneNames.Contains(value) || snapshot.ScenePaths.Any(p => p == value || StripExtension(p) == value || p.EndsWith("/" + value + ".unity", StringComparison.Ordinal));
                case UnityStringKind.AnimatorState:
                case UnityStringKind.AnimatorName:
                    // States may be addressed as "Layer.State" or "Layer.SubMachine.State".
                    int dot = value.LastIndexOf('.');
                    string shortName = dot >= 0 ? value.Substring(dot + 1) : value;
                    return snapshot.GetValues(kind).Contains(value) || snapshot.GetValues(kind).Contains(shortName);
                default:
                    return snapshot.GetValues(kind).Contains(value);
            }
        }

        private static string StripExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot > path.LastIndexOf('/') ? path.Substring(0, dot) : path;
        }

        private static string DescribeMissing(UnityStringKind kind, string value)
        {
            switch (kind)
            {
                case UnityStringKind.Tag: return $"Tag \"{value}\" is not defined in the Tag Manager.";
                case UnityStringKind.Layer: return $"Layer \"{value}\" is not defined in the Tag Manager.";
                case UnityStringKind.SortingLayer: return $"Sorting layer \"{value}\" is not defined.";
                case UnityStringKind.Scene: return $"Scene \"{value}\" is not in the Build Settings scene list.";
                case UnityStringKind.InputAxis: return $"Input axis \"{value}\" is not defined in the Input Manager.";
                case UnityStringKind.AnimatorState: return $"No animator controller has a state named \"{value}\".";
                case UnityStringKind.AnimatorParameter: return $"No animator controller has a parameter named \"{value}\".";
                case UnityStringKind.AnimatorName: return $"No animator controller has a state or parameter named \"{value}\".";
                case UnityStringKind.ResourcePath: return $"No asset at Resources path \"{value}\".";
                default: return $"Unknown name \"{value}\".";
            }
        }

        /// <summary>Case-insensitive match, otherwise the nearest name within a small edit distance.</summary>
        public static string ClosestMatch(string value, IEnumerable<string> candidates)
        {
            string best = null;
            int bestDistance = int.MaxValue;
            int limit = Math.Max(2, value.Length / 3);
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return candidate;
                int distance = EditDistance(value.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (distance <= limit && distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static int EditDistance(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 8) return int.MaxValue;
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                var swap = previous; previous = current; current = swap;
            }
            return previous[b.Length];
        }

        public static string Describe(UnityStringKind kind)
        {
            switch (kind)
            {
                case UnityStringKind.Tag: return "Tag";
                case UnityStringKind.Layer: return "Layer";
                case UnityStringKind.SortingLayer: return "Sorting Layer";
                case UnityStringKind.Scene: return "Scene (Build Settings)";
                case UnityStringKind.InputAxis: return "Input Axis";
                case UnityStringKind.AnimatorState: return "Animator State";
                case UnityStringKind.AnimatorParameter: return "Animator Parameter";
                case UnityStringKind.AnimatorName: return "Animator State / Parameter";
                case UnityStringKind.ResourcePath: return "Resources Path";
                case UnityStringKind.ShaderName: return "Shader";
                default: return "";
            }
        }
    }
}
