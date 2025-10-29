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
    [Table("door_snapshots")]
    public class DoorSnapshot : BaseModel
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
        [Column("mark")]
        public string? Mark { get; set; }

        [Column("level")]
        public string? Level { get; set; }

        [Column("type_id")]
        public long? TypeId { get; set; }  // FamilySymbol ElementId for type comparison

        // === ALL INSTANCE PARAMETERS (single source of truth) ===
        // Contains ALL instance parameters as ParameterValue objects:
        // - FamilyName, TypeName, Mark, Level
        // - FireRating, PhaseCreated, PhaseDemolished
        // - Comments, Width (if instance), Height (if instance)
        // - ALL custom instance parameters
        [Column("all_parameters")]
        public Dictionary<string, object> AllParameters { get; set; }

        // === ALL TYPE PARAMETERS (single source of truth) ===
        // Contains ALL type parameters as ParameterValue objects:
        // - Width (if type), Height (if type)
        // - FireRating (if type-level)
        // - ALL custom type parameters
        [Column("type_parameters")]
        public Dictionary<string, object> TypeParameters { get; set; }
    }
}
