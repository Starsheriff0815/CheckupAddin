using System.Windows;
using System.Windows.Controls;
using CheckupAddIn.Services;

namespace CheckupAddIn.Views
{
    public partial class InfoDialog : Window
    {
        private readonly string _contextKey;

        public InfoDialog(UIElement content, string contextKey, string titleKey, double defaultWidth, double defaultHeight,
                          bool showCancel = false)
        {
            InitializeComponent();
            ThemeLoader.ApplyTo(this);
            LanguageLoader.ApplyTo(this);
            Title = LanguageLoader.Get(titleKey);
            InfoContent.Content = content;
            _contextKey = contextKey;

            if (showCancel)
                CancelBtn.Visibility = Visibility.Visible;

            if (UiStateStore.TryLoadInfoDialogSize(contextKey, out double w, out double h))
            {
                Width  = w;
                Height = h;
            }
            else
            {
                Width  = defaultWidth;
                Height = defaultHeight;
            }
        }

        public InfoDialog(string text, string contextKey, string titleKey, double defaultWidth, double defaultHeight,
                          bool showCancel = false)
            : this(MakeTextBlock(text), contextKey, titleKey, defaultWidth, defaultHeight, showCancel)
        {
        }

        static UIElement MakeTextBlock(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupPrimaryText");
            return tb;
        }

        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (CancelBtn.Visibility == Visibility.Visible)
                    DialogResult = false;
                else
                    DialogResult = true;
                Close();
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActualWidth > 0 && ActualHeight > 0)
                UiStateStore.SaveInfoDialogSize(_contextKey, ActualWidth, ActualHeight);
        }
    }
}
