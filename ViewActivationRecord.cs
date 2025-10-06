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
        public string ViewId { get; set; }
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
        [Column("creator_name")]
        public string CreatorName { get; set; }
        [Column("last_changed_by")]
        public string LastChangedBy { get; set; }
        [Column("sheet_number")]
        public string SheetNumber { get; set; }
        [Column("view_number")]
        public string ViewNumber { get; set; }
        [Column("project_id")]
        public Guid ProjectId { get; set; }
    }
}
