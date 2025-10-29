using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;
using System.Collections.Generic;

namespace ViewTracker
{
    /// <summary>
    /// REFACTORED: Minimal schema with all parameters in JSON
    /// Dedicated columns kept ONLY for indexing/queries
    /// </summary>
    [Table("room_snapshots")]
    public class RoomSnapshot : BaseModel
    {
        // === PRIMARY KEY ===
        [PrimaryKey("track_id", false)]
        [Column("track_id")]
        public string TrackId { get; set; }

        [PrimaryKey("version_name", false)]
        [Column("version_name")]
        public string VersionName { get; set; }

        // === METADATA ===
        [Column("project_id")]
        public Guid ProjectId { get; set; }

        [Column("file_name")]
        public string FileName { get; set; }

        [Column("snapshot_date")]
        public DateTime? SnapshotDate { get; set; }

        [Column("created_by")]
        public string? CreatedBy { get; set; }

        [Column("is_official")]
        public bool IsOfficial { get; set; }

        // === KEY IDENTIFIERS (for fast queries only) ===
        [Column("room_number")]
        public string? RoomNumber { get; set; }

        [Column("level")]
        public string? Level { get; set; }

        // === POSITION DATA (for recreating deleted/unplaced rooms) ===
        [Column("position_x")]
        public double? PositionX { get; set; }

        [Column("position_y")]
        public double? PositionY { get; set; }

        [Column("position_z")]
        public double? PositionZ { get; set; }

        // === READ-ONLY CALCULATED VALUES (for display/reporting only) ===
        [Column("area")]
        public double? Area { get; set; }

        [Column("perimeter")]
        public double? Perimeter { get; set; }

        [Column("volume")]
        public double? Volume { get; set; }

        [Column("unbound_height")]
        public double? UnboundHeight { get; set; }

        // === ALL INSTANCE PARAMETERS (single source of truth) ===
        // Contains ALL user-editable parameters as ParameterValue objects:
        // - RoomName, Department, Occupancy, Phase
        // - BaseFinish, CeilingFinish, WallFinish, FloorFinish
        // - Comments, Occupant
        // - ALL custom parameters (FINI_*, etc.)
        [Column("all_parameters")]
        public Dictionary<string, object> AllParameters { get; set; }
    }
}