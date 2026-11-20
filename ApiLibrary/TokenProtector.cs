using System.Security.Cryptography;
using System.Text;

namespace ApiLibrary
{
    // Encrypts/decrypts the access token using Windows DPAPI (machine scope).
    // The OS manages the key - nothing secret is stored in our code or files.
    // Machine scope is required because the manager app (user account) and the
    // Windows service (LocalSystem account) must both be able to decrypt.
    internal static class TokenProtector
    {
        public static string? Protect(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText),
                null,
                DataProtectionScope.LocalMachine);

            return Convert.ToBase64String(encrypted);
        }

        public static string? Unprotect(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return encryptedText;

            try
            {
                byte[] decrypted = ProtectedData.Unprotect(
                    Convert.FromBase64String(encryptedText),
                    null,
                    DataProtectionScope.LocalMachine);

                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                // Not decryptable: database was copied from another machine,
                // or the value is corrupted. Treat as "no token".
                return null;
            }
        }
    }
}
