namespace ApiLibrary.Models
{
    public class Transaction
    {
        public long Id { get; set; }            // SQLite rowid - identifies a specific row even when ScannedCode is duplicated
        public string? ScannedCode { get; set; }
    }
}
