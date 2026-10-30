namespace vllmLauncher.Models
{
    /// <summary>
    /// Represents a single vLLM model configuration
    /// </summary>
    public class ModelConfig
    {
        public string Name { get; set; } = string.Empty;
        public string FullText { get; set; } = string.Empty;

        /// <summary>Command segments marked as clickable links inside FullText.</summary>
        public System.Collections.Generic.List<CommandMarker> Commands { get; set; } = new();

        /// <summary>Multi-line command groups currently rolled up (卷起) to a single line.</summary>
        public System.Collections.Generic.List<CollapsedGroup> CollapsedGroups { get; set; } = new();

        public ModelConfig()
        {
        }

        public ModelConfig(string name, string fullText)
        {
            Name = name;
            FullText = fullText;
        }
    }
}
