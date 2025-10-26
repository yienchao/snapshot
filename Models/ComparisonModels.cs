using System.Collections.Generic;

namespace ViewTracker.Models
{
    /// <summary>
    /// Unified comparison result for all entity types (Rooms, Doors, Elements)
    /// </summary>
    public class ComparisonResult<T>
    {
        public List<T> NewEntities { get; set; } = new List<T>();
        public List<T> ModifiedEntities { get; set; } = new List<T>();
        public List<T> DeletedEntities { get; set; } = new List<T>();
        public List<T> UnplacedEntities { get; set; } = new List<T>(); // Only used by Rooms

        public int TotalChanges => NewEntities.Count + ModifiedEntities.Count + DeletedEntities.Count + UnplacedEntities.Count;
    }

    /// <summary>
    /// Unified entity change for all entity types
    /// </summary>
    public class EntityChange
    {
        public string TrackId { get; set; }
        public string Identifier1 { get; set; } // RoomNumber or Mark
        public string Identifier2 { get; set; } // RoomName or "Family: Type"
        public string ChangeType { get; set; } // New, Modified, Deleted, Unplaced
        public List<string> Changes { get; set; } = new List<string>();
        public List<string> InstanceParameterChanges { get; set; } = new List<string>();
        public List<string> TypeParameterChanges { get; set; } = new List<string>();
    }
}
