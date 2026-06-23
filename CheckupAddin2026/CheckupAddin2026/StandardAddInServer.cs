using System.Reflection;
using System.Runtime.InteropServices;
using Inventor;
using CheckupAddIn.Services;
using CheckupAddIn.ViewModels;
using CheckupAddIn.Views;

namespace CheckupAddIn
{
    /// <summary>
    /// COM entry point for the Inventor add-in. Inventor instantiates this class on startup via COM.
    /// </summary>
    /// <remarks>
    /// The GUID D72E8C3A-5B1F-4E3A-9C6D-A1B2C3D4E5F6 must be identical in three places:
    ///   1. [Guid] attribute here
    ///   2. &lt;ClassId&gt; in Autodesk.CheckupAddIn.addin
    ///   3. &lt;ClientId&gt; in Autodesk.CheckupAddIn.addin
    /// Changing it breaks COM registration; Inventor will silently not load the add-in.
    /// </remarks>
    [ComVisible(true)]
    [Guid("D72E8C3A-5B1F-4E3A-9C6D-A1B2C3D4E5F6")]
    [ProgId("CheckupAddIn.StandardAddInServer")]
    public class StandardAddInServer : ApplicationAddInServer
    {
        private Inventor.Application _app;
        private CheckupWindow _window;
        private CheckupViewModel _viewModel;
        private Services.UserSettings     _userSettings;
        private Services.CatalogStore     _catalogStore;
        private Services.CapabilityStore  _capabilityStore;

        // Button definition (reused for all ribbon placements)
        private ButtonDefinition _checkupButton;

        // ══════════════════════════════════════════════
        //  ACTIVATE — called by Inventor on load
        // ══════════════════════════════════════════════

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            _app = addInSiteObject.Application;

            string addinDir = System.IO.Path.GetDirectoryName(
                typeof(StandardAddInServer).Assembly.Location) ?? AppContext.BaseDirectory;
            _userSettings    = UserSettings.Load(addinDir);
            string localDir  = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Checkup 2026");
            _catalogStore    = CatalogStore.Load(addinDir, localDir);
            _capabilityStore = CapabilityStore.Load(addinDir, localDir);
            LanguageLoader.Detect(_app);

