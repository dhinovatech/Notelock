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
            TitleLabel.Text = "Encrypt Document";
            SubtitleLabel.Text = "Set a strong password to protect your file's content.";
        }
        else
        {
            ConfirmPasswordLayout.IsVisible = false;
            ActionBtn.Text = "Decrypt & Open";
            TitleLabel.Text = "Decrypt Document";
            SubtitleLabel.Text = "Enter your password to unlock the encrypted document.";
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

    private void OnPasswordCompleted(object sender, EventArgs e)
    {
        if (_isEncryptMode)
        {
            ConfirmPasswordEntry.Focus();
        }
        else
        {
            OnActionClicked(this, EventArgs.Empty);
        }
    }

    private void OnConfirmPasswordCompleted(object sender, EventArgs e)
    {
        OnActionClicked(this, EventArgs.Empty);
    }
}
