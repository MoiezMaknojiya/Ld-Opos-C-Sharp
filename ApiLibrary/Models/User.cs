namespace ApiLibrary.Models
{
    public class User
    {
        public int UUID { get; set; }
        public string? Username { get; set; }
        public string? AccessToken { get; set; }
        public int StoreId { get; set; }
        public string? LoginTime { get; set; }
    }
}
