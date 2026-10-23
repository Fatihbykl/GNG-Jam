using FuzzyLogicLabs.FuzzyIDE.Editor.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.TestRunner
{
    /// <summary>
    /// Connects <see cref="TestRunnerBridge"/> to the Unity Test Framework. This assembly only compiles when the
    /// com.unity.test-framework package is installed, so the editor itself never depends on it.
    /// </summary>
    [InitializeOnLoad]
    internal static class TestRunnerIntegration
    {
        private static readonly TestRunnerApi s_Api;

        static TestRunnerIntegration()
        {
            s_Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_Api.hideFlags = HideFlags.HideAndDontSave;
            // Registered on every domain load: Play Mode runs reload the domain and report into the new one.
            s_Api.RegisterCallbacks(new Callbacks());
            TestRunnerBridge.Runner = Run;
        }

        private static void Run(string[] patterns, bool playMode)
        {
            var filter = new Filter { testMode = playMode ? TestMode.PlayMode : TestMode.EditMode, groupNames = patterns };
            s_Api.Execute(new ExecutionSettings(filter));
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result) => TestRunnerBridge.ReportRunFinished();

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test == null || result.Test.IsSuite) return;
                TestOutcome outcome;
                switch (result.TestStatus)
                {
                    case TestStatus.Passed: outcome = TestOutcome.Passed; break;
                    case TestStatus.Failed: outcome = TestOutcome.Failed; break;
                    default: outcome = TestOutcome.Skipped; break;
                }
                TestRunnerBridge.ReportResult(result.Test.FullName, outcome, result.Message, result.StackTrace, result.Duration);
            }
        }
    }
}
