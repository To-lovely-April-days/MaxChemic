using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Models
{
    public class CanvasDevice : INotifyPropertyChanged
    {
        private double _x;
        private double _y;
        private bool _isSelected;
        private string _status = "Connected";
        private string _id;

        public string Id
        {
            get => _id;
            set
            {
                _id = value;
                OnPropertyChanged();
            }
        }

        public IDevice Device { get; set; }

        public double X
        {
            get => _x;
            set
            {
                _x = value;
                OnPropertyChanged();
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                OnPropertyChanged();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
            }
        }

        public CanvasDevice(IDevice device)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
            Id = Guid.NewGuid().ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}