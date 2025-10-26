using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;
using System.Collections.Generic;

namespace ViewTracker
{
    [Table("element_snapshots")]
    public class ElementSnapshot : BaseModel
    {
        [PrimaryKey("track_id", false)]
        [Column("track_id")]
        public string TrackId { get; set; }

        [PrimaryKey("version_name", false)]
        [Column("version_name")]
        public string VersionName { get; set; }

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

        [Column("category")]
        public string? Category { get; set; }

        [Column("family_name")]
        public string? FamilyName { get; set; }

        [Column("type_name")]
        public string? TypeName { get; set; }

        [Column("mark")]
        public string? Mark { get; set; }

        [Column("level")]
        public string? Level { get; set; }

        [Column("phase_created")]
        public string? PhaseCreated { get; set; }

        [Column("phase_demolished")]
        public string? PhaseDemolished { get; set; }

        [Column("comments")]
        public string? Comments { get; set; }

        [Column("all_parameters")]
        public Dictionary<string, object> AllParameters { get; set; }

        [Column("type_parameters")]
        public Dictionary<string, object> TypeParameters { get; set; }
    }
}
