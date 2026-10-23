using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor.Compilation;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public enum TestOutcome { None, Passed, Failed, Skipped }

    public sealed class TestResultInfo
    {
        public TestOutcome Outcome;
        public string Message;
        public string StackTrace;
        public double Duration;
    }

    /// <summary>
    /// Link between the editor and the Unity Test Framework. The framework is an optional package, so the code that
    /// talks to TestRunnerApi lives in its own assembly (Editor/TestRunner, compiled only when the package exists) and
    /// plugs itself in through <see cref="Runner"/>; results come back through <see cref="ReportResult"/>.
    /// </summary>
    public static class TestRunnerBridge
    {
        /// <summary>Runs tests whose full names match the regex patterns (EditMode or PlayMode). Set by the integration assembly.</summary>
        public static Action<string[], bool> Runner;

        private static readonly Dictionary<string, TestResultInfo> s_Results = new Dictionary<string, TestResultInfo>();
        private static readonly List<Regex> s_Running = new List<Regex>();

        public static bool IsAvailable => Runner != null;
        public static int Version { get; private set; }

        /// <summary>Runs one test (with all its parameterized cases) or, when <paramref name="methodName"/> is null, a whole fixture.</summary>
        public static void Run(string fixtureName, string methodName, string scriptPath)
        {
            if (Runner == null || string.IsNullOrEmpty(fixtureName)) return;
            // NUnit full names match reflection names (nested fixtures as "Outer+Inner").
            string pattern = TestPattern(fixtureName, methodName);
            s_Running.Add(new Regex(pattern));
            Version++;
            Runner(new[] { pattern }, IsPlayModeScript(scriptPath));
        }

        public static string TestPattern(string fixtureFullName, string methodName)
        {
            string prefix = "^" + Regex.Escape(fixtureFullName) + "\\.";
            return methodName == null ? prefix : prefix + Regex.Escape(methodName) + "(\\(.*\\))?$";
        }

        public static void ReportResult(string fullName, TestOutcome outcome, string message, string stackTrace, double duration)
        {
            s_Results[fullName] = new TestResultInfo { Outcome = outcome, Message = message, StackTrace = stackTrace, Duration = duration };
            Version++;
        }

        public static void ReportRunFinished()
        {
            s_Running.Clear();
            Version++;
        }

        public static bool IsRunning(string fullName) => s_Running.Any(r => r.IsMatch(fullName));

        /// <summary>Combined result of a test method's cases (or of every test of a fixture when <paramref name="methodName"/> is null).</summary>
        public static TestResultInfo GetResult(string fixtureFullName, string methodName, out int passed, out int total)
        {
            passed = 0;
            total = 0;
            var pattern = new Regex(TestPattern(fixtureFullName, methodName));
            TestResultInfo worst = null;
            double duration = 0;
            foreach (var kvp in s_Results)
            {
                if (!pattern.IsMatch(kvp.Key)) continue;
                total++;
                duration += kvp.Value.Duration;
                if (kvp.Value.Outcome == TestOutcome.Passed) passed++;
                if (worst == null || Rank(kvp.Value.Outcome) > Rank(worst.Outcome)) worst = kvp.Value;
            }
            if (worst == null) return null;
            return new TestResultInfo { Outcome = worst.Outcome, Message = worst.Message, StackTrace = worst.StackTrace, Duration = duration };
        }

        private static int Rank(TestOutcome outcome) => outcome == TestOutcome.Failed ? 3 : outcome == TestOutcome.Skipped ? 1 : outcome == TestOutcome.Passed ? 2 : 0;

        // Editor-only assemblies hold EditMode tests; anything that also builds for players runs in Play Mode.
        private static bool IsPlayModeScript(string scriptPath)
        {
            try
            {
                string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromScriptPath(scriptPath);
                if (string.IsNullOrEmpty(asmdef))
                    return !(scriptPath ?? "").Replace('\\', '/').Contains("/Editor/");
                string json = File.ReadAllText(asmdef);
                var platforms = Regex.Match(json, "\"includePlatforms\"\\s*:\\s*\\[(?<list>[^\\]]*)\\]");
                return !(platforms.Success && platforms.Groups["list"].Value.Trim() == "\"Editor\"");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
