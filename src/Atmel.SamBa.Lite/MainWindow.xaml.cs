using Anp.Atmel.SamBa.Lite.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;


namespace Anp.Atmel.SamBa.Lite
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        // The hex editor has no bindable data source (its Stream property is plain CLR), so the
        // view mirrors the view model's HexViewData into it here. The view model stays the
        // single source of truth; the editor is a read-only visualizer.
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is MainViewModel oldVm)
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;

            if (e.NewValue is MainViewModel newVm)
            {
                newVm.PropertyChanged += OnViewModelPropertyChanged;
                LoadHexView(newVm);
                ApplySavedSplitterHeights(newVm);
            }
        }

        // Both rows are Star, and GridSplitter (ResizeBehavior=PreviousAndNext) resizes a pair
        // of Star rows by re-weighting both together -- "1 star == 1 pixel" at drag time, per
        // its own SetLengths logic -- not by pinning one to a fixed pixel size. Restoring must
        // match that: both values captured, both reapplied as Star, or the ratio is wrong the
        // moment the window differs in size from when it was saved. A saved value of 0 in
        // either (fresh install, or a settings file predating this option) leaves the
        // XAML-authored "*" defaults in place for both.
        private void ApplySavedSplitterHeights(MainViewModel viewModel)
        {
            if (viewModel == null || viewModel.MemoryViewHeight <= 0 || viewModel.LogViewHeight <= 0)
                return;

            MemoryViewRow.Height = new GridLength(viewModel.MemoryViewHeight, GridUnitType.Star);
            LogViewRow.Height = new GridLength(viewModel.LogViewHeight, GridUnitType.Star);
        }

        // Mirror both rows' current heights into the view model so they persist together.
        private void MemoryLogSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.MemoryViewHeight = MemoryViewRow.Height.Value;
                vm.LogViewHeight = LogViewRow.Height.Value;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.HexViewData))
                LoadHexView(sender as MainViewModel);
        }

        private void LoadHexView(MainViewModel viewModel)
        {
            var data = viewModel?.HexViewData;

            // CloseProvider releases the previous stream; the editor does not dispose a
            // replaced stream on its own.
            HexView.CloseProvider();

            if (data != null && data.Length > 0)
                HexView.Stream = new MemoryStream(data);
        }

        // The VID/PID boxes live inside a checkable menu item (selecting the Custom filter).
        // On the first click, focus the box for editing without letting the click bubble up and
        // toggle the parent item; once focused, clicks pass through for caret placement.
        private void HexBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
            {
                tb.Focus();
                e.Handled = true;
            }
        }
    }
}
