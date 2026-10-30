using System.Collections.ObjectModel;

namespace vllmLauncher.Models
{
    /// <summary>
    /// Application settings persisted to config.json
    /// </summary>
    public class AppSettings
    {
        public string ServerAddress { get; set; } = string.Empty;
        public bool UseScreen { get; set; } = false;
        public double PasteWaitTime { get; set; } = 3.0;
        public ObservableCollection<ModelConfig> Models { get; set; } = new();
    }
}
