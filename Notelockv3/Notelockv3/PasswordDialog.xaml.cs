using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Notelockv3.Services;

namespace Notelockv3
{
    public sealed partial class PasswordDialog : ContentDialog
    {
        private bool _isEncryptMode;
        public string? ResultPassword { get; private set; }

        public PasswordDialog(bool isEncryptMode)
        {
            this.InitializeComponent();
            _isEncryptMode = isEncryptMode;

            ApplyLocalization();
        }

        private void ApplyLocalization()
        {
            var loc = LocalizationService.Instance;
            FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            CloseButtonText = loc.GetString("Cancel");
            if (PasswordLabel != null) PasswordLabel.Text = loc.GetString("PasswordLabel");
            if (ConfirmPasswordLabel != null) ConfirmPasswordLabel.Text = loc.GetString("ConfirmPasswordLabel");
            if (PasswordEntry != null) PasswordEntry.PlaceholderText = loc.GetString("EnterPassword");
            if (ConfirmPasswordEntry != null) ConfirmPasswordEntry.PlaceholderText = loc.GetString("RepeatPassword");

            if (_isEncryptMode)
            {
                ConfirmPasswordLayout.Visibility = Visibility.Visible;
                PrimaryButtonText = loc.GetString("EncryptSave");
                TitleLabel.Text = loc.GetString("EncryptDoc");
                SubtitleLabel.Text = loc.GetString("EncryptSubtitle");
            }
            else
            {
                ConfirmPasswordLayout.Visibility = Visibility.Collapsed;
                PrimaryButtonText = loc.GetString("DecryptOpen");
                TitleLabel.Text = loc.GetString("DecryptDoc");
                SubtitleLabel.Text = loc.GetString("DecryptSubtitle");
            }
        }

        private bool ValidatePasswordInput()
        {
            var loc = LocalizationService.Instance;
            ErrorLabel.Visibility = Visibility.Collapsed;
            string pass = PasswordEntry.Password;

            if (string.IsNullOrWhiteSpace(pass))
            {
                ErrorLabel.Text = loc.GetString("PasswordEmptyError");
                ErrorLabel.Visibility = Visibility.Visible;
                return false;
            }

            if (_isEncryptMode)
            {
                string confirm = ConfirmPasswordEntry.Password;
                if (pass != confirm)
                {
                    ErrorLabel.Text = loc.GetString("PasswordMismatchError");
                    ErrorLabel.Visibility = Visibility.Visible;
                    return false;
                }
            }

            ResultPassword = pass;
            return true;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (!ValidatePasswordInput())
            {
                args.Cancel = true;
            }
        }

        private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ResultPassword = null;
        }

        private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_isEncryptMode)
                {
                    ConfirmPasswordEntry.Focus(FocusState.Programmatic);
                    e.Handled = true;
                }
                else
                {
                    if (ValidatePasswordInput())
                    {
                        this.Hide();
                    }
                    e.Handled = true;
                }
            }
        }

        private void OnConfirmPasswordKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (ValidatePasswordInput())
                {
                    this.Hide();
                }
                e.Handled = true;
            }
        }
    }
}
