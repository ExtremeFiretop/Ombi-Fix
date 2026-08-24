using System.Collections.Generic;
using System.Linq;

namespace Ombi.Schedule.Jobs.Plex.Models
{
    public class ProcessedContent
    {
        public IEnumerable<string> Content { get; set; }
        public IEnumerable<int> Episodes { get; set; }

        /// <summary>
        /// Rating keys observed during a complete Plex content scan. This is separate from Content,
        /// which historically tracks only items newly processed during the run.
        /// </summary>
        public HashSet<string> ObservedContentKeys { get; set; } = new HashSet<string>();

        /// <summary>
        /// True only when every configured Plex server completed the full snapshot successfully.
        /// Destructive reconciliation must never run when this is false.
        /// </summary>
        public bool FullSnapshotComplete { get; set; }

        public bool HasProcessedContent => Content?.Any() ?? false;
        public bool HasProcessedEpisodes => Episodes?.Any() ?? false;
    }
}