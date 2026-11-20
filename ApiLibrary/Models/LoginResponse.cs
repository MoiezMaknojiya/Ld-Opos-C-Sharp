using Newtonsoft.Json;

namespace ApiLibrary.Models
{
    public enum LoginFailureReason
    {
        None,           // Login succeeded
        InvalidUuid,    // Server was reached but rejected the UUID
        ServerError,    // Server was reached but had an internal problem (5xx)
        NetworkError    // Server could not be reached (no internet / timeout)
    }

    public class LoginResponse
    {
        public int success { get; set; }
        public UserData? user { get; set; }
        public string? accessToken { get; set; }
        public int store_id { get; set; }

        // Set locally, never sent by the server - tells the UI why login failed
        [JsonIgnore]
        public LoginFailureReason FailureReason { get; set; } = LoginFailureReason.None;
    }
}
