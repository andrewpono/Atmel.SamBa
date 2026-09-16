using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;


namespace Anp.Atmel.SamBa.FirmwareUpdater.Views
{
    public partial class TextToolWindow : Window, INotifyPropertyChanged
    {
        private string _text;

        public TextToolWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value)
                    return;
                _text = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
