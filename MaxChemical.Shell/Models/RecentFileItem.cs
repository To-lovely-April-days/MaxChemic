using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Models
{
    public sealed class RecentFileItem
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTime LastAccessed { get; set; }
        public string DisplayName => System.IO.Path.GetFileName(FileName);
    }
}
