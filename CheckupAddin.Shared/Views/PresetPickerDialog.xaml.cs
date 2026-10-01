using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CheckupAddIn.Models;
using CheckupAddIn.Services;

namespace CheckupAddIn.Views
{
    /// <summary>
    /// Import picker (TDD §5.1 Import, §10.5 D12/D15). Entries are ticked with a checkbox (several allowed).
    /// "Replace this preset" = exactly one ticked (overwrites the right-clicked preset);
    /// "Add as new" = one or more ticked (each becomes a new Preset Button). Double-click = replace with that entry.
    /// </summary>
    public partial class PresetPickerDialog : Window
    {
        public enum PickerMode { Replace, AddNew }

        /// <summary>Ticked entries in file order (set when the dialog closes with OK).</summary>
        public IReadOnlyList<PresetData> SelectedPresets { get; private set; } = new List<PresetData>();
        public PickerMode Mode { get; private set; } = PickerMode.Replace;

        /// <summary>First ticked entry — kept for callers that import a single preset.</summary>
        public PresetData SelectedPreset => SelectedPresets.FirstOrDefault();

        private readonly IReadOnlyList<PresetData> _library;
        private readonly IReadOnlyList<PresetData> _existing;
        private readonly int _freeSlots;

        public PresetPickerDialog(IReadOnlyList<PresetData> presets, IReadOnlyList<PresetData> existing = null)
        {
            InitializeComponent();
            ThemeLoader.ApplyTo(this);
            LanguageLoader.ApplyTo(this);
            _library   = presets ?? new List<PresetData>();
            _existing  = existing ?? new List<PresetData>();
            _freeSlots = PresetsManager.MaxPresets - _existing.Count;
            PickerList.ItemsSource = _library;
            if (UiStateStore.TryLoadInfoDialogSize("PresetPicker", out double w, out double h))
            {
                Width  = w;
                Height = h;
            }
            UpdateButtons();
        }

        private List<PresetData> Ticked() =>
            _library.Where(p => PickerList.SelectedItems.Contains(p)).ToList();   // file order, not click order

        private void PickerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        // D15 Q1: entries matching no existing preset always need a new slot — too many → Add disabled + hint.
        private void UpdateButtons()
        {
            var ticked = Ticked();
            int needed = PresetsManager.CountGuaranteedNew(_existing, ticked);
            bool overLimit = needed > _freeSlots;

            ReplaceButton.IsEnabled = ticked.Count == 1;
            AddNewButton.IsEnabled  = ticked.Count >= 1 && !overLimit;

            LimitHint.Text = string.Format(LanguageLoader.Get(overLimit ? "Dlg_PresetPicker_LimitHint" : "Dlg_PresetPicker_Free"),
                                           System.Math.Max(0, _freeSlots));
            LimitHint.SetResourceReference(TextBlock.ForegroundProperty, overLimit ? "CheckupErrorText" : "CheckupSecondaryText");
        }

        private void PickerList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not PresetData clicked) return;
            SelectedPresets = new List<PresetData> { clicked };
            Mode = PickerMode.Replace;
            DialogResult = true;
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            SelectedPresets = Ticked();
            Mode = PickerMode.Replace;
            DialogResult = true;
        }

        private void AddNew_Click(object sender, RoutedEventArgs e)
        {
            SelectedPresets = Ticked();
            Mode = PickerMode.AddNew;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
        {
            if (ActualWidth > 0 && ActualHeight > 0)
                UiStateStore.SaveInfoDialogSize("PresetPicker", ActualWidth, ActualHeight);
        }
    }
}
