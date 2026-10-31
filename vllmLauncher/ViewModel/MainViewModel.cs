using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using vllmLauncher.Models;
using vllmLauncher.Services;

namespace vllmLauncher.ViewModel
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ConfigService _configService;
        private readonly PowerShellService _psService;

        private string _serverAddress = string.Empty;
        private bool _useScreen = false;
        private double _pasteWaitTime = 3.0;
        private ModelConfigViewModel? _selectedModel;
        private bool _editorActive = false;
        private string _editorName = string.Empty;
        private string _editorText = string.Empty;

        public string ServerAddress
        {
            get => _serverAddress;
            set { _serverAddress = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool UseScreen
        {
            get => _useScreen;
            set { _useScreen = value; OnPropertyChanged(); }
        }

        public double PasteWaitTime
        {
            get => _pasteWaitTime;
            set { _pasteWaitTime = value; OnPropertyChanged(); SaveConfig(); }
        }

        public bool EditorActive
        {
            get => _editorActive;
            set { _editorActive = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ModelConfigViewModel> Models { get; }

        public ModelConfigViewModel? SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (_selectedModel == value) return;
                if (_selectedModel != null) _selectedModel.IsSelected = false;
                _selectedModel = value;
                OnPropertyChanged();
                EditorActive = _selectedModel != null;
                if (_selectedModel != null) _selectedModel.IsSelected = true;
                LoadEditorFromSelected();
            }
        }

        public string EditorName
        {
            get => _editorName;
            set
            {
                _editorName = value;
                OnPropertyChanged();
                SyncEditorToSelected();
            }
        }

        public string EditorText
        {
            get => _editorText;
            set
            {
                _editorText = value;
                OnPropertyChanged();
                SyncEditorToSelected();
            }
        }

        public ICommand ConnectCommand { get; }
        public ICommand KillAllCommand { get; }
        public ICommand AddModelCommand { get; }
        public ICommand BackupCommand { get; }

        public MainViewModel(ConfigService configService, PowerShellService psService)
        {
            _configService = configService;
            _psService = psService;

            Models = new ObservableCollection<ModelConfigViewModel>();

            ConnectCommand = new RelayCommand(_ => Connect());
            KillAllCommand = new RelayCommand(_ => KillAllModels());
            AddModelCommand = new RelayCommand(_ => AddModel());
            BackupCommand = new RelayCommand(_ => BackupConfig());

            LoadConfig();
        }

        public void SaveConfig()
        {
            var settings = new AppSettings
            {
                ServerAddress = ServerAddress,
                UseScreen = UseScreen,
                PasteWaitTime = PasteWaitTime,
                Models = new ObservableCollection<ModelConfig>()
            };

            foreach (var vm in Models)
            {
                settings.Models.Add(vm.Config);
            }

            _configService.Save(settings);
        }

        public void BackupConfig()
        {
            try
            {
                SaveConfig();
                _configService.Backup();
                System.Windows.MessageBox.Show("备份成功", "备份", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"备份失败: {ex.Message}", "备份", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void LoadConfig()
        {
            var settings = _configService.Load();

            // Assign backing fields directly: setters call SaveConfig(), and at this
            // point Models is still empty — going through setters would wipe models
            // from config.json on every startup if the process is later killed.
            _serverAddress = settings.ServerAddress;
            OnPropertyChanged(nameof(ServerAddress));
            _useScreen = settings.UseScreen;
            OnPropertyChanged(nameof(UseScreen));
            _pasteWaitTime = settings.PasteWaitTime;
            OnPropertyChanged(nameof(PasteWaitTime));

            foreach (var modelConfig in settings.Models)
            {
                var vm = CreateModelViewModel(modelConfig);
                Models.Add(vm);
            }
            if (Models.Count > 0)
            {
                SelectedModel = Models[0];
            }
        }

        public void Connect()
        {
            if (string.IsNullOrWhiteSpace(ServerAddress))
            {
                return;
            }
            try
            {
                _psService.ConnectToRemote(ServerAddress);
            }
            catch (Exception ex)
            {
                // Failed to start PowerShell - user can retry
                System.Windows.MessageBox.Show($"打开 PowerShell 失败: {ex.Message}", "错误");
            }
        }

        private void KillAllModels()
        {
            if (string.IsNullOrWhiteSpace(ServerAddress)) return;
            try
            {
                if (UseScreen)
                {
                    _psService.KillAllVllmProcessesWithScreen(ServerAddress);
                }
                else
                {
                    _psService.KillAllVllmProcesses(ServerAddress);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"终止失败: {ex.Message}", "错误");
            }
        }

        public void AddModel()
        {
            if (Models.Count >= 100) return;

            var config = new ModelConfig($"Model {Models.Count + 1}", string.Empty);
            var vm = CreateModelViewModel(config);
            Models.Add(vm);
            SelectedModel = vm;
            SaveConfig();
        }

        public void DeleteModel(ModelConfigViewModel vm)
        {
            var result = System.Windows.MessageBox.Show(
                $"确定要删除模型 \"{vm.Name}\" 吗？",
                "确认删除",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            if (SelectedModel == vm)
            {
                SelectedModel = Models.FirstOrDefault(m => m != vm);
            }
            Models.Remove(vm);
            SaveConfig();
        }

        public void MoveModel(ModelConfigViewModel item, int newIndex)
        {
            var oldIndex = Models.IndexOf(item);
            if (oldIndex < 0 || oldIndex == newIndex) return;

            // Clamp new index to valid range
            if (newIndex < 0) newIndex = 0;
            if (newIndex > Models.Count) newIndex = Models.Count;

            Models.Remove(item);
            Models.Insert(newIndex, item);
            SaveConfig();
        }

        private void LoadEditorFromSelected()
        {
            if (SelectedModel != null)
            {
                _editorName = SelectedModel.Name;
                _editorText = SelectedModel.FullText;
            }
            else
            {
                _editorName = string.Empty;
                _editorText = string.Empty;
            }
            OnPropertyChanged(nameof(EditorName));
            OnPropertyChanged(nameof(EditorText));
        }

        private void SyncEditorToSelected()
        {
            if (SelectedModel == null) return;

            SelectedModel.Name = EditorName;
            SelectedModel.FullText = EditorText;
            SaveConfig();
        }

        /// <summary>Update the selected model's command markers (caller saves the config afterwards).</summary>
        public void UpdateSelectedCommands(System.Collections.Generic.List<CommandMarker> markers)
        {
            if (SelectedModel == null) return;
            SelectedModel.Config.Commands = markers;
        }

        /// <summary>Update the selected model's rolled-up groups and persist the config.</summary>
        public void SetSelectedCollapsedGroups(System.Collections.Generic.List<vllmLauncher.Models.CollapsedGroup> groups)
        {
            if (SelectedModel == null) return;
            SelectedModel.Config.CollapsedGroups = groups;
            SaveConfig();
        }

        /// <summary>
        /// Execute an arbitrary script (e.g. a command link clicked in the editor),
        /// same flow as the model list "启动" button.
        /// </summary>
        public void ExecuteScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return;

            if (string.IsNullOrWhiteSpace(ServerAddress))
            {
                System.Windows.MessageBox.Show("请先配置服务器地址。", "提示",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            // 持久化是一次性开关：本次按快照值发送（screen+脚本），发送后自动关闭，
            // 后续命令不再包screen（screen会话已由首条命令建立）。
            bool useScreenThisRun = UseScreen;
            _psService.StartWithDelay(ServerAddress, script, useScreenThisRun, PasteWaitTime);
            if (useScreenThisRun)
                UseScreen = false;
        }

        private ModelConfigViewModel CreateModelViewModel(ModelConfig config)
        {
            return new ModelConfigViewModel(config, vm => DeleteModel(vm));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
