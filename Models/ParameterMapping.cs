using System.Collections.Generic;

namespace ViewTracker.Models
{
    public class ParameterMapping
    {
        public string SourceColumn { get; set; }
        public string TargetParameter { get; set; }
    }

    public class MappingPreset
    {
        public string Name { get; set; }
        public List<ParameterMapping> Mappings { get; set; }
    }
}
