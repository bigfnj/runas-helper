namespace RunAsHelper
{
    /// <summary>
    /// Shared single-instance identity. The mutex is held by the running tray
    /// process; other components (e.g. the validation dialog) probe it to tell
    /// whether the tray is up.
    /// </summary>
    internal static class AppInstance
    {
        // Unique name — prevents collisions with other apps on the system.
        public const string MutexName = @"Global\RunAsHelper_{3A8F2C1D-7E4B-4F9A-A2D6-8C5F1B3E9072}";
    }
}
