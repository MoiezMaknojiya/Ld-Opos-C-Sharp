using ApiLibrary;
using Interop.CoreScanner;
using System.ServiceProcess;

namespace LdOposManager
{
    public partial class DashboardForm : Form
    {
        private readonly string _userName;
        private readonly string _accessToken;
        private readonly DatabaseServices _dbHelper;
        private readonly ApiServices _apiService;
        private const string ServiceName = "LdOposService";
        private const string CoreScannerServiceName = "CoreScanner";

        // CoreScanner opcodes (Zebra Scanner SDK for Windows)
        private const int OpcodeScanEnable = 2014;
        private const int OpcodeSetParameterDefaults = 2015;
        private const int OpcodeAttrStore = 5005;
        private const int OpcodeSwitchHostMode = 6200;

        // USB host mode variant codes
        private const string HostModeIbmHandheld = "XUA-45001-1";   // intermediate hop (allowed from HID keyboard mode)
        private const string HostModeHidKeyboard = "XUA-45001-3";   // USB HID Keyboard
        private const string HostModeOpos = "XUA-45001-8";          // OPOS (IBM Hand-Held with Full Disable)

        public DashboardForm(string userName, string accessToken)
        {
            InitializeComponent();
            _userName = userName;
            _accessToken = accessToken;
            _dbHelper = new DatabaseServices();
            _apiService = new ApiServices();
        }

        // onClick Logout Function 
        private async void btnLogout_Click(object sender, EventArgs e)
        {
            try
            {
                btnLogout.Enabled = false;

                // Stop the Windows Service
                StopWindowsService();

                // LOCAL LOGOUT - delete EVERYTHING, queued or not. Never depends on the network.
                // (Policy: do not press Logout while the internet is down - queued scans are discarded.)
                _dbHelper.DeleteAllUsers();
                _dbHelper.DeleteAllTransactions();

                // SERVER LOGOUT LAST - best effort: if the network is down this
                // returns false after its timeout, but local logout is already complete
                await _apiService.LogoutAsync(_accessToken);

                // Restart the app fresh - avoids stacking hidden forms on every login/logout cycle
                Application.Restart();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during logout: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnLogout.Enabled = true;
            }
        }

        // onFormLoad Function
        private void DashboardForm_Load(object sender, EventArgs e)
        {
            lblWelcome.Text = $"Welcome, {_userName}!";
            CheckScannerDriver();
            EnsureServiceRunning();
        }

