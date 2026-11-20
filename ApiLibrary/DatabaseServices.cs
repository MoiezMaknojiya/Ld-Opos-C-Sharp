using ApiLibrary.Models;
using Dapper;
using System.Data.SQLite;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ApiLibrary
{
    public class DatabaseServices
    {
        // Folder layout:
        //   C:\ProgramData\LdOposService\        <- normal permissions (logs stay readable by anyone)
        //   C:\ProgramData\LdOposService\db\     <- locked down: SYSTEM + Administrators only
        private static readonly string baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LdOposService"
        );

        private static readonly string dbDirectory = Path.Combine(baseDirectory, "db");
        private static readonly string dbPath = Path.Combine(dbDirectory, "oPosData.db");

        private static string ConnectionString => $"Data Source={dbPath}";
        private static SQLiteConnection GetConnection()
        {
            return new SQLiteConnection(ConnectionString);
        }

        public static void InitializeDatabase()
        {
            if (!Directory.Exists(baseDirectory))
                Directory.CreateDirectory(baseDirectory);

            try
            {
                // Make sure the BASE folder uses normal inherited permissions
                // (undoes the lockdown a previous version applied to it, so the
                // logs folder inside it stays readable by anyone)
                DirectoryInfo baseInfo = new DirectoryInfo(baseDirectory);
                DirectorySecurity baseSecurity = baseInfo.GetAccessControl();
                baseSecurity.SetAccessRuleProtection(false, false);

                foreach (FileSystemAccessRule rule in
                         baseSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    baseSecurity.RemoveAccessRule(rule);
                }

                baseInfo.SetAccessControl(baseSecurity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR resetting base folder permissions: {ex.Message}");
            }

            if (!Directory.Exists(dbDirectory))
                Directory.CreateDirectory(dbDirectory);

            try
            {
                // LOCK DOWN only the db subfolder: only SYSTEM (the service) and
                // Administrators (the manager app, which always runs elevated) can
                // access it. Standard users and non-elevated processes are denied
                // by Windows itself.
                DirectoryInfo dirInfo = new DirectoryInfo(dbDirectory);
                DirectorySecurity security = dirInfo.GetAccessControl();

                // Cut inheritance from the parent folder
                security.SetAccessRuleProtection(true, false);

                // Remove any previously granted rules
                foreach (FileSystemAccessRule rule in
                         security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    security.RemoveAccessRule(rule);
                }

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

                security.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                security.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                dirInfo.SetAccessControl(security);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"PERMISSION ERROR locking database folder: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR locking database folder: {ex.Message}");
            }

            // Move the database from the old location (directly in the base folder)
            // into the locked db subfolder
            try
            {
                string oldDbPath = Path.Combine(baseDirectory, "oPosData.db");
                if (File.Exists(oldDbPath) && !File.Exists(dbPath))
                {
                    File.Move(oldDbPath, dbPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR moving database to db subfolder: {ex.Message}");
            }

            // Create the database file if it doesn't exist
            if (!File.Exists(dbPath))
            {
                SQLiteConnection.CreateFile(dbPath);
            }

            try
            {
                // Make sure the database file (and any -wal/-journal files) simply inherit
                // the locked-down folder permissions instead of carrying their own rules
                FileInfo fileInfo = new FileInfo(dbPath);
                FileSecurity fileSecurity = fileInfo.GetAccessControl();
                fileSecurity.SetAccessRuleProtection(false, false);

                foreach (FileSystemAccessRule rule in
                         fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    fileSecurity.RemoveAccessRule(rule);
                }

                fileInfo.SetAccessControl(fileSecurity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR resetting database file permissions: {ex.Message}");
            }

            using (var connection = GetConnection())
            {
                // Migrate old User table (with removed columns like Id/Email/FolderPath/DeptId):
                // drop and recreate
                var userColumns = connection.Query<string>(
                    "SELECT name FROM pragma_table_info('User')").ToList();
                if (userColumns.Count > 0 &&
                    (userColumns.Contains("Id") || userColumns.Contains("Email") ||
                     userColumns.Contains("FolderPath") || userColumns.Contains("DeptId")))
                {
                    connection.Execute("DROP TABLE User");
                }

                connection.Execute(@"
                    CREATE TABLE IF NOT EXISTS User (
                        UUID INTEGER NOT NULL UNIQUE,
                        Username TEXT,
                        AccessToken TEXT NOT NULL,
                        StoreId INTEGER NOT NULL DEFAULT 0,
                        LoginTime TEXT NOT NULL
                    )");

                // Migrate old Transactions table (pre-ScannedCode schema, or with removed
                // columns like IsProcessed/CreatedAt): drop and recreate
                var transactionColumns = connection.Query<string>(
                    "SELECT name FROM pragma_table_info('Transactions')").ToList();
                if (transactionColumns.Count > 0 &&
                    (!transactionColumns.Contains("ScannedCode") ||
                     transactionColumns.Contains("IsProcessed") || transactionColumns.Contains("CreatedAt")))
                {
                    connection.Execute("DROP TABLE Transactions");
                }

                connection.Execute(@"
                    CREATE TABLE IF NOT EXISTS Transactions (
                        ScannedCode TEXT NOT NULL
                    )");
            }
        }

        public void AddUser(User user)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    string query = @"
                            INSERT INTO User (UUID, Username, AccessToken, StoreId, LoginTime)
                            VALUES (@UUID, @Username, @AccessToken, @StoreId, @LoginTime)";

                    // AccessToken is stored encrypted (Windows DPAPI) so it cannot be read
                    // by opening the database file directly or copying it to another machine
                    connection.Execute(query, new
                    {
                        user.UUID,
                        user.Username,
                        AccessToken = TokenProtector.Protect(user.AccessToken),
                        user.StoreId,
                        user.LoginTime
                    });
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to add user: {ex.Message}");
            }
        }

        public void DeleteAllUsers()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Execute("DELETE FROM User");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete users: {ex.Message}");
            }
        }

        public void DeleteAllTransactions()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    connection.Execute("DELETE FROM Transactions");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete transactions: {ex.Message}");
            }
        }

        public void AddTransaction(string? scannedCode)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    // Duplicates are allowed - same ScannedCode can be inserted multiple times
                    connection.Execute(
                        "INSERT INTO Transactions (ScannedCode) VALUES (@ScannedCode)",
                        new { ScannedCode = scannedCode ?? "" });
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to add transaction: {ex.Message}");
            }
        }

        public void DeleteTransaction(long id)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    // rowid targets the exact row, even when the same ScannedCode exists multiple times
                    connection.Execute("DELETE FROM Transactions WHERE rowid = @Id", new { Id = id });
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to delete transaction: {ex.Message}");
            }
        }

        public User? GetLastLoggedInUser()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    // Dapper maps columns to User properties by name - no index numbers needed
                    var user = connection.QueryFirstOrDefault<User>(
                        "SELECT * FROM User ORDER BY LoginTime DESC LIMIT 1");

                    // AccessToken is stored encrypted - decrypt it before handing it out
                    if (user != null)
                    {
                        user.AccessToken = TokenProtector.Unprotect(user.AccessToken);
                    }

                    return user;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load last logged-in user: {ex.Message}");
            }
        }

        public List<Transaction> GetPendingTransactions()
        {
            try
            {
                using (var connection = GetConnection())
                {
                    // Every row in the table is pending - uploaded rows are deleted immediately.
                    // rowid is aliased to Id so each row can be targeted individually
                    return connection.Query<Transaction>(
                        "SELECT rowid AS Id, ScannedCode FROM Transactions")
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get pending transactions: {ex.Message}");
            }
        }
    }
}
