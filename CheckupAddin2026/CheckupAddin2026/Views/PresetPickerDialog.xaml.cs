using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CheckupAddIn.Models;
using CheckupAddIn.Services;

namespace CheckupAddIn.Views
{
    public partial class PresetPickerDialog : Window
    {
        public PresetData SelectedPreset { get; private set; }

        public PresetPickerDialog(System.Collections.Generic.IReadOnlyList<PresetData> presets)
        {
            InitializeComponent();
            ThemeLoader.ApplyTo(this);
            LanguageLoader.ApplyTo(this);
            PickerList.ItemsSource = presets;
            if (UiStateStore.TryLoadInfoDialogSize("PresetPicker", out double w, out double h))
            {
                Width  = w;
                Height = h;
            }
        }

        private void PickerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => OkButton.IsEnabled = PickerList.SelectedItem != null;

        private void PickerList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PickerList.SelectedItem != null)
                DialogResult = true;
        }

        private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            if (ActualWidth > 0 && ActualHeight > 0)
                UiStateStore.SaveInfoDialogSize("PresetPicker", ActualWidth, ActualHeight);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult == true)
                SelectedPreset = PickerList.SelectedItem as PresetData;
            base.OnClosing(e);
        }
    }
}
