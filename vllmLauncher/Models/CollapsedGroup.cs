using System.Collections.Generic;

namespace vllmLauncher.Models
{
    /// <summary>
    /// A multi-line command group that was rolled up (卷起): only its first line stays in
    /// FullText, the remaining lines are stored here until the group is expanded (展开).
    /// </summary>
    public class CollapsedGroup
    {
        public int GroupId { get; set; }

        /// <summary>Full text of each hidden line, in document order.</summary>
        public List<string> HiddenLines { get; set; } = new();

        /// <summary>Marker segment lengths per hidden line (parallel to HiddenLines).</summary>
        public List<List<int>> HiddenSegments { get; set; } = new();

        /// <summary>Structurally consistent enough to be re-inserted without losing text.</summary>
        public bool IsConsistent()
        {
            if (HiddenLines.Count == 0 || HiddenLines.Count != HiddenSegments.Count) return false;
            for (int i = 0; i < HiddenLines.Count; i++)
            {
                string line = HiddenLines[i] ?? string.Empty;
                if (line.Contains('\n')) return false;
                var segs = HiddenSegments[i];
                if (segs == null) return false;
                if (line.Length == 0)
                {
                    // blank separator line: hidden verbatim, carries no markers
                    if (segs.Count != 0) return false;
                    continue;
                }
                if (segs.Count == 0) return false;
                int sum = 0;
                foreach (int s in segs) { if (s <= 0) return false; sum += s; }
                if (sum != line.Length) return false;
            }
            return true;
        }
    }
}
