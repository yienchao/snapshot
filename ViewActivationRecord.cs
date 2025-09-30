using System;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace ViewTracker
{
    [Table("view_activations")]
    public class ViewActivationRecord : BaseModel
    {
        [PrimaryKey("view_unique_id")]
        public string ViewUniqueId { get; set; }
        [Column("file_name")]
        public string FileName { get; set; }
        [Column("view_id")]
        public int ViewId { get; set; }
        [Column("view_name")]
        public string ViewName { get; set; }
        [Column("view_type")]
        public string ViewType { get; set; }
        [Column("last_viewer")]
        public string LastViewer { get; set; }
        [Column("last_activation_date")]
        public string LastActivationDate { get; set; }
        [Column("last_initialization")]
        public string LastInitialization { get; set; }
        [Column("activation_count")]
        public int ActivationCount { get; set; }
    }
}
