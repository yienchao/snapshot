using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System;
using System.Collections.Generic;

namespace ViewTracker
{
    [Table("room_snapshots")]
    public class RoomSnapshot : BaseModel
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

        [Column("room_number")]
        public string? RoomNumber { get; set; }

        [Column("room_name")]
        public string? RoomName { get; set; }

        [Column("level")]
        public string? Level { get; set; }

        [Column("area")]
        public double? Area { get; set; }

        [Column("all_parameters")]
        public Dictionary<string, object> AllParameters { get; set; }
    }
}