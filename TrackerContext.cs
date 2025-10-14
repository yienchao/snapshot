using System;

namespace ViewTracker
{
    /// <summary>
    /// Global context for storing the currently selected tracker entity type
    /// </summary>
    public static class TrackerContext
    {
        public enum EntityType
        {
            Room,
            Door,
            Element
        }

        private static EntityType _currentEntityType = EntityType.Room;

        public static EntityType CurrentEntityType
        {
            get => _currentEntityType;
            set
            {
                _currentEntityType = value;
                EntityTypeChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static event EventHandler EntityTypeChanged;

        public static string GetEntityTypeName()
        {
            return CurrentEntityType switch
            {
                EntityType.Room => "Room",
                EntityType.Door => "Door",
                EntityType.Element => "Element",
                _ => "Room"
            };
        }

        public static string GetEntityTypePluralName()
        {
            return CurrentEntityType switch
            {
                EntityType.Room => "Rooms",
                EntityType.Door => "Doors",
                EntityType.Element => "Elements",
                _ => "Rooms"
            };
        }
    }
}
