using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentOffice.Ribbon
{
/// <summary>Ribbon (XML) for Agent Office: a "Chat Panel" toggle button in its own Agent Office tab.</summary>
    [ComVisible(true)]
    public class AgentRibbon : Microsoft.Office.Core.IRibbonExtensibility
    {
        private readonly ThisAddIn _addIn;
        private Microsoft.Office.Core.IRibbonUI _ribbonUi;

        public AgentRibbon(ThisAddIn addIn)
        {
            _addIn = addIn;
        }

        // --- IRibbonExtensibility -------------------------------------------

        public string GetCustomUI(string ribbonID)
        {
            var asm = typeof(AgentRibbon).Assembly;
            using (var stream = asm.GetManifestResourceStream("AgentOffice.Ribbon.AgentRibbon.xml"))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        // --- Callbacks (late-bound by Office via IDispatch) -----------------

        public void OnLoad(Microsoft.Office.Core.IRibbonUI ribbonUi)
        {
            _ribbonUi = ribbonUi;
        }

        public bool GetPanePressed(Microsoft.Office.Core.IRibbonControl control)
        {
            return _addIn.ChatPane != null && _addIn.ChatPane.Visible;
        }

        public void OnTogglePane(Microsoft.Office.Core.IRibbonControl control, bool pressed)
        {
            // SetChatPaneVisible flips Visible; the VisibleChanged handler in
            // ThisAddIn persists the choice and re-invalidates this button.
            _addIn.SetChatPaneVisible(pressed);
        }

/// <summary>Branded button image.</summary>
        public stdole.IPictureDisp GetImage(Microsoft.Office.Core.IRibbonControl control)
        {
            var resource = IsOfficeDarkTheme()
                ? "AgentOffice.Resources.AgentOffice-32-light.png"
                : "AgentOffice.Resources.AgentOffice-32.png";
            var asm = typeof(AgentRibbon).Assembly;
            using (var stream = asm.GetManifestResourceStream(resource))
            {
                if (stream == null) return null;
                using (var raw = new Bitmap(stream))
                // Office renders palette-indexed bitmaps as scattered pixels — normalize to 32bpp ARGB.
                using (var bmp = new Bitmap(raw.Width, raw.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.DrawImageUnscaled(raw, 0, 0);
                    }
                    return PictureDispConverter.ToIPictureDisp(bmp);
                }
            }
        }

/// <summary>Office theme from HKCU\Software\Microsoft\Office\16.0\Common "UI Theme": 0=Colorful, 3=White, 4=Dark Gray, 5=Black.</summary>
        private static bool IsOfficeDarkTheme()
        {
            try
            {
                var raw = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\Common",
                    "UI Theme", 0);
                var theme = raw is int ? (int)raw : 0;
                return theme == 4 || theme == 5;
            }
            catch
            {
                return false;
            }
        }

        // --- Helpers ---------------------------------------------------------

/// <summary>Force Office to re-query GetPanePressed (state changed).</summary>
        internal void InvalidateToggle()
        {
            if (_ribbonUi != null)
            {
                try { _ribbonUi.InvalidateControl("ChatPaneToggle"); }
                catch { /* ribbon already gone (shutdown) — ignore */ }
            }
        }

/// <summary>Classic Bitmap → IPictureDisp bridge (the ribbon framework wants an OLE picture, not a GDI+ object).</summary>
        private sealed class PictureDispConverter : System.Windows.Forms.AxHost
        {
            private PictureDispConverter() : base(string.Empty) { }

            public static stdole.IPictureDisp ToIPictureDisp(Image image)
            {
                return (stdole.IPictureDisp)GetIPictureDispFromPicture(image);
            }
        }
    }
}
