namespace vllmLauncher.Models
{
    /// <summary>
    /// Identity object shared by all Hyperlink segments of one logical command.
    /// Hover highlight is applied by the view to every hyperlink of the group,
    /// so a multi-line command lights up as a whole.
    /// </summary>
    public class CommandGroup
    {
        public int Id { get; }

        public CommandGroup(int id)
        {
            Id = id;
        }
    }
}
