using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Models
{
    public class RegionTypeMenuItem : INotifyPropertyChanged
    {
        public string Header { get; set; }
        public string RegionTypeValue { get; set; }
        public Brush ColorBrush { get; set; }
        public bool IsSeparator { get; set; }
        public ICommand Command { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
