using System.Windows;

namespace CheckupAddIn.Helpers
{
    /// <summary>
    /// Freezable that carries an arbitrary Data reference through resource inheritance.
    /// Used to pass the Window DataContext (VM) into Popup content whose visual tree
    /// is isolated from the main window tree.
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore() => new BindingProxy();

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }
    }
}
