namespace vllmLauncher.Models
{
    /// <summary>
    /// Marks a command segment inside ModelConfig.FullText.
    /// Offsets follow the plain-text model: each paragraph is separated by one '\n'.
    /// Segments sharing the same GroupId form one logical (possibly multi-line) command.
    /// </summary>
    public class CommandMarker
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public int GroupId { get; set; }
    }
}
