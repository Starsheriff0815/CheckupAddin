using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CheckupAddIn.Models;

namespace CheckupAddIn.Views
{
    /// <summary>
    /// Items panel of the Preset Bar (T47, TDD §10.5 D8). Lays the Preset Buttons out left → right in list
    /// order; from the first button that does not fit on, all remaining buttons are hidden (arranged into a
    /// zero-size slot, clipped) and flagged <see cref="PresetButtonVm.IsOverflow"/> so the More dropdown lists them.
    /// </summary>
    /// <remarks>
    /// Layout-only. MeasureOverride is pure (no state written) so a parent may measure it several times per pass;
    /// the overflow flags are committed in ArrangeOverride, after the final size is known. The flag setters are
    /// equality-guarded, so a re-layout triggered by a flag change settles on the next pass.
    /// </remarks>
    public sealed class PresetOverflowPanel : Panel
    {
        private readonly List<bool> _fits = new();

        public PresetOverflowPanel()
        {
            ClipToBounds = true;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _fits.Clear();
            double used = 0, height = 0;
            bool overflow = false;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                double w = child.DesiredSize.Width;
                bool fits = !overflow && used + w <= availableSize.Width + 0.5;
                if (fits)
                {
                    used  += w;
                    height = Math.Max(height, child.DesiredSize.Height);
                }
                else overflow = true;
                _fits.Add(fits);
            }
            return new Size(used, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                bool fits = i < _fits.Count && _fits[i];
                if (fits)
                {
                    child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                    x += child.DesiredSize.Width;
                }
                else
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                }
                if (child is FrameworkElement fe && fe.DataContext is PresetButtonVm vm)
                    vm.IsOverflow = !fits;
            }
            return finalSize;
        }
    }

    /// <summary>
    /// The Preset Bar container (T47, TDD §10.5 D3/D8): exactly three children —
    /// [0] the Preset Buttons host (an ItemsControl using <see cref="PresetOverflowPanel"/>),
    /// [1] the More Button, [2] the Add Preset Button ("+").
    /// Order left → right: presets → More (only when something overflows) → "+"; "+" is always last.
    /// </summary>
    public sealed class PresetBarPanel : Panel
    {
        private bool _overflow;

        public PresetBarPanel()
        {
            ClipToBounds = true;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (InternalChildren.Count < 3) return new Size(0, 0);
            var items = InternalChildren[0];
            var more  = InternalChildren[1];
            var add   = InternalChildren[2];

            var inf = new Size(double.PositiveInfinity, availableSize.Height);
            add.Measure(inf);
            more.Measure(inf);
            items.Measure(inf);   // natural width of all Preset Buttons

            double addW    = add.DesiredSize.Width;
            double natural = items.DesiredSize.Width;
            double height  = Math.Max(add.DesiredSize.Height, items.DesiredSize.Height);

            if (double.IsInfinity(availableSize.Width) || natural + addW <= availableSize.Width + 0.5)
            {
                _overflow = false;
                return new Size(natural + addW, height);
            }

            _overflow = true;
            double moreW  = more.DesiredSize.Width;
            double itemsW = Math.Max(0, availableSize.Width - addW - moreW);
            items.Measure(new Size(itemsW, availableSize.Height));
            height = Math.Max(height, more.DesiredSize.Height);
            return new Size(items.DesiredSize.Width + moreW + addW, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count < 3) return finalSize;
            var items = InternalChildren[0];
            var more  = InternalChildren[1];
            var add   = InternalChildren[2];

            double x = 0;
            items.Arrange(new Rect(x, 0, items.DesiredSize.Width, finalSize.Height));
            x += items.DesiredSize.Width;

            if (_overflow)
            {
                more.Arrange(new Rect(x, 0, more.DesiredSize.Width, finalSize.Height));
                x += more.DesiredSize.Width;
            }
            else
            {
                more.Arrange(new Rect(0, 0, 0, 0));
            }

            add.Arrange(new Rect(x, 0, add.DesiredSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}
