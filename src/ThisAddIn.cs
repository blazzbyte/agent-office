using System;

namespace AgentOffice
{
/// <summary>VSTO entry point for Agent Office.</summary>
    public partial class ThisAddIn
    {
        private Bridge.BridgeHost _host;
        private Agent.AgentHost _agent;
        private Microsoft.Office.Tools.CustomTaskPane _taskPane;
        private Taskpane.AgentTaskpaneControl _taskpaneControl;
        private Ribbon.AgentRibbon _ribbon;

/// <summary>The chat taskpane (null if creation failed).</summary>
        internal Microsoft.Office.Tools.CustomTaskPane ChatPane
        {
            get { return _taskPane; }
        }

/// <summary>Show/hide the chat pane from the ribbon toggle.</summary>
        internal void SetChatPaneVisible(bool visible)
        {
            if (_taskPane != null) _taskPane.Visible = visible;
        }

/// <summary>Office requests the ribbon extensibility object here.</summary>
        protected override Microsoft.Office.Core.IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            _ribbon = new Ribbon.AgentRibbon(this);
            return _ribbon;
        }

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            try
            {
                // Ensure modern TLS protocols (TLS 1.2 / TLS 1.3) are enabled for all outbound HTTPS calls.
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12 |
                    System.Net.SecurityProtocolType.Tls11 |
                    System.Net.SecurityProtocolType.Tls |
                    (System.Net.SecurityProtocolType)12288;
                System.Net.ServicePointManager.DefaultConnectionLimit = 64;
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Failed to initialize ServicePointManager security protocols", ex);
            }

            try
            {
                _agent = new Agent.AgentHost();
                _agent.Start();
            }
            catch (Exception ex)
            {
                // Never crash Excel because of the agent — log and stay disabled.
                Bridge.Log.Error("Agent Office in-proc agent failed to start", ex);
            }

            try
            {
                _host = new Bridge.BridgeHost();
                _host.Start();
            }
            catch (Exception ex)
            {
                // Never crash Excel because of the bridge — log and stay disabled.
                Bridge.Log.Error("Agent Office failed to start", ex);
            }

            try
            {
                _taskpaneControl = new Taskpane.AgentTaskpaneControl();
                _taskPane = CustomTaskPanes.Add(_taskpaneControl, "Agent Office");
                _taskPane.DockPosition =
                    Microsoft.Office.Core.MsoCTPDockPosition.msoCTPDockPositionRight;
                _taskPane.Width = 420;
                // Restore the user's last choice (default: visible on first run);
                // the ribbon toggle mirrors/persists it from then on.
                _taskPane.VisibleChanged += ChatPane_VisibleChanged;
                _taskPane.Visible = Taskpane.HostSettings.PaneVisible;
            }
            catch (Exception ex)
            {
                // The add-in still works headless (bridge) if the taskpane fails.
                Bridge.Log.Error("Agent Office taskpane failed to be created", ex);
            }
        }

/// <summary>Any visibility change (ribbon toggle OR the pane's own X).</summary>
        private void ChatPane_VisibleChanged(object sender, EventArgs e)
        {
            try
            {
                Taskpane.HostSettings.SavePaneVisible(_taskPane.Visible);
                if (_ribbon != null) _ribbon.InvalidateToggle();
            }
            catch (Exception ex)
            {
                Bridge.Log.Error("Failed to persist pane visibility", ex);
            }
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            try
            {
                if (_taskPane != null)
                {
                    CustomTaskPanes.Remove(_taskPane);
                    _taskPane = null;
                }
                _taskpaneControl?.Dispose();
                _taskpaneControl = null;
            }
            catch
            {
                // Swallow — Excel is shutting down anyway.
            }

            try
            {
                _host?.Dispose();
                _host = null;
                _agent?.Dispose();
                _agent = null;
            }
            catch
            {
                // Swallow — Excel is shutting down anyway.
            }
        }

        #region VSTO generated code

/// <summary>Required method for Designer support - do not modify the contents of this method with the code editor.</summary>
        private void InternalStartup()
        {
            this.Startup += new System.EventHandler(ThisAddIn_Startup);
            this.Shutdown += new System.EventHandler(ThisAddIn_Shutdown);
        }

        #endregion
    }
}
