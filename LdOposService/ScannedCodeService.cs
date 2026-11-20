using ApiLibrary;
using ApiLibrary.Models;
using Interop.CoreScanner;
using System.Xml;

namespace LdOposService
{
    public class ScannedCodeService : BackgroundService
    {
        private readonly ILogger<ScannedCodeService> _logger;
        private readonly ApiServices _apiService;
        private readonly DatabaseServices _dbHelper;
        private CCoreScannerClass? _scanner;
        private User? _currentUser;                    // Cached logged-in user - avoids querying the database on every scan

        private volatile bool _isUploading = false;    // Flag: Is upload in progress? [volatile: accessed from multiple threads]

        // CoreScanner opcodes
        private const int OpcodeGetVersion = 1000;
        private const int OpcodeRegisterForEvents = 1001;
        private const int OpcodeUnregisterForEvents = 1002;
        private const int OpcodeScanEnable = 2014;

        public ScannedCodeService(ILogger<ScannedCodeService> logger, ApiServices apiService)
        {
            _logger = logger;
            _apiService = apiService;
            _dbHelper = new DatabaseServices();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("======================================== SERVICE START ========================================");

            // Outer self-healing loop - whatever goes wrong, clean up and start over.
            // The service must keep running 24/7 and recover from any error on its own.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Create database tables if they don't exist
                    DatabaseServices.InitializeDatabase();

                    // Keep retrying until the scanner SDK connection succeeds
                    while (!stoppingToken.IsCancellationRequested && !StartScanning())
                    {
                        _logger.LogWarning("Could not connect to CoreScanner. Retrying in 30 seconds.\n");
                        await Task.Delay(30000, stoppingToken);
                    }

                    // Connected - barcode events now arrive via OnBarcodeEvent.
                    // Every 60 seconds: refresh the cached user (detects logout/login),
                    // re-enable scanning (covers a re-plugged scanner in OPOS "Full Disable" mode),
                    // retry pending uploads and verify the scanner connection is still alive.
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        RefreshCurrentUser();
                        EnableScanning();
                        await UploadUnprocessedTransactionsAsync();
                        await Task.Delay(60000, stoppingToken);

                        if (!IsScannerConnectionAlive())
                        {
                            _logger.LogWarning("CoreScanner connection lost. Reconnecting.\n");
                            StopScanning();
                            break;  // Back to the outer loop - reconnect from scratch
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Service cancellation requested.\n");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in ScannedCodeService. Restarting work loop in 10 seconds.\n\n");
                    StopScanning();

                    try
                    {
                        await Task.Delay(10000, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            StopScanning();
        }

        //------------------------------------------------------------------------------------------------------
        // CURRENT USER CACHE - credentials live in a local variable; the database is only
        // queried when nothing is cached, plus one refresh per 60-second cycle
        //------------------------------------------------------------------------------------------------------
        private User? GetCurrentUser()
        {
            if (_currentUser == null)
            {
                RefreshCurrentUser();
            }
            return _currentUser;
        }

        private void RefreshCurrentUser()
        {
            try
            {
                _currentUser = _dbHelper.GetLastLoggedInUser();
            }
            catch (Exception ex)
            {
                // Transient database error - keep the existing cached user instead of dropping scans
                _logger.LogError(ex, "Error refreshing current user from database. Keeping cached user.\n\n");
            }
        }

        // Enables the scan beam on every connected scanner. Required for OPOS mode
        // ("Full Disable"): the scanner stays dark until an application enables it.
        // Safe to call repeatedly - enabling an already-enabled scanner does nothing.
        private void EnableScanning()
        {
            if (_scanner == null)
                return;

            try
            {
                short numberOfScanners;
                int[] scannerIdList = new int[255];
                _scanner.GetScanners(out numberOfScanners, scannerIdList, out string outXml, out int status);

                if (status != 0)
                    return;

                for (int i = 0; i < numberOfScanners; i++)
                {
                    string inXml = $"<inArgs><scannerID>{scannerIdList[i]}</scannerID></inArgs>";
                    _scanner.ExecCommand(OpcodeScanEnable, ref inXml, out string _, out int _);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not enable scanning: {message}\n", ex.Message);
            }
        }

        // Lightweight health check - asks CoreScanner for its version.
        // If this fails, the connection (or the CoreScanner service itself) has died.
        private bool IsScannerConnectionAlive()
        {
            if (_scanner == null)
                return false;

            try
            {
                string inXml = "<inArgs></inArgs>";
                _scanner.ExecCommand(OpcodeGetVersion, ref inXml, out string outXml, out int status);
                return status == 0;
            }
            catch
            {
                return false;
            }
        }

        //------------------------------------------------------------------------------------------------------
        // START SCANNING - open CoreScanner and subscribe to barcode events
        //------------------------------------------------------------------------------------------------------
        private bool StartScanning()
        {
            try
            {
                _scanner = new CCoreScannerClass();

                // Open API - 1 = all scanner types
                short[] scannerTypes = new short[1];
                scannerTypes[0] = 1;
                short numberOfScannerTypes = 1;
                _scanner.Open(0, scannerTypes, numberOfScannerTypes, out int status);

                if (status != 0)
                {
                    _logger.LogWarning("CoreScanner Open failed. Status: {status}.\n", status);
                    _scanner = null;
                    return false;
                }

                // Subscribe to barcode events
                _scanner.BarcodeEvent += new _ICoreScannerEvents_BarcodeEventEventHandler(OnBarcodeEvent);

                string inXml = "<inArgs>" +
                               "<cmdArgs>" +
                               "<arg-int>1</arg-int>" +   // Number of events to subscribe
                               "<arg-int>1</arg-int>" +   // Event ID 1 = barcode event
                               "</cmdArgs>" +
                               "</inArgs>";
                _scanner.ExecCommand(OpcodeRegisterForEvents, ref inXml, out string outXml, out status);

                if (status != 0)
                {
                    _logger.LogWarning("CoreScanner event subscription failed. Status: {status}.\n", status);
                    StopScanning();
                    return false;
                }

                // Scanners in OPOS mode ("IBM Hand-Held with Full Disable") boot with
                // scanning turned OFF until software enables it - enable them now
                EnableScanning();

                _logger.LogInformation("Connected to CoreScanner. Waiting for barcode scans.\n");
                return true;
            }
            catch (System.Runtime.InteropServices.COMException comEx) when ((uint)comEx.HResult == 0x80040154)
            {
                // REGDB_E_CLASSNOTREG - the CoreScanner COM class is not registered on this machine
                _logger.LogError("Zebra CoreScanner driver is NOT installed on this machine. " +
                    "Install the Zebra CoreScanner driver, then the service will connect automatically.\n\n");
                _scanner = null;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to CoreScanner.\n\n");
                _scanner = null;
                return false;
            }
        }

        //------------------------------------------------------------------------------------------------------
        // ON BARCODE SCAN
        //------------------------------------------------------------------------------------------------------
        private void OnBarcodeEvent(short eventType, ref string pscanData)
        {
            // Capture the data and hand off to an async method
            // (this COM event handler cannot be async because of the ref parameter)
            string scanData = pscanData;
            _ = HandleBarcodeAsync(scanData);
        }

        private async Task HandleBarcodeAsync(string scanData)
        {
            try
            {
                string barcode = DecodeBarcode(scanData);

                _logger.LogInformation("======================================== BARCODE SCANNED ========================================");
                _logger.LogInformation("ScannedCode: {barcode}.\n", barcode);

                if (string.IsNullOrWhiteSpace(barcode))
                {
                    _logger.LogWarning("Scanned barcode is empty. Skipping.\n");
                    return;
                }

                // No logged-in user -> ignore the scan completely (do not save it)
                if (GetCurrentUser() == null)
                {
                    _logger.LogWarning("No user is logged in. ScannedCode {barcode} ignored.\n", barcode);
                    return;
                }

                // Step 1: Save to local database first
                // IMPORTANT: This ensures the scan is never lost, even if internet is down
                _dbHelper.AddTransaction(barcode);
                _logger.LogInformation("ScannedCode saved to database.\n");

                // Step 2: Try to upload immediately
                await UploadUnprocessedTransactionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling barcode event.\n\n");
            }
        }

        //------------------------------------------------------------------------------------------------------
        // UPLOAD - send all pending scans to the server, delete each one after successful upload
        //------------------------------------------------------------------------------------------------------
        private async Task UploadUnprocessedTransactionsAsync()
        {
            // Prevent overlapping uploads
            if (_isUploading)
            {
                return;
            }

            _isUploading = true;
            try
            {
                var pendingTransactions = _dbHelper.GetPendingTransactions();

                if (pendingTransactions.Count == 0)
                {
                    return;
                }

                // Need the logged-in user for user_id and store_id (cached - no database query if already loaded)
                User? currentUser = GetCurrentUser();
                if (currentUser == null)
                {
                    _logger.LogWarning("No logged-in user found. {count} scan(s) will stay queued until a user logs in.\n", pendingTransactions.Count);
                    return;
                }

                _logger.LogInformation("======================================== UPLOAD START ========================================");
                _logger.LogInformation("Found {count} pending scan(s) to upload.\n", pendingTransactions.Count);

                foreach (var transaction in pendingTransactions)
                {
                    if (string.IsNullOrWhiteSpace(transaction.ScannedCode))
                    {
                        // Empty code is useless - remove it from the queue
                        _dbHelper.DeleteTransaction(transaction.Id);
                        continue;
                    }

                    bool uploadSuccess = await _apiService.UpdateTicketAsync(
                        currentUser.UUID,
                        currentUser.StoreId,
                        transaction.ScannedCode);

                    if (uploadSuccess)
                    {
                        // Upload succeeded - remove from queue
                        _dbHelper.DeleteTransaction(transaction.Id);
                        _logger.LogInformation("ScannedCode {code} uploaded successfully.\n", transaction.ScannedCode);
                    }
                    else
                    {
                        // Upload failed (network down or server error) - stop and retry the whole queue later
                        _logger.LogWarning("Failed to upload ScannedCode {code}. Will retry later.\n", transaction.ScannedCode);
                        break;
                    }
                }

                _logger.LogInformation("======================================== UPLOAD END ========================================\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading pending scans.\n\n");
            }
            finally
            {
                _isUploading = false;
            }
        }

        //------------------------------------------------------------------------------------------------------
        // DECODE BARCODE - CoreScanner delivers the label as space-separated hex bytes inside XML
        //------------------------------------------------------------------------------------------------------
        private string DecodeBarcode(string strXml)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(strXml);

            string barcode = xmlDoc.DocumentElement?.GetElementsByTagName("datalabel").Item(0)?.InnerText ?? "";

            string strData = string.Empty;
            string[] numbers = barcode.Split(' ');

            foreach (string number in numbers)
            {
                if (string.IsNullOrEmpty(number))
                {
                    break;
                }
                strData += ((char)Convert.ToInt32(number, 16)).ToString();
            }

            // Strip any transmitted suffix from the end (Enter/CR/LF or its '^' representation)
            // so only the actual barcode value is stored and uploaded
            return strData.TrimEnd('\r', '\n', '\t', ' ', '^');
        }

        //------------------------------------------------------------------------------------------------------
        // STOP SCANNING - unsubscribe and close the CoreScanner connection
        //------------------------------------------------------------------------------------------------------
        private void StopScanning()
        {
            if (_scanner == null)
                return;

            try
            {
                // Detach the event handler so the old COM object cannot keep firing events
                _scanner.BarcodeEvent -= OnBarcodeEvent;

                string inXml = "<inArgs>" +
                               "<cmdArgs>" +
                               "<arg-int>1</arg-int>" +   // Number of events to unsubscribe
                               "<arg-int>1</arg-int>" +   // Event ID 1 = barcode event
                               "</cmdArgs>" +
                               "</inArgs>";
                _scanner.ExecCommand(OpcodeUnregisterForEvents, ref inXml, out string outXml, out int status);

                _scanner.Close(0, out status);
                _logger.LogInformation("CoreScanner connection closed.\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing CoreScanner connection.\n\n");
            }
            finally
            {
                _scanner = null;
            }
        }
    }
}
