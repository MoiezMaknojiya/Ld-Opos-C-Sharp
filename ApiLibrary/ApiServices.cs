using System.Text;
using Newtonsoft.Json;
using ApiLibrary.Models;


namespace ApiLibrary
{
    public class ApiServices
    {
        private const string LoginUrl = "https://lotteryscreen.app/api/user/login";
        private const string LogoutUrl = "https://lotteryscreen.app/api/user/logout";
        private const string UpdateTicketUrl = "https://lotteryscreen.app/api/updateTicket";

        // Shared across all ApiServices instances - one HttpClient for the whole process
        // prevents socket exhaustion when forms create their own ApiServices
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<LoginResponse> LoginAsync(string? uuid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    return FailedLogin(LoginFailureReason.InvalidUuid);
                }

                // Serialize instead of hand-building JSON - handles quotes/special characters safely
                string json = JsonConvert.SerializeObject(new { uuid });
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                HttpResponseMessage response = await _httpClient.PostAsync(LoginUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    var loginResponse = JsonConvert.DeserializeObject<LoginResponse>(responseContent);
                    if (loginResponse == null)
                    {
                        return FailedLogin(LoginFailureReason.ServerError);
                    }

                    // Server answered - if it did not accept the UUID, mark why
                    loginResponse.FailureReason = loginResponse.success == 1
                        ? LoginFailureReason.None
                        : LoginFailureReason.InvalidUuid;
                    return loginResponse;
                }

                // Server was reached but returned an error status
                // 4xx = it rejected the request (bad UUID), 5xx = server-side problem
                return (int)response.StatusCode >= 500
                    ? FailedLogin(LoginFailureReason.ServerError)
                    : FailedLogin(LoginFailureReason.InvalidUuid);
            }
            catch
            {
                // Could not reach the server at all (no internet, DNS failure, timeout)
                return FailedLogin(LoginFailureReason.NetworkError);
            }
        }

        private static LoginResponse FailedLogin(LoginFailureReason reason)
        {
            return new LoginResponse
            {
                success = 0,
                user = null,
                accessToken = null,
                store_id = 0,
                FailureReason = reason
            };
        }

        public async Task<bool> LogoutAsync(string accessToken)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                request.Content = new StringContent("", Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> UpdateTicketAsync(int userId, int storeId, string scanCode)
        {
            try
            {
                var payload = new Dictionary<string, string>
                {
                    { "user_id", userId.ToString() },
                    { "store_id", storeId.ToString() },
                    { "scan_code", scanCode },
                    { "dpScan", "1" }
                };

                var content = new FormUrlEncodedContent(payload);

                // Use CancellationTokenSource for per-request timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                HttpResponseMessage response = await _httpClient.PostAsync(UpdateTicketUrl, content, cts.Token);

                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Network error updating ticket: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"Request timeout updating ticket: {ex.Message}");
                return false;
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Console.WriteLine($"Socket error updating ticket: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating ticket: {ex.Message}");
                return false;
            }
        }

    }
}