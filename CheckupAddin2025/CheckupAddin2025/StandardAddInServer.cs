using System;
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
    /// The GUID 04B466E8-07D5-4D83-A5F8-9E29BC01F5C9 must be identical in three places:
    ///   1. [Guid] attribute here
    ///   2. &lt;ClassId&gt; in Autodesk.CheckupAddIn.addin
    ///   3. &lt;ClientId&gt; in Autodesk.CheckupAddIn.addin
    /// Changing it breaks COM registration; Inventor will silently not load the add-in.
    /// </remarks>
    [ComVisible(true)]
    [Guid("04B466E8-07D5-4D83-A5F8-9E29BC01F5C9")]
    [ProgId("CheckupAddIn.StandardAddInServer")]
    public class StandardAddInServer : ApplicationAddInServer
    {
        private Inventor.Application _app;
        private CheckupWindow        _window;
        private CheckupViewModel     _viewModel;
        private UserSettings         _userSettings;
        private CatalogStore         _catalogStore;
        private CapabilityStore      _capabilityStore;

        // Button definition (reused for all ribbon placements)
        private ButtonDefinition _checkupButton;

        // ══════════════════════════════════════════════
        //  ACTIVATE — called by Inventor on load
        // ══════════════════════════════════════════════

        public void Activate(ApplicationAddInSite addInSiteObject, bool firstTime)
        {
            _app = addInSiteObject.Application;

            string addinDir = FindAddinDirectory();
            _userSettings = UserSettings.Load(addinDir);
            string localDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "Checkup 2025");
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
                    $"Checkup 2025 failed to initialize:\n{ex.Message}",
                    "Checkup 2025", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        // ══════════════════════════════════════════════
        //  BUTTON DEFINITION
        // ══════════════════════════════════════════════

        private void CreateButtonDefinition()
        {
            var controlDefs = _app.CommandManager.ControlDefinitions;

            // Create a single button definition
            _checkupButton = controlDefs.AddButtonDefinition(
                DisplayName: "Checkup 2025",
                InternalName: "CheckupAddIn_ShowWindow",
                Classification: CommandTypesEnum.kNonShapeEditCmdType,
                ClientId: "{04B466E8-07D5-4D83-A5F8-9E29BC01F5C9}",
                DescriptionText: "Open Checkup 2025 – Sheet Metal inspection and property viewer",
                ToolTipText: "Checkup 2025\nOpen the Checkup 2025 property viewer window.",
                StandardIcon: LoadIconAsPictureDisp(16),
                LargeIcon: LoadIconAsPictureDisp(32));

            _checkupButton.OnExecute += OnCheckupButtonClick;
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
                        DisplayName: "Checkup 2025",
                        InternalName: $"CheckupAddIn_Panel_{tabInternalName}",
                        ClientId: "{04B466E8-07D5-4D83-A5F8-9E29BC01F5C9}");
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
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Could not open Checkup 2025 window:\n{ex.Message}",
                    "Checkup 2025", System.Windows.MessageBoxButton.OK,
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

            _viewModel       = null;
            _window          = null;
            _checkupButton   = null;
            _catalogStore    = null;
            _capabilityStore = null;
            _app             = null;

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

        // ══════════════════════════════════════════════
        //  ADDIN DIRECTORY DISCOVERY
        // ══════════════════════════════════════════════

        /// <summary>
        /// Returns the directory that contains Autodesk.CheckupAddIn2025.addin.
        /// Because the .addin file lives in each user's own AppData, this gives every user
        /// their own Checkup2025_Settings.json without touching other users' copies.
        /// Falls back to the DLL directory if the addin cannot be located via the API.
        /// </summary>
        private string FindAddinDirectory()
        {
            const string myGuid = "{04B466E8-07D5-4D83-A5F8-9E29BC01F5C9}";
            try
            {
                foreach (ApplicationAddIn ai in _app.ApplicationAddIns)
                {
                    try
                    {
                        if (!string.Equals(ai.ClassIdString, myGuid, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string loc = ai.Location ?? "";
                        if (System.IO.Directory.Exists(loc))
                            return loc;
                        string dir = System.IO.Path.GetDirectoryName(loc);
                        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                            return dir;
                    }
                    catch { }
                }
            }
            catch { }
            // Fallback: use DLL directory (matches old behaviour)
            return System.IO.Path.GetDirectoryName(typeof(StandardAddInServer).Assembly.Location) ?? "";
        }

        // ══════════════════════════════════════════════
        //  ICON HELPER — Bitmap → IPictureDisp via OLE
        // ══════════════════════════════════════════════

        [StructLayout(LayoutKind.Sequential)]
        private struct PICTDESC_BITMAP
        {
            public int    cbSizeofstruct;
            public int    picType;    // 1 = PICTYPE_BITMAP
            public IntPtr hbitmap;
            public IntPtr hpal;
            public short  bReserved;
        }

        [DllImport("oleaut32.dll")]
        private static extern int OleCreatePictureIndirect(
            ref PICTDESC_BITMAP pdesc, ref Guid riid, bool fOwn,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObj);

        private static readonly Guid IID_IPictureDisp =
            new Guid("7BF80981-BF32-101A-8BBB-00AA00300CAB");

        private static object LoadIconAsPictureDisp(int size)
        {
            using (var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("CheckupAddIn.checkup_icon.png"))
            {
                if (stream == null) return null;
                using (var src = new System.Drawing.Bitmap(stream))
                {
                    var scaled = new System.Drawing.Bitmap(src, new System.Drawing.Size(size, size));
                    try
                    {
                        var desc = new PICTDESC_BITMAP
                        {
                            cbSizeofstruct = Marshal.SizeOf(typeof(PICTDESC_BITMAP)),
                            picType        = 1,
                            hbitmap        = scaled.GetHbitmap()
                        };
                        var iid = IID_IPictureDisp;
                        OleCreatePictureIndirect(ref desc, ref iid, fOwn: true, out object pic);
                        return pic;
                    }
                    finally { scaled.Dispose(); }
                }
            }
        }
    }
}