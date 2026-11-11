namespace GameStartMenu.Library
{
    /// <summary>
    /// Tracks achievements/trophy progress for a game on a specific gaming platform.
    /// </summary>
    public class PlatformCompletion
    {
        public string PlatformName { get; set; } = "";
        public string SubPlatform { get; set; } = "";
        public int Unlocked { get; set; }
        public int Total { get; set; }
        public int Percentage { get; set; }
        public bool IsCompleted100 { get; set; }
        public string ExtraInfo { get; set; } = "";
    }
}
