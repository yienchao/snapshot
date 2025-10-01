using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace ViewTracker
{
    [Table("Projects")]
    public class ProjectRecord : BaseModel
    {
        [PrimaryKey("uuid")]
        public Guid Uuid { get; set; }

        [Column("project_name")]
        public string ProjectName { get; set; }

        [Column("created_at")]
        public DateTime CreatedAt { get; set; }
    }
}
