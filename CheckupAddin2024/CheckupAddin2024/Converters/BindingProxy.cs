using System.Windows;

namespace CheckupAddIn.Converters
{
    /// <summary>
    /// Freezable-based proxy that carries a DataContext reference into a Popup's isolated visual tree.
    /// Declare as a Window resource and bind Data to the DataContext (ViewModel), then use
    /// {Binding Data.SomeProperty, Source={StaticResource VmProxy}} inside Popup content.
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore() => new BindingProxy();

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }
    }
}
