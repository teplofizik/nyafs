namespace NyaFs.ImageFormat.Plugins
{
    /// <summary>
    /// Ambient registry used by legacy static entry points and the CLI.
    /// The processor replaces it with its session registry; library code should
    /// prefer passing an explicit <see cref="PluginRegistry"/>.
    /// </summary>
    public static class PluginHost
    {
        private static PluginRegistry CurrentRegistry = new PluginRegistry();

        public static PluginRegistry Current => CurrentRegistry;

        public static void SetCurrent(PluginRegistry Registry) =>
            CurrentRegistry = Registry ?? throw new System.ArgumentNullException(nameof(Registry));

        /// <summary>Reset to an empty registry (mainly for tests).</summary>
        public static void Reset() => CurrentRegistry = new PluginRegistry();
    }
}