            try
            {
                CreateButtonDefinition();

                // firstTime is false when Inventor reloads the add-in without a restart.
                // Ribbon items must only be added once to avoid duplicates.
                if (firstTime)
                {
                    AddButtonToRibbons();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Checkup – failed to initialize:\n{ex.Message}",
                    "Checkup", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        // ══════════════════════════════════════════════
        //  BUTTON DEFINITION
        // ══════════════════════════════════════════════

        private void CreateButtonDefinition()
        {
            var controlDefs = _app.CommandManager.ControlDefinitions;

            // Load icons; null on any failure → Inventor falls back to DisplayName text.
            object standardIcon = null;
            object largeIcon = null;
            try
            {
                standardIcon = LoadIconAsPictureDisp(16);
                largeIcon    = LoadIconAsPictureDisp(32);
            }
            catch { }

            _checkupButton = controlDefs.AddButtonDefinition(
                DisplayName: "Checkup",
                InternalName: "CheckupAddIn_ShowWindow",
                Classification: CommandTypesEnum.kNonShapeEditCmdType,
                ClientId: "{D72E8C3A-5B1F-4E3A-9C6D-A1B2C3D4E5F6}",
                DescriptionText: "Open Checkup – Sheet Metal inspection and property viewer",
                ToolTipText: "Checkup\nOpen the Checkup property viewer window.",
                StandardIcon: standardIcon,
                LargeIcon: largeIcon);

            _checkupButton.OnExecute += OnCheckupButtonClick;
        }

        // ══════════════════════════════════════════════
        //  ICON HELPERS — Bitmap → IPictureDisp (OLE)
        // ══════════════════════════════════════════════

        // PICTDESC structure (bitmap variant) for OleCreatePictureIndirect.
        [StructLayout(LayoutKind.Sequential)]
        private struct PICTDESC_BITMAP
        {
            public int  cbSizeofstruct;
            public int  picType;    // 1 = PICTYPE_BITMAP
            public IntPtr hbitmap;
            public IntPtr hpal;
            public short  bReserved;
        }

        [DllImport("oleaut32.dll")]
        private static extern int OleCreatePictureIndirect(
            ref PICTDESC_BITMAP pdesc,
            ref Guid riid,
            bool fOwn,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObj);

        private static readonly Guid IID_IPictureDisp =
            new Guid("7BF80981-BF32-101A-8BBB-00AA00300CAB");

        private static object LoadIconAsPictureDisp(int size)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("CheckupAddIn.checkup_icon.png");
            if (stream == null) return null;

            using var src = new System.Drawing.Bitmap(stream);
            // GetHbitmap() creates an independent GDI copy; OLE takes ownership via fOwn=true.
            var scaled = new System.Drawing.Bitmap(src, new System.Drawing.Size(size, size));
            try
            {
                var desc = new PICTDESC_BITMAP
                {
                    cbSizeofstruct = Marshal.SizeOf<PICTDESC_BITMAP>(),
                    picType        = 1,
                    hbitmap        = scaled.GetHbitmap()
                };
                var iid = IID_IPictureDisp;
                OleCreatePictureIndirect(ref desc, ref iid, fOwn: true, out object pic);
                return pic;
            }
            finally
            {
                scaled.Dispose();
            }
        }

        // ══════════════════════════════════════════════
        //  RIBBON PLACEMENT
        // ══════════════════════════════════════════════

        private void AddButtonToRibbons()
        {
            var ribbons = _app.UserInterfaceManager.Ribbons;

            // ── Part environment ──
            try
            {
                var partRibbon = ribbons["Part"];

                // Sheet Metal tab (last position)
                TryAddButtonToTab(partRibbon, "id_TabSheetMetal");

                // 3D Model tab (last position)
                TryAddButtonToTab(partRibbon, "id_TabModel");
            }
            catch { }

            // ── Assembly environment ──
            try
            {
                var asmRibbon = ribbons["Assembly"];

                // Assemble tab (last position)
                TryAddButtonToTab(asmRibbon, "id_TabAssemble");
            }
            catch { }

            // ── Drawing environment ──
            try
            {
                var drawingRibbon = ribbons["Drawing"];

                // Place Views tab and Annotate tab
                TryAddButtonToTab(drawingRibbon, "id_TabPlaceViews");
                TryAddButtonToTab(drawingRibbon, "id_TabAnnotate");
            }
            catch { }
        }

        private void TryAddButtonToTab(Ribbon ribbon, string tabInternalName)
        {
            try
            {
                RibbonTab tab = null;

                // Find the tab by internal name
                foreach (RibbonTab t in ribbon.RibbonTabs)
                {
                    if (t.InternalName == tabInternalName)
                    {
                        tab = t;
                        break;
                    }
                }

                if (tab == null) return;

                // Create or reuse a panel for Checkup
                RibbonPanel panel = null;
                try
                {
                    panel = tab.RibbonPanels.Add(
                        DisplayName: "Checkup",
                        InternalName: $"CheckupAddIn_Panel_{tabInternalName}",
                        ClientId: "{D72E8C3A-5B1F-4E3A-9C6D-A1B2C3D4E5F6}");
                }
                catch
                {
                    // Panel might already exist
                    foreach (RibbonPanel p in tab.RibbonPanels)
                    {
                        if (p.InternalName == $"CheckupAddIn_Panel_{tabInternalName}")
                        {
                            panel = p;
                            break;
                        }
                    }
                }

                if (panel == null) return;

                panel.CommandControls.AddButton(
                    ButtonDefinition: _checkupButton,
                    UseLargeIcon: true);
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  BUTTON CLICK — SHOW/TOGGLE WINDOW
        // ══════════════════════════════════════════════

        private void OnCheckupButtonClick(NameValueMap context)
        {
            try
            {
                // If window exists and is open, bring to front
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    _viewModel?.DoRefresh();
                    return;
                }

                // Create new ViewModel and Window
                var _swOpen = System.Diagnostics.Stopwatch.StartNew();
                PerfLogger.LogSession("Checkup 2026 activated");
                _viewModel = new CheckupViewModel(_app, _userSettings, _catalogStore, _capabilityStore);
                _window = new CheckupWindow();
                _window.SetViewModel(_viewModel);

                // Parent the WPF window to Inventor so it stays on top and minimises with it.
                try
                {
                    _window.SetInventorOwner(new IntPtr(_app.MainFrameHWND));
                }
                catch { }

                // Handle window closed — clean up references
                _window.Closed += (s, e) =>
                {
                    _viewModel?.UnsubscribeFromInventorEvents();
                    _viewModel = null;
                    _window = null;
                };

                _window.Show();
                PerfLogger.LogOpen(_swOpen.ElapsedMilliseconds, PerfLogger.OptimizationsOn());
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Could not open Checkup window:\n{ex.Message}",
                    "Checkup", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        // ══════════════════════════════════════════════
        //  DEACTIVATE — called by Inventor on unload
        // ══════════════════════════════════════════════

        public void Deactivate()
        {
            try
            {
                _viewModel?.UnsubscribeFromInventorEvents();
            }
            catch { }

            try
            {
                if (_window != null && _window.IsLoaded)
                    _window.Close();
            }
            catch { }

            _viewModel = null;
            _window = null;
            if (_checkupButton != null)
            {
                try { _checkupButton.OnExecute -= OnCheckupButtonClick; } catch { }
                _checkupButton = null;
            }
            _app             = null;
            _userSettings    = null;
            _catalogStore    = null;
            _capabilityStore = null;

            // Force release of COM references — without this, Inventor can hang on shutdown
            // because the CLR garbage collector does not run before Inventor unloads the AppDomain.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // ══════════════════════════════════════════════
        //  REQUIRED INTERFACE MEMBERS
        // ══════════════════════════════════════════════

        public void ExecuteCommand(int commandID) { }

        public object Automation => null;
    }
}