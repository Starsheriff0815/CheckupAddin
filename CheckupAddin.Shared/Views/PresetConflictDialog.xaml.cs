using System.Windows;
using CheckupAddIn.Services;

namespace CheckupAddIn.Views
{
    /// <summary>
    /// Multi-import conflict question (TDD §10.5 D15): an imported preset's ID or name already exists.
    /// Overwrite the existing preset, add the import as a new preset, or cancel the whole import.
    /// </summary>
    public partial class PresetConflictDialog : Window
    {
        public enum ConflictChoice { Cancel, Overwrite, AddNew }

        public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;

        /// <summary>True when "Apply to all remaining conflicts" was ticked.</summary>
        public bool ApplyToAll => ApplyAllCheck.IsChecked == true;

        public PresetConflictDialog(string presetName, bool moreConflictsFollow)
        {
            InitializeComponent();
            ThemeLoader.ApplyTo(this);
            LanguageLoader.ApplyTo(this);
            BodyText.Text = string.Format(LanguageLoader.Get("Dlg_PresetConflict_Body"), presetName);
            ApplyAllCheck.Visibility = moreConflictsFollow ? Visibility.Visible : Visibility.Collapsed;
            if (UiStateStore.TryLoadInfoDialogSize("PresetConflict", out double w, out double h))
            {
                Width  = w;
                Height = h;
            }
        }

        private void Overwrite_Click(object sender, RoutedEventArgs e) { Choice = ConflictChoice.Overwrite; DialogResult = true; }
        private void AddNew_Click(object sender, RoutedEventArgs e)    { Choice = ConflictChoice.AddNew;    DialogResult = true; }
        private void Cancel_Click(object sender, RoutedEventArgs e)    { Choice = ConflictChoice.Cancel;    DialogResult = false; }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActualWidth > 0 && ActualHeight > 0)
                UiStateStore.SaveInfoDialogSize("PresetConflict", ActualWidth, ActualHeight);
        }
    }
}
