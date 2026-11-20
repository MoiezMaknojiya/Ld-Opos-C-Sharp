using ApiLibrary;
using ApiLibrary.Models;

namespace LdOposManager
{
    public partial class LoginForm : Form
    {
        private readonly DatabaseServices _dbHelper;
        private readonly ApiServices _apiService;

        public LoginForm()
        {
            InitializeComponent();
            _dbHelper = new DatabaseServices();
            _apiService = new ApiServices();
        }

        // onClick Login Function
        private async void btnLogin_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtUUID.Text))
            {
                MessageBox.Show("Please enter UUID", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnLogin.Enabled = false;

            try
            {
                var loginResponse = await _apiService.LoginAsync(txtUUID.Text);

                if (loginResponse.success == 1 && loginResponse.user != null)
                {
                    var user = new User
                    {
                        UUID = loginResponse.user.id,
                        Username = loginResponse.user.username,
                        AccessToken = loginResponse.accessToken,
                        StoreId = loginResponse.store_id,
                        LoginTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };

                    // Clear any leftover user rows first - the User table must only ever hold one row
                    _dbHelper.DeleteAllUsers();
                    _dbHelper.AddUser(user);
                    DashboardForm dashboard = new DashboardForm(loginResponse.user.username ?? "", loginResponse.accessToken ?? "");

                    this.Hide();
                    dashboard.ShowDialog();
                    this.Close();
                }
                else
                {
                    string message = loginResponse.FailureReason switch
                    {
                        LoginFailureReason.NetworkError => "Cannot reach the server. Please check your internet connection and try again.",
                        LoginFailureReason.ServerError => "The server had a problem. Please try again in a few minutes.",
                        _ => "Login Failed: Invalid UUID"
                    };

                    MessageBox.Show(message, "Login Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    btnLogin.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnLogin.Enabled = true;
            }
        }

        private void LoginForm_Load(object sender, EventArgs e)
        {
            // Initialize database
            DatabaseServices.InitializeDatabase();

            try
            {
                var hasUser = _dbHelper.GetLastLoggedInUser();

                if (hasUser != null)
                {
                    // Auto-login and open dashboard
                    DashboardForm dashboard = new DashboardForm(
                        hasUser.Username ?? "",
                        hasUser.AccessToken ?? ""
                    );

                    this.Hide();
                    dashboard.ShowDialog();
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking saved user: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}