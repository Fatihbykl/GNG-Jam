using System.Collections.Concurrent;

namespace FuzzyLogicLabs
{
    public struct TrackedVariableContext
    {
        public string Id;
        public string ScriptPath;
        public string VariableName;
        public string CurrentValue;
    }

    public static class LiveStateTracker
    {
        public static readonly ConcurrentDictionary<string, TrackedVariableContext> State = new ConcurrentDictionary<string, TrackedVariableContext>();

        public static void Track(string id, string scriptPath, string variableName, object value)
        {
            var ctx = new TrackedVariableContext
            {
                Id = id,
                ScriptPath = scriptPath,
                VariableName = variableName,
                CurrentValue = value == null ? "null" : value.ToString()
            };
            State[id] = ctx;
        }

        // Legacy overload for trackers injected before Phase 17.5
        public static void Track(string id, object value)
        {
            string scriptPath = id;
            int lineNumber = 0;
            
            int underscoreIdx = id.LastIndexOf('_');
            if (underscoreIdx >= 0 && underscoreIdx < id.Length - 1)
            {
                scriptPath = id.Substring(0, underscoreIdx);
                int.TryParse(id.Substring(underscoreIdx + 1), out lineNumber);
            }

            Track(id, scriptPath, "Unknown", value);
        }
    }
}
