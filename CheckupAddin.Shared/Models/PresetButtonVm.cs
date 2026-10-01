using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CheckupAddIn.Models
{
    /// <summary>Where the drag-and-drop insert marker is drawn on a Preset Button (TDD §10.5 D9).</summary>
    public enum PresetDropMarker
    {
        None,
        /// <summary>Insert before this preset (left edge in the Preset Bar, top edge in the More dropdown).</summary>
        Before,
        /// <summary>Insert after this preset (right edge in the Preset Bar, bottom edge in the More dropdown).</summary>
        After
    }

    /// <summary>
    /// One Preset Button in the Preset Bar / More dropdown (T47, TDD §10.5).
    /// <see cref="IsOverflow"/> is set by the layout panel (<c>PresetOverflowPanel</c>) — true when the
    /// button does not fit into the Preset Bar and is listed in the More dropdown instead.
    /// </summary>
    public sealed class PresetButtonVm : INotifyPropertyChanged
    {
        public string Id { get; init; } = "";

        private string _name = "";
        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value ?? ""; OnPropertyChanged(); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
        }

        private bool _isOverflow;
        public bool IsOverflow
        {
            get => _isOverflow;
            set { if (_isOverflow == value) return; _isOverflow = value; OnPropertyChanged(); }
        }

        /// <summary>False when this is the only preset left — the context menu's Delete is disabled (D7).</summary>
        private bool _canDelete;
        public bool CanDelete
        {
            get => _canDelete;
            set { if (_canDelete == value) return; _canDelete = value; OnPropertyChanged(); }
        }

        private bool _isDragging;
        public bool IsDragging
        {
            get => _isDragging;
            set { if (_isDragging == value) return; _isDragging = value; OnPropertyChanged(); }
        }

        private PresetDropMarker _dropMarker;
        public PresetDropMarker DropMarker
        {
            get => _dropMarker;
            set { if (_dropMarker == value) return; _dropMarker = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
