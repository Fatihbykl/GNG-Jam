namespace FuzzyLogicLabs.FuzzyIDE.Editor.Terminal
{
    // Terminal server functionality is now hosted directly within TerminalHost.exe
    // to provide domain-reload resilience and persistent terminal sessions.
    public static class TerminalServer
    {
        public static int GetHttpPort() => 1;
        public static int GetWsPort() => 1;
    }
}
