using System.Windows;
using System.Windows.Input;
using CheckupAddIn.Services;

namespace CheckupAddIn.Views
{
    public partial class InputDialog : Window
    {
        public string InputText => NameBox.Text.Trim();

        public InputDialog(string initialValue = "")
        {
            InitializeComponent();
            ThemeLoader.ApplyTo(this);
            LanguageLoader.ApplyTo(this);
            NameBox.Text = initialValue;
            Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        }

        private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) DialogResult = true;
        }
    }
}
