namespace Notelock;

public partial class PasswordPage : ContentPage
{
    private TaskCompletionSource<string?> _tcs;
    private bool _isEncryptMode;

    public PasswordPage(TaskCompletionSource<string?> tcs, bool isEncryptMode)
    {
        InitializeComponent();
        _tcs = tcs;
        _isEncryptMode = isEncryptMode;

        if (_isEncryptMode)
        {
            ConfirmPasswordLayout.IsVisible = true;
            ActionBtn.Text = "Encrypt & Save";
        }
        else
        {
            ConfirmPasswordLayout.IsVisible = false;
            ActionBtn.Text = "Decrypt & Open";
        }
    }

    private void OnActionClicked(object sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        string pass = PasswordEntry.Text;

        if (string.IsNullOrWhiteSpace(pass))
        {
            ErrorLabel.Text = "Password cannot be empty";
            ErrorLabel.IsVisible = true;
            return;
        }

        if (_isEncryptMode)
        {
            string confirm = ConfirmPasswordEntry.Text;
            if (pass != confirm)
            {
                ErrorLabel.Text = "Passwords do not match";
                ErrorLabel.IsVisible = true;
                return;
            }
        }

        // Return the valid password
        _tcs.SetResult(pass);
        Navigation.PopModalAsync();
    }

    private void OnCancelClicked(object sender, EventArgs e)
    {
        _tcs.SetResult(null);
        Navigation.PopModalAsync();
    }
}
