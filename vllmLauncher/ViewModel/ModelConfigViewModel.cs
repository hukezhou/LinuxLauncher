using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using vllmLauncher.Models;

namespace vllmLauncher.ViewModel
{
    public class ModelConfigViewModel : INotifyPropertyChanged
    {
        private readonly Action<ModelConfigViewModel> _onDelete;
        private ModelConfig _config;
        private bool _isSelected;
        private bool _isDragSource;
        private bool _isDropTarget;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public bool IsDragSource
        {
            get => _isDragSource;
            set { _isDragSource = value; OnPropertyChanged(); }
        }

        public bool IsDropTarget
        {
            get => _isDropTarget;
            set { _isDropTarget = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _config.Name;
            set { _config.Name = value; OnPropertyChanged(); }
        }

        public string FullText
        {
            get => _config.FullText;
            set { _config.FullText = value; OnPropertyChanged(); }
        }

        public ModelConfig Config => _config;

        public ICommand DeleteCommand { get; }

        public ModelConfigViewModel(ModelConfig config, Action<ModelConfigViewModel> onDelete)
        {
            _config = config;
            _onDelete = onDelete;

            DeleteCommand = new RelayCommand(_ => _onDelete(this));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