        // Starts the scanning service if it is not already running (logout stops it, login must start it again)
        private void EnsureServiceRunning()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    sc.Refresh();

                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch (InvalidOperationException)
            {
                MessageBox.Show($"Service '{ServiceName}' not found. Please ensure the service is installed.",
                    "Service Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Access denied. Please run this application as Administrator to control the service.",
                    "Permission Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.TimeoutException)
            {
                MessageBox.Show("Service start timed out. Please check the service manually.",
                    "Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting service: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Checks that the Zebra CoreScanner driver is installed (barcode scanning depends on it)
        private void CheckScannerDriver()
        {
            if (IsCoreScannerDriverInstalled())
            {
                lblScannerStatus.Text = "Scanner driver: OK";
                lblScannerStatus.ForeColor = Color.Green;
            }
            else
            {
                lblScannerStatus.Text = "Scanner driver NOT installed - barcode scanning will not work!";
                lblScannerStatus.ForeColor = Color.Red;

                MessageBox.Show(
                    "The Zebra CoreScanner driver is not installed on this computer.\n\n" +
                    "Barcode scanning will NOT work until it is installed.\n\n" +
                    "Please install: Zebra_CoreScanner_Driver_(64bit)_v3.07.0050",
                    "Scanner Driver Missing",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static bool IsCoreScannerDriverInstalled()
        {
            try
            {
                // The Zebra driver installs a Windows service named "CoreScanner"
                return ServiceController.GetServices()
                    .Any(s => s.ServiceName.Equals(CoreScannerServiceName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        //------------------------------------------------------------------------------------------------------
        // SCANNER CONFIGURATION - replicates the printed barcode configuration sheet in one click:
        //   1. switch to IBM Hand-held mode (needed first: HID keyboard mode only allows this hop)
        //   2. set factory defaults
        //   3. store lottery settings (I 2 of 5 any length, UPC/EAN family off, QR off, suffix, Blink autoscan)
        //   4. switch permanently to the chosen target mode (OPOS or HID-Keyboard)
        //------------------------------------------------------------------------------------------------------
        private async void btnChangeToOposMode_Click(object sender, EventArgs e)
        {
            await ConfigureScannerAsync(HostModeOpos, "OPOS");
        }

        private async void btnUpdateToHidKeyboard_Click(object sender, EventArgs e)
        {
            await ConfigureScannerAsync(HostModeHidKeyboard, "HID-Keyboard");
        }

        private async Task ConfigureScannerAsync(string targetHostMode, string targetModeName)
        {
            btnChangeToOposMode.Enabled = false;
            btnUpdateToHidKeyboard.Enabled = false;
            CCoreScannerClass? scanner = null;

            try
            {
                lblScannerStatus.ForeColor = Color.Black;
                lblScannerStatus.Text = "Connecting to scanner...";

                scanner = new CCoreScannerClass();
                short[] scannerTypes = { 1 };                      // 1 = all scanner types
                scanner.Open(0, scannerTypes, 1, out int status);
                if (status != 0)
                {
                    ShowScannerConfigResult(false, $"Could not connect to CoreScanner (status {status}).", targetModeName);
                    return;
                }

                int scannerId = await WaitForScannerAsync(scanner, 6);
                if (scannerId == 0)
                {
                    ShowScannerConfigResult(false, "No scanner found. Connect the scanner and try again.", targetModeName);
                    return;
                }

                // Step 1: switch to IBM Hand-held mode and wait for the scanner to reboot/reconnect
                lblScannerStatus.Text = "Step 1/4: Switching communication mode (scanner will reboot)...";
                SwitchHostMode(scanner, scannerId, HostModeIbmHandheld);
                await Task.Delay(5000);
                scannerId = await WaitForScannerAsync(scanner, 30);
                if (scannerId == 0)
                {
                    ShowScannerConfigResult(false, "Scanner did not reconnect after the mode switch. Unplug/replug it and try again.", targetModeName);
                    return;
                }

                // Step 2: factory defaults (barcode #1 on the sheet)
                lblScannerStatus.Text = "Step 2/4: Restoring factory defaults...";
                string defaultsXml = $"<inArgs><scannerID>{scannerId}</scannerID></inArgs>";
                scanner.ExecCommand(OpcodeSetParameterDefaults, ref defaultsXml, out _, out status);
                await Task.Delay(2000);

                // Belt and braces: RSM action attribute 6001 = Restore Defaults, written
                // permanently. Covers scanners where opcode 2015 only resets RAM (settings
                // would otherwise revert to old stored values after the final reboot).
                var defaultsFailed = new List<string>();
                StoreAttribute(scanner, scannerId, 6001, "X", "0", "Restore defaults (permanent)", defaultsFailed);
                await Task.Delay(2000);

                // Step 3: store the lottery settings permanently
                // (matches the 123Scan profile: I 2 of 5 any length, whole UPC/EAN family off,
                //  QR off, Enter key after data, Blink autoscan. Code 128 family stays at its
                //  factory default = enabled.)
                lblScannerStatus.Text = "Step 3/4: Applying lottery settings...";
                var failedSettings = new List<string>();

                // Enable Interleaved 2 of 5, any length (lottery barcodes)
                StoreAttribute(scanner, scannerId, 6, "F", "True", "Enable I 2 of 5", failedSettings);
                StoreAttribute(scanner, scannerId, 22, "B", "0", "I 2 of 5 any length (L1)", failedSettings);
                StoreAttribute(scanner, scannerId, 23, "B", "0", "I 2 of 5 any length (L2)", failedSettings);

                // Disable the whole UPC/EAN family (small barcodes on the tickets)
                StoreAttribute(scanner, scannerId, 1, "F", "False", "Disable UPC-A", failedSettings);
                StoreAttribute(scanner, scannerId, 2, "F", "False", "Disable UPC-E", failedSettings);
                StoreAttribute(scanner, scannerId, 12, "F", "False", "Disable UPC-E1", failedSettings);
                StoreAttribute(scanner, scannerId, 3, "F", "False", "Disable EAN-13", failedSettings);
                StoreAttribute(scanner, scannerId, 4, "F", "False", "Disable EAN-8", failedSettings);
                StoreAttribute(scanner, scannerId, 83, "F", "False", "Disable Bookland EAN", failedSettings);
                StoreAttribute(scanner, scannerId, 85, "F", "False", "Disable UCC Coupon Code", failedSettings);

                // Disable QR and the other 2D symbologies (matches the curated 123Scan HID config;
                // PDF417 stays enabled - both config files keep it on)
                StoreAttribute(scanner, scannerId, 293, "F", "False", "Disable QR Code", failedSettings);
                StoreAttribute(scanner, scannerId, 573, "F", "False", "Disable MicroQR", failedSettings);
                StoreAttribute(scanner, scannerId, 292, "F", "False", "Disable Data Matrix", failedSettings);
                StoreAttribute(scanner, scannerId, 574, "F", "False", "Disable Aztec", failedSettings);
                StoreAttribute(scanner, scannerId, 294, "F", "False", "Disable MaxiCode", failedSettings);

                // Data is always sent as scanned - no suffix (235=0). The single Enter after
                // each scan in HID-Keyboard mode comes from an ADF rule instead, copied
                // byte-for-byte from the proven 123Scan config ("Send all with enter").
                // In OPOS mode the ADF buffer is cleared so the service gets the raw code only.
                StoreAttribute(scanner, scannerId, 235, "B", "0", "Send data as scanned", failedSettings);

                bool appendEnter = targetHostMode == HostModeHidKeyboard;
                StoreAttribute(scanner, scannerId, 392, "A",
                    appendEnter ? "0x01 0x08 0x11 0xF0 0x47 0x0D" : "0x00",
                    appendEnter ? "ADF rule: send all with Enter" : "Clear ADF rules", failedSettings);

                // Blink autoscan mode
                StoreAttribute(scanner, scannerId, 138, "B", "7", "Blink autoscan mode", failedSettings);

                // Step 4: switch permanently to the target mode
                lblScannerStatus.Text = $"Step 4/4: Switching to {targetModeName} mode (scanner will reboot)...";
                int switchStatus = SwitchHostMode(scanner, scannerId, targetHostMode);
                if (switchStatus != 0)
                {
                    ShowScannerConfigResult(false, $"Scanner refused the {targetModeName} mode switch (status {switchStatus}). It may already be in {targetModeName} mode, or this model may not support it.", targetModeName);
                    return;
                }

                await Task.Delay(5000);
                scannerId = await WaitForScannerAsync(scanner, 30);

                if (scannerId == 0)
                {
                    ShowScannerConfigResult(false, $"Settings were applied, but the scanner did not reconnect in {targetModeName} mode. Unplug/replug it and check again.", targetModeName);
                    return;
                }

                // OPOS mode is "Full Disable": the scanner boots with scanning turned OFF
                // until software enables it. Best effort - harmless in OPOS, and must never
                // fail the whole operation in HID-Keyboard mode where command support is limited.
                try
                {
                    string enableXml = $"<inArgs><scannerID>{scannerId}</scannerID></inArgs>";
                    scanner.ExecCommand(OpcodeScanEnable, ref enableXml, out _, out _);
                }
                catch
                {
                    // Scanner in HID-Keyboard mode may reject commands - scanning works anyway
                }

                // Restart the scanning service so it reconnects cleanly to the reconfigured scanner
                lblScannerStatus.Text = "Restarting scanning service...";
                bool serviceRestarted = RestartWindowsService();

                string serviceNote = serviceRestarted
                    ? "and the service has been restarted."
                    : "but the scanning service could not be restarted - please restart it manually.";

                if (failedSettings.Count > 0)
                {
                    ShowScannerConfigResult(true,
                        $"Successfully updated to {targetModeName} mode {serviceNote}\n\n" +
                        "However, these settings could not be applied:\n\n- " +
                        string.Join("\n- ", failedSettings) +
                        "\n\nThey may not be supported by this scanner model.", targetModeName);
                }
                else
                {
                    ShowScannerConfigResult(true, $"Successfully updated to {targetModeName} mode {serviceNote}", targetModeName);
                }
            }
            catch (System.Runtime.InteropServices.COMException comEx) when ((uint)comEx.HResult == 0x80040154)
            {
                ShowScannerConfigResult(false, "The Zebra CoreScanner driver is not installed on this computer.", targetModeName);
            }
            catch (Exception ex)
            {
                ShowScannerConfigResult(false, $"Error configuring scanner: {ex.Message}", targetModeName);
            }
            finally
            {
                try { scanner?.Close(0, out _); } catch { }
                btnChangeToOposMode.Enabled = true;
                btnUpdateToHidKeyboard.Enabled = true;
            }
        }

        // Stops the scanning service if it is running, then starts it again.
        // Returns false if the restart did not complete.
        private bool RestartWindowsService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    sc.Refresh();

                    if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private void ShowScannerConfigResult(bool success, string message, string targetModeName)
        {
            lblScannerStatus.ForeColor = success ? Color.Green : Color.Red;
            lblScannerStatus.Text = success ? $"Scanner: {targetModeName} mode configured" : "Scanner configuration failed";

            MessageBox.Show(message,
                success ? "Scanner Configured" : "Scanner Configuration",
                MessageBoxButtons.OK,
                success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        // Polls CoreScanner until a scanner is connected (scanners disappear for a few
        // seconds while rebooting after a host mode switch). Returns 0 if none found.
        private static async Task<int> WaitForScannerAsync(CCoreScannerClass scanner, int timeoutSeconds)
        {
            for (int elapsed = 0; elapsed < timeoutSeconds; elapsed += 2)
            {
                try
                {
                    short numberOfScanners;
                    int[] scannerIdList = new int[255];
                    scanner.GetScanners(out numberOfScanners, scannerIdList, out string outXml, out int status);

                    if (status == 0 && numberOfScanners > 0)
                        return scannerIdList[0];
                }
                catch
                {
                    // CoreScanner busy while the scanner re-enumerates - keep polling
                }

                await Task.Delay(2000);
            }

            return 0;
        }

        private static int SwitchHostMode(CCoreScannerClass scanner, int scannerId, string hostModeCode)
        {
            string inXml = $"<inArgs><scannerID>{scannerId}</scannerID>" +
                           "<cmdArgs>" +
                           $"<arg-string>{hostModeCode}</arg-string>" +
                           "<arg-bool>TRUE</arg-bool>" +   // silent switch (no beeps)
                           "<arg-bool>TRUE</arg-bool>" +   // permanent (survives reboot/unplug)
                           "</cmdArgs></inArgs>";

            scanner.ExecCommand(OpcodeSwitchHostMode, ref inXml, out string outXml, out int status);
            return status;
        }

        // Stores one scanner setting permanently (survives reboot). Adds the setting's
        // name to failedSettings if the scanner rejects it.
        private static void StoreAttribute(CCoreScannerClass scanner, int scannerId,
            int attributeId, string dataType, string value, string settingName, List<string> failedSettings)
        {
            string inXml = $"<inArgs><scannerID>{scannerId}</scannerID><cmdArgs><arg-xml><attrib_list>" +
                           $"<attribute><id>{attributeId}</id><datatype>{dataType}</datatype><value>{value}</value></attribute>" +
                           "</attrib_list></arg-xml></cmdArgs></inArgs>";

            scanner.ExecCommand(OpcodeAttrStore, ref inXml, out string outXml, out int status);

            if (status != 0)
                failedSettings.Add(settingName);
        }

        private void StopWindowsService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                    {
                        // Wait for start to complete if starting
                        if (sc.Status == ServiceControllerStatus.StartPending)
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        }

                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        MessageBox.Show("Windows Service stopped successfully.", "Service Stopped", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        MessageBox.Show("Windows Service is already stopped.", "Service Stopped", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                MessageBox.Show($"Service '{ServiceName}' not found. Please ensure the service is installed.",
                    "Service Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Access denied. Please run this application as Administrator to control the service.",
                    "Permission Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.TimeoutException)
            {
                MessageBox.Show("Service stop operation timed out. Please check the service manually.",
                    "Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping service: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
