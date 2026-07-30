using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Notelockv3.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Notelockv3
{
    public sealed partial class MainWindow : Window
    {
        private string? _currentFilePath;
        private string? _currentPassword;
        private bool _isDirty;
        private string _loadedContent = string.Empty;
        
        private double _zoomPercentage = 100.0;
        private double _baseFontSize = 14.0;
        private bool _isStatusBarVisible = true;
        private bool _isWordWrapEnabled = true;

        // Undo/Redo history variables
        private DispatcherTimer? _undoTimer;
        private List<(string Text, int SelectionStart, int SelectionLength)> _history = new List<(string Text, int SelectionStart, int SelectionLength)>();
        private int _historyIndex = -1;
        private bool _isUndoRedoAction = false;
        private bool _isWindowClosingAllowed = false;
        private bool _isLoadingFile = false;
        private bool _isClosing = false;
        private bool _isFirstActivation = true;
        private EventHandler _onLanguageChanged;

        public MainWindow(string[]? args = null)
        {
            this.InitializeComponent();

            if (this.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = ElementTheme.Default;
            }

            _onLanguageChanged = (s, e) => ApplyLocalization();
            LocalizationService.Instance.LanguageChanged += _onLanguageChanged;
            ApplyLocalization();

            // Set Window & Taskbar Icon
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppContext.BaseDirectory, "Images", "appicon.ico");
                }
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
                }
                if (File.Exists(iconPath))
                {
                    SetTaskbarIcon(iconPath);
                }
            }
            catch { }

            // Handle App Window closing & closed to clean up resources safely
            this.AppWindow.Closing += OnAppWindowClosing;
            this.Closed += OnWindowClosed;

            // Setup Undo Timer (debounce capturing)
            _undoTimer = new DispatcherTimer();
            _undoTimer.Interval = TimeSpan.FromSeconds(1);
            _undoTimer.Tick += (s, e) =>
            {
                if (_isClosing) return;
                _undoTimer?.Stop();
                try
                {
                    CaptureHistoryState(MainEditor.Text ?? string.Empty, MainEditor.SelectionStart, MainEditor.SelectionLength);
                }
                catch { }
            };

            // Seed initial history
            _history.Add((string.Empty, 0, 0));
            _historyIndex = 0;

            MainEditor.TextChanged += (s, e) =>
            {
                if (_isClosing || _isLoadingFile) return;

                string currentText = MainEditor.Text ?? string.Empty;
                bool newIsDirty = NormalizeLineEndings(currentText) != NormalizeLineEndings(_loadedContent);
                if (_isDirty != newIsDirty)
                {
                    _isDirty = newIsDirty;
                    UpdateTitle();
                }

                if (!_isUndoRedoAction)
                {
                    _undoTimer?.Stop();
                    _undoTimer?.Start();
                }

                UpdateCursorPosLabel();
                UpdateLineEndingsLabel();
            };

            MainEditor.SelectionChanged += (s, e) =>
            {
                if (_isClosing) return;
                UpdateCursorPosLabel();
            };

            // Setup additional Ctrl + '=' and Ctrl + '-' zoom keyboard accelerators
            var zoomInAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = (Windows.System.VirtualKey)187,
                Modifiers = Windows.System.VirtualKeyModifiers.Control
            };
            zoomInAccel.Invoked += (s, e) => { OnZoomInClicked(s, new RoutedEventArgs()); e.Handled = true; };
            ZoomInMenuItem.KeyboardAccelerators.Add(zoomInAccel);

            var zoomOutAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = (Windows.System.VirtualKey)189,
                Modifiers = Windows.System.VirtualKeyModifiers.Control
            };
            zoomOutAccel.Invoked += (s, e) => { OnZoomOutClicked(s, new RoutedEventArgs()); e.Handled = true; };
            ZoomOutMenuItem.KeyboardAccelerators.Add(zoomOutAccel);

            // Initial state updates on load
            this.Activated += async (s, e) =>
            {
                if (!_isFirstActivation) return;
                _isFirstActivation = false;

                UpdateWordWrap();
                UpdateZoom();

                if (args != null && args.Length > 0)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        string arg = args[i];
                        if (!arg.StartsWith("-") && !arg.StartsWith("/") && File.Exists(arg))
                        {
                            await OpenFile(arg);
                            break;
                        }
                    }
                }
            };
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _isClosing = true;
            if (_onLanguageChanged != null)
            {
                LocalizationService.Instance.LanguageChanged -= _onLanguageChanged;
            }
            if (_undoTimer != null)
            {
                _undoTimer.Stop();
                _undoTimer = null;
            }
        }

        private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_isWindowClosingAllowed) return;

            if (_isDirty)
            {
                args.Cancel = true; // Pause closing until prompt decision
                bool shouldContinue = await PromptSaveIfDirty();
                if (shouldContinue)
                {
                    _isClosing = true;
                    _isWindowClosingAllowed = true;
                    if (_undoTimer != null)
                    {
                        _undoTimer.Stop();
                        _undoTimer = null;
                    }
                    this.Close();
                }
            }
            else
            {
                _isClosing = true;
                _isWindowClosingAllowed = true;
                if (_undoTimer != null)
                {
                    _undoTimer.Stop();
                    _undoTimer = null;
                }
            }
        }

        private void UpdateTitle()
        {
            if (_isClosing) return;
            try
            {
                string filename = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
                string prefix = _isDirty ? "*" : "";
                Title = $"{prefix}{filename} - Notelock";
                if (StatusBarFileNameLabel != null)
                {
                    StatusBarFileNameLabel.Text = $"{prefix}{filename}";
                }
            }
            catch { }
        }

        // --- File Menu Operations ---

        private async void OnNewClicked(object sender, RoutedEventArgs e)
        {
            if (await PromptSaveIfDirty())
            {
                _isLoadingFile = true;
                try
                {
                    MainEditor.Text = string.Empty;
                    _loadedContent = string.Empty;

                    _undoTimer?.Stop();
                    _history.Clear();
                    _history.Add((string.Empty, 0, 0));
                    _historyIndex = 0;

                    _currentFilePath = null;
                    _currentPassword = null;
                    _isDirty = false;
                    UpdateTitle();
                }
                finally
                {
                    _isLoadingFile = false;
                }
            }
        }

        private async void OnNewWindowClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Error", $"Could not open new window: {ex.Message}");
            }
        }

        private async void OnOpenClicked(object sender, RoutedEventArgs e)
        {
            if (!await PromptSaveIfDirty()) return;

            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".dhin");
                picker.FileTypeFilter.Add(".txt");
                picker.FileTypeFilter.Add(".ini");
                picker.FileTypeFilter.Add(".log");
                picker.FileTypeFilter.Add(".inf");
                picker.FileTypeFilter.Add(".cfg");
                picker.FileTypeFilter.Add("*");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    await OpenFile(file.Path);
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Error", $"Failed to pick file: {ex.Message}");
            }
        }

        public async Task OpenFile(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                string ext = Path.GetExtension(filePath);
                if (ext.Equals(".dhin", StringComparison.OrdinalIgnoreCase))
                {
                    string? password = await PromptPassword(isEncrypt: false);
                    if (password == null) return;

                    byte[] fileBytes = await File.ReadAllBytesAsync(filePath);

                    try
                    {
                        // Decrypt automatically handles V2 (AES-GCM) and Legacy V1 (AES-CBC) formats seamlessly
                        string content = EncryptionService.Decrypt(fileBytes, password);

                        _isLoadingFile = true;
                        try
                        {
                            _undoTimer?.Stop();
                            _history.Clear();
                            _history.Add((content, 0, 0));
                            _historyIndex = 0;

                            MainEditor.Text = content;
                            _loadedContent = MainEditor.Text ?? content;
                            _currentFilePath = filePath;
                            _currentPassword = password;
                            _isDirty = false;
                            UpdateTitle();
                            UpdateLineEndingsLabel();
                        }
                        finally
                        {
                            _isLoadingFile = false;
                        }
                    }
                    catch (Exception)
                    {
                        await ShowMessageDialogAsync("Error", "Decryption failed. Invalid password or corrupted file.");
                    }
                }
                else
                {
                    string content = await File.ReadAllTextAsync(filePath);

                    _isLoadingFile = true;
                    try
                    {
                        _undoTimer?.Stop();
                        _history.Clear();
                        _history.Add((content, 0, 0));
                        _historyIndex = 0;

                        MainEditor.Text = content;
                        _loadedContent = MainEditor.Text ?? content;
                        _currentFilePath = filePath;
                        _currentPassword = null;
                        _isDirty = false;
                        UpdateTitle();
                        UpdateLineEndingsLabel();
                    }
                    finally
                    {
                        _isLoadingFile = false;
                    }
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Error", $"Could not open file: {ex.Message}");
            }
        }

        private async void OnSaveClicked(object sender, RoutedEventArgs e)
        {
            await PerformSave();
        }

        private async Task<bool> PerformSave()
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                return await PerformSaveAs();
            }

            string ext = Path.GetExtension(_currentFilePath);
            if (ext.Equals(".dhin", StringComparison.OrdinalIgnoreCase))
            {
                return await SaveEncryptedFile(_currentFilePath);
            }
            else
            {
                return await SavePlainTextFile(_currentFilePath);
            }
        }

        private async void OnSaveAsClicked(object sender, RoutedEventArgs e)
        {
            await PerformSaveAs();
        }

        private async Task<bool> PerformSaveAs()
        {
            try
            {
                var loc = LocalizationService.Instance;
                // High-standard UX Security Choice Dialog explaining file protection in plain terms
                var securityDialog = new ContentDialog
                {
                    Title = loc.GetString("SaveDocumentSecurityTitle"),
                    Content = loc.GetString("SaveDocumentSecurityContent"),
                    PrimaryButtonText = loc.GetString("EncryptedDhinChoice"),
                    SecondaryButtonText = loc.GetString("PlainTextTxtChoice"),
                    CloseButtonText = loc.GetString("Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                };

                var dialogResult = await securityDialog.ShowAsync();

                if (dialogResult == ContentDialogResult.Primary)
                {
                    // Encrypted (.dhin) Option Selected: Pick file location FIRST for best UX
                    var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(savePicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                    savePicker.FileTypeChoices.Add("Notelock Encrypted Document (*.dhin)", new List<string>() { ".dhin" });

                    string baseName = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileNameWithoutExtension(_currentFilePath);
                    savePicker.SuggestedFileName = $"{baseName}.dhin";

                    var file = await savePicker.PickSaveFileAsync();
                    if (file != null)
                    {
                        string? password = await PromptPassword(isEncrypt: true);
                        if (string.IsNullOrEmpty(password)) return false;

                        _currentPassword = password;
                        bool saved = await SaveEncryptedFile(file.Path);
                        if (saved)
                        {
                            await ShowMessageDialogAsync("Success", "File saved and encrypted successfully.");
                        }
                        return saved;
                    }
                    return false;
                }
                else if (dialogResult == ContentDialogResult.Secondary)
                {
                    // Plain Text (.txt) Option Selected
                    var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(savePicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                    savePicker.FileTypeChoices.Add("Text Document (*.txt)", new List<string>() { ".txt" });

                    string baseName = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileNameWithoutExtension(_currentFilePath);
                    savePicker.SuggestedFileName = $"{baseName}.txt";

                    var file = await savePicker.PickSaveFileAsync();
                    if (file != null)
                    {
                        return await SavePlainTextFile(file.Path);
                    }
                    return false;
                }
                else
                {
                    // User Cancelled
                    return false;
                }
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Error", $"An unexpected error occurred while saving: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SaveEncryptedFile(string filePath)
        {
            try
            {
                string? password = _currentPassword;
                if (string.IsNullOrEmpty(password))
                {
                    password = await PromptPassword(isEncrypt: true);
                    if (string.IsNullOrEmpty(password)) return false;
                }

                string text = MainEditor.Text ?? string.Empty;
                byte[] encrypted = EncryptionService.Encrypt(text, password);

                await File.WriteAllBytesAsync(filePath, encrypted);
                _currentFilePath = filePath;
                _currentPassword = password;
                _loadedContent = text;
                _isDirty = false;
                UpdateTitle();

                return true;
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Error", $"Failed to save encrypted file: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SavePlainTextFile(string filePath)
        {
            try
            {
                string text = MainEditor.Text ?? string.Empty;
                await File.WriteAllTextAsync(filePath, text);

                _currentFilePath = filePath;
                _currentPassword = null;
                _loadedContent = text;
                _isDirty = false;
                UpdateTitle();

                return true;
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Error", $"Failed to save text file: {ex.Message}");
                return false;
            }
        }

        private async void OnExitClicked(object sender, RoutedEventArgs e)
        {
            if (await PromptSaveIfDirty())
            {
                _isWindowClosingAllowed = true;
                this.Close();
            }
        }

        private async Task<bool> PromptSaveIfDirty()
        {
            if (!_isDirty) return true;

            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("SaveChangesTitle"),
                Content = loc.GetString("SaveChangesPrompt"),
                PrimaryButtonText = loc.GetString("Save"),
                SecondaryButtonText = loc.GetString("DontSave"),
                CloseButtonText = loc.GetString("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = (this.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return await PerformSave();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                return true; // Proceed without saving
            }
            else
            {
                return false; // Cancel operation
            }
        }

        private async Task<string?> PromptPassword(bool isEncrypt)
        {
            if (this.Content == null) return null;

            int attempts = 0;
            while (this.Content.XamlRoot == null && attempts < 25)
            {
                await Task.Delay(100);
                attempts++;
            }

            if (this.Content.XamlRoot == null) return null;

            var loc = LocalizationService.Instance;

            var stack = new StackPanel { Spacing = 14, Width = 360, Margin = new Thickness(0, 8, 0, 0) };

            // Subtitle Description
            var subtitle = new TextBlock
            {
                Text = isEncrypt ? loc.GetString("EncryptSubtitle") : loc.GetString("DecryptSubtitle"),
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(subtitle);

            // Password Input Field
            var passLabel = new TextBlock
            {
                Text = loc.GetString("PasswordLabel"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };
            var passwordBox = new PasswordBox
            {
                PlaceholderText = loc.GetString("EnterPassword"),
                Height = 38
            };
            var passStack = new StackPanel { Spacing = 6 };
            passStack.Children.Add(passLabel);
            passStack.Children.Add(passwordBox);
            stack.Children.Add(passStack);

            // Confirm Password Field (Only for Encrypt)
            PasswordBox? confirmPasswordBox = null;
            if (isEncrypt)
            {
                var confirmLabel = new TextBlock
                {
                    Text = loc.GetString("ConfirmPasswordLabel"),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                };
                confirmPasswordBox = new PasswordBox
                {
                    PlaceholderText = loc.GetString("RepeatPassword"),
                    Height = 38
                };
                var confirmStack = new StackPanel { Spacing = 6 };
                confirmStack.Children.Add(confirmLabel);
                confirmStack.Children.Add(confirmPasswordBox);
                stack.Children.Add(confirmStack);
            }

            // Error Label
            var errorLabel = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                FontSize = 13,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(errorLabel);

            var dialog = new ContentDialog
            {
                Title = isEncrypt ? loc.GetString("EncryptDoc") : loc.GetString("DecryptDoc"),
                Content = stack,
                PrimaryButtonText = isEncrypt ? loc.GetString("EncryptSave") : loc.GetString("DecryptOpen"),
                CloseButtonText = loc.GetString("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = (this.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
            };

            if (loc.IsRightToLeft)
            {
                dialog.FlowDirection = FlowDirection.RightToLeft;
            }

            string? resultPassword = null;
            dialog.PrimaryButtonClick += (sender, args) =>
            {
                errorLabel.Visibility = Visibility.Collapsed;
                string pass = passwordBox.Password;
                if (string.IsNullOrWhiteSpace(pass))
                {
                    errorLabel.Text = loc.GetString("PasswordEmptyError");
                    errorLabel.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                if (isEncrypt && confirmPasswordBox != null)
                {
                    string confirm = confirmPasswordBox.Password;
                    if (pass != confirm)
                    {
                        errorLabel.Text = loc.GetString("PasswordMismatchError");
                        errorLabel.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }
                }

                resultPassword = pass;
            };

            await dialog.ShowAsync();
            return resultPassword;
        }

        // --- Edit History & Clipboard Handlers ---

        private void CaptureHistoryState(string text, int selStart, int selLen)
        {
            if (_historyIndex >= 0 && _historyIndex < _history.Count && text == _history[_historyIndex].Text) return;

            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
            }

            _history.Add((text, selStart, selLen));
            _historyIndex = _history.Count - 1;

            if (_history.Count > 100)
            {
                _history.RemoveAt(0);
                _historyIndex--;
            }
        }

        private void OnUndoClicked(object sender, RoutedEventArgs e)
        {
            if (_undoTimer != null && _undoTimer.IsEnabled)
            {
                _undoTimer.Stop();
                CaptureHistoryState(MainEditor.Text ?? string.Empty, MainEditor.SelectionStart, MainEditor.SelectionLength);
            }

            if (_historyIndex > 0)
            {
                _historyIndex--;
                _isUndoRedoAction = true;
                var state = _history[_historyIndex];
                MainEditor.Text = state.Text;
                int start = Math.Clamp(state.SelectionStart, 0, state.Text.Length);
                int length = Math.Clamp(state.SelectionLength, 0, state.Text.Length - start);
                MainEditor.Select(start, length);
                _isUndoRedoAction = false;
            }
            MainEditor?.Focus(FocusState.Programmatic);
        }

        private void OnRedoClicked(object sender, RoutedEventArgs e)
        {
            if (_historyIndex < _history.Count - 1)
            {
                _historyIndex++;
                _isUndoRedoAction = true;
                var state = _history[_historyIndex];
                MainEditor.Text = state.Text;
                int start = Math.Clamp(state.SelectionStart, 0, state.Text.Length);
                int length = Math.Clamp(state.SelectionLength, 0, state.Text.Length - start);
                MainEditor.Select(start, length);
                _isUndoRedoAction = false;
            }
            MainEditor?.Focus(FocusState.Programmatic);
        }

        private void OnCutClicked(object sender, RoutedEventArgs e)
        {
            if (MainEditor == null) return;
            CaptureHistoryState(MainEditor.Text ?? string.Empty, MainEditor.SelectionStart, MainEditor.SelectionLength);
            if (MainEditor.SelectionLength > 0)
            {
                string selectedText = MainEditor.SelectedText;
                var package = new DataPackage();
                package.SetText(selectedText);
                Clipboard.SetContent(package);

                MainEditor.SelectedText = string.Empty;
            }
            MainEditor?.Focus(FocusState.Programmatic);
        }

        private void OnCopyClicked(object sender, RoutedEventArgs e)
        {
            if (MainEditor == null) return;
            if (MainEditor.SelectionLength > 0)
            {
                string selectedText = MainEditor.SelectedText;
                var package = new DataPackage();
                package.SetText(selectedText);
                Clipboard.SetContent(package);
            }
            MainEditor?.Focus(FocusState.Programmatic);
        }

        private async void OnPasteClicked(object sender, RoutedEventArgs e)
        {
            if (MainEditor == null) return;
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                {
                    CaptureHistoryState(MainEditor.Text ?? string.Empty, MainEditor.SelectionStart, MainEditor.SelectionLength);
                    string clipboardText = await content.GetTextAsync();
                    int start = MainEditor.SelectionStart;
                    MainEditor.SelectedText = clipboardText;
                    MainEditor.SelectionStart = start + clipboardText.Length;
                    MainEditor.SelectionLength = 0;
                }
            }
            catch { }
            MainEditor?.Focus(FocusState.Programmatic);
        }

        private void OnDeleteClicked(object sender, RoutedEventArgs e)
        {
            if (MainEditor == null) return;
            CaptureHistoryState(MainEditor.Text ?? string.Empty, MainEditor.SelectionStart, MainEditor.SelectionLength);
            if (MainEditor.SelectionLength > 0)
            {
                MainEditor.SelectedText = string.Empty;
            }
            else if (MainEditor.SelectionStart < (MainEditor.Text?.Length ?? 0))
            {
                int start = MainEditor.SelectionStart;
                MainEditor.Select(start, 1);
                MainEditor.SelectedText = string.Empty;
            }
            MainEditor?.Focus(FocusState.Programmatic);
        }

        private void OnSelectAllClicked(object sender, RoutedEventArgs e)
        {
            if (MainEditor == null) return;
            MainEditor.Focus(FocusState.Programmatic);
            MainEditor.SelectAll();
        }

        private void OnTimeDateClicked(object sender, RoutedEventArgs e)
        {
            if (MainEditor == null) return;
            CaptureHistoryState(MainEditor.Text ?? string.Empty, MainEditor.SelectionStart, MainEditor.SelectionLength);
            string timeDateStr = DateTime.Now.ToString("h:mm tt M/d/yyyy");
            MainEditor.SelectedText = timeDateStr;
            MainEditor?.Focus(FocusState.Programmatic);
        }

        // --- Find / Replace Handlers ---

        private void OnFindClicked(object sender, RoutedEventArgs e)
        {
            FindReplacePanel.Visibility = Visibility.Visible;
            ReplaceRow.Visibility = Visibility.Collapsed;
            FindEntry.Focus(FocusState.Programmatic);
        }

        private void OnFindNextMenuClicked(object sender, RoutedEventArgs e)
        {
            if (FindReplacePanel.Visibility != Visibility.Visible)
            {
                FindReplacePanel.Visibility = Visibility.Visible;
                ReplaceRow.Visibility = Visibility.Collapsed;
                FindEntry.Focus(FocusState.Programmatic);
            }
            else
            {
                FindText(forward: true);
            }
        }

        private void OnFindPrevMenuClicked(object sender, RoutedEventArgs e)
        {
            if (FindReplacePanel.Visibility != Visibility.Visible)
            {
                FindReplacePanel.Visibility = Visibility.Visible;
                ReplaceRow.Visibility = Visibility.Collapsed;
                FindEntry.Focus(FocusState.Programmatic);
            }
            else
            {
                FindText(forward: false);
            }
        }

        private void OnReplaceMenuClicked(object sender, RoutedEventArgs e)
        {
            FindReplacePanel.Visibility = Visibility.Visible;
            ReplaceRow.Visibility = Visibility.Visible;
            FindEntry.Focus(FocusState.Programmatic);
        }

        private void OnCloseFindReplaceClicked(object sender, RoutedEventArgs e)
        {
            FindReplacePanel.Visibility = Visibility.Collapsed;
            MainEditor.Focus(FocusState.Programmatic);
        }

        private void OnFindTextChanged(object sender, TextChangedEventArgs e)
        {
        }

        private void OnFindPrevClicked(object sender, RoutedEventArgs e)
        {
            FindText(forward: false);
        }

        private void OnFindNextClicked(object sender, RoutedEventArgs e)
        {
            FindText(forward: true);
        }

        private async void FindText(bool forward)
        {
            string searchPattern = FindEntry.Text;
            if (string.IsNullOrEmpty(searchPattern)) return;

            string text = MainEditor.Text ?? string.Empty;
            var loc = LocalizationService.Instance;
            if (text.Length == 0)
            {
                await ShowMessageDialogAsync(loc.GetString("Find"), loc.GetString("CannotFind", searchPattern));
                return;
            }

            int cursor = Math.Clamp(MainEditor.SelectionStart, 0, text.Length);
            int selectionLen = MainEditor.SelectionLength;

            StringComparison comparison = (MatchCaseCheckBox.IsChecked == true)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            int index = -1;
            if (forward)
            {
                int searchStart = cursor + selectionLen;
                if (searchStart > text.Length) searchStart = text.Length;

                index = text.IndexOf(searchPattern, searchStart, comparison);
                if (index == -1 && searchStart > 0)
                {
                    index = text.IndexOf(searchPattern, 0, searchStart, comparison);
                }
            }
            else
            {
                int searchStart = cursor - 1;
                if (searchStart >= 0 && searchStart < text.Length)
                {
                    index = text.LastIndexOf(searchPattern, searchStart, comparison);
                }
                if (index == -1 && text.Length > 0)
                {
                    index = text.LastIndexOf(searchPattern, text.Length - 1, comparison);
                }
            }

            if (index != -1)
            {
                MainEditor.Focus(FocusState.Programmatic);
                MainEditor.Select(index, searchPattern.Length);
            }
            else
            {
                await ShowMessageDialogAsync(loc.GetString("Find"), loc.GetString("CannotFind", searchPattern));
            }
        }

        private void OnReplaceClicked(object sender, RoutedEventArgs e)
        {
            string findText = FindEntry.Text;
            if (string.IsNullOrEmpty(findText)) return;

            int start = MainEditor.SelectionStart;
            int len = MainEditor.SelectionLength;
            string text = MainEditor.Text ?? string.Empty;

            StringComparison comparison = (MatchCaseCheckBox.IsChecked == true)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (len == findText.Length && start + len <= text.Length && text.Substring(start, len).Equals(findText, comparison))
            {
                CaptureHistoryState(text, MainEditor.SelectionStart, MainEditor.SelectionLength);
                string replaceText = ReplaceEntry.Text ?? string.Empty;
                MainEditor.Text = text.Remove(start, len).Insert(start, replaceText);
                MainEditor.Select(start, replaceText.Length);
            }

            FindText(forward: true);
        }

        private async void OnReplaceAllClicked(object sender, RoutedEventArgs e)
        {
            string findText = FindEntry.Text;
            if (string.IsNullOrEmpty(findText)) return;

            string replaceText = ReplaceEntry.Text ?? string.Empty;
            string text = MainEditor.Text ?? string.Empty;

            StringComparison comparison = (MatchCaseCheckBox.IsChecked == true)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            CaptureHistoryState(text, MainEditor.SelectionStart, MainEditor.SelectionLength);
            MainEditor.Text = text.Replace(findText, replaceText, comparison);

            var loc = LocalizationService.Instance;
            MainEditor.SelectionStart = 0;
            MainEditor.SelectionLength = 0;
            await ShowMessageDialogAsync(loc.GetString("Replace"), loc.GetString("ReplaceAllDone"));
        }

        // --- Format / View / Zoom Handlers ---

        private void OnWordWrapClicked(object sender, RoutedEventArgs e)
        {
            _isWordWrapEnabled = !_isWordWrapEnabled;
            UpdateWordWrap();
        }

        private void UpdateWordWrap()
        {
            WordWrapMenuItem.Text = _isWordWrapEnabled ? "✓ Word Wrap" : "Word Wrap";
            MainEditor.TextWrapping = _isWordWrapEnabled ? Microsoft.UI.Xaml.TextWrapping.Wrap : Microsoft.UI.Xaml.TextWrapping.NoWrap;
            ScrollViewer.SetHorizontalScrollBarVisibility(MainEditor, _isWordWrapEnabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(MainEditor, ScrollBarVisibility.Auto);
        }

        private void OnZoomInClicked(object sender, RoutedEventArgs e)
        {
            _zoomPercentage = Math.Min(500, _zoomPercentage + 10);
            UpdateZoom();
        }

        private void OnZoomOutClicked(object sender, RoutedEventArgs e)
        {
            _zoomPercentage = Math.Max(10, _zoomPercentage - 10);
            UpdateZoom();
        }

        private void OnZoomResetClicked(object sender, RoutedEventArgs e)
        {
            _zoomPercentage = 100.0;
            UpdateZoom();
        }

        private void UpdateZoom()
        {
            ZoomLabel.Text = $"{_zoomPercentage}%";
            MainEditor.FontSize = _baseFontSize * (_zoomPercentage / 100.0);
        }

        private void OnStatusBarToggleClicked(object sender, RoutedEventArgs e)
        {
            _isStatusBarVisible = !_isStatusBarVisible;
            StatusBarMenuItem.Text = _isStatusBarVisible ? "✓ Status Bar" : "Status Bar";
            StatusBar.Visibility = _isStatusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnGoToClicked(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var inputTextBox = new TextBox
            {
                Text = "1",
                PlaceholderText = loc.GetString("LineNumberPlaceholder"),
                Margin = new Thickness(0, 10, 0, 0)
            };

            var dialog = new ContentDialog
            {
                Title = loc.GetString("GoToLineTitle"),
                Content = inputTextBox,
                PrimaryButtonText = loc.GetString("GoTo"),
                CloseButtonText = loc.GetString("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = (this.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string input = inputTextBox.Text;
                if (string.IsNullOrEmpty(input) || !int.TryParse(input, out int lineToGo) || lineToGo < 1) return;

                string text = MainEditor.Text ?? string.Empty;
                int currentLine = 1;
                int charIndex = 0;

                if (lineToGo > 1)
                {
                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        if (c == '\r')
                        {
                            currentLine++;
                            if (currentLine == lineToGo)
                            {
                                charIndex = i + 1;
                                if (i + 1 < text.Length && text[i + 1] == '\n')
                                {
                                    charIndex = i + 2;
                                }
                                break;
                            }
                            if (i + 1 < text.Length && text[i + 1] == '\n')
                            {
                                i++;
                            }
                        }
                        else if (c == '\n')
                        {
                            currentLine++;
                            if (currentLine == lineToGo)
                            {
                                charIndex = i + 1;
                                break;
                            }
                        }
                    }

                    if (currentLine < lineToGo)
                    {
                        await ShowMessageDialogAsync(loc.GetString("GoToLineTitle"), loc.GetString("LineBeyondTotal"));
                        return;
                    }
                }

                MainEditor.Focus(FocusState.Programmatic);
                MainEditor.Select(charIndex, 0);
            }
        }

        private async void OnAboutClicked(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = loc.GetString("AboutNotelockTitle"),
                Content = loc.GetString("AboutContent"),
                PrimaryButtonText = loc.GetString("VisitWebsite"),
                CloseButtonText = loc.GetString("OK"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                XamlRoot = this.Content.XamlRoot,
                RequestedTheme = (this.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                try
                {
                    await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.dhinovatech.com"));
                }
                catch
                {
                    // Ignore launcher exceptions
                }
            }
        }

        // --- Status Bar & Dialog Helpers ---

        private void UpdateCursorPosLabel()
        {
            if (_isClosing || MainEditor == null || CursorPosLabel == null) return;
            try
            {
                string text = MainEditor.Text ?? string.Empty;
                int cursor = Math.Clamp(MainEditor.SelectionStart, 0, text.Length);

                ReadOnlySpan<char> span = text.AsSpan(0, cursor);
                int line = 1;
                int lastNewLinePos = -1;

                for (int i = 0; i < span.Length; i++)
                {
                    char c = span[i];
                    if (c == '\n')
                    {
                        line++;
                        lastNewLinePos = i;
                    }
                    else if (c == '\r')
                    {
                        if (i + 1 < span.Length && span[i + 1] == '\n')
                        {
                            // Skip \r in \r\n pair, \n will increment line
                            continue;
                        }
                        line++;
                        lastNewLinePos = i;
                    }
                }

                int col = cursor - (lastNewLinePos + 1) + 1;

                var loc = LocalizationService.Instance;
                CursorPosLabel.Text = loc.GetString("LnCol", line, Math.Max(1, col));
            }
            catch { }
        }

        private static string NormalizeLineEndings(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private void UpdateLineEndingsLabel()
        {
            if (_isClosing || MainEditor == null || LineEndingsLabel == null) return;
            try
            {
                string text = MainEditor.Text ?? string.Empty;
                if (text.Contains("\r\n"))
                {
                    LineEndingsLabel.Text = "Windows (CRLF)";
                }
                else if (text.Contains("\n"))
                {
                    LineEndingsLabel.Text = "Unix (LF)";
                }
                else
                {
                    LineEndingsLabel.Text = "Windows (CRLF)";
                }
            }
            catch { }
        }

        private void ApplyLocalization()
        {
            var loc = LocalizationService.Instance;
            if (FileMenu != null) FileMenu.Title = loc.GetString("File");
            if (NewMenuItem != null) NewMenuItem.Text = loc.GetString("New");
            if (NewWindowMenuItem != null) NewWindowMenuItem.Text = loc.GetString("NewWindow");
            if (OpenMenuItem != null) OpenMenuItem.Text = loc.GetString("Open");
            if (SaveMenuItem != null) SaveMenuItem.Text = loc.GetString("Save");
            if (SaveAsMenuItem != null) SaveAsMenuItem.Text = loc.GetString("SaveAs");
            if (ExitMenuItem != null) ExitMenuItem.Text = loc.GetString("Exit");

            if (EditMenu != null) EditMenu.Title = loc.GetString("Edit");
            if (UndoMenuItem != null) UndoMenuItem.Text = loc.GetString("Undo");
            if (RedoMenuItem != null) RedoMenuItem.Text = loc.GetString("Redo");
            if (CutMenuItem != null) CutMenuItem.Text = loc.GetString("Cut");
            if (CopyMenuItem != null) CopyMenuItem.Text = loc.GetString("Copy");
            if (PasteMenuItem != null) PasteMenuItem.Text = loc.GetString("Paste");
            if (DeleteMenuItem != null) DeleteMenuItem.Text = loc.GetString("Delete");
            if (FindMenuItem != null) FindMenuItem.Text = loc.GetString("Find");
            if (FindNextMenuItem != null) FindNextMenuItem.Text = loc.GetString("FindNext");
            if (FindPrevMenuItem != null) FindPrevMenuItem.Text = loc.GetString("FindPrev");
            if (ReplaceMenuItem != null) ReplaceMenuItem.Text = loc.GetString("Replace");
            if (GoToMenuItem != null) GoToMenuItem.Text = loc.GetString("GoTo");
            if (SelectAllMenuItem != null) SelectAllMenuItem.Text = loc.GetString("SelectAll");
            if (TimeDateMenuItem != null) TimeDateMenuItem.Text = loc.GetString("TimeDate");

            if (FormatMenu != null) FormatMenu.Title = loc.GetString("Format");
            if (WordWrapMenuItem != null) WordWrapMenuItem.Text = _isWordWrapEnabled ? $"✓ {loc.GetString("WordWrap")}" : loc.GetString("WordWrap");

            if (ViewMenu != null) ViewMenu.Title = loc.GetString("View");
            if (ZoomSubMenu != null) ZoomSubMenu.Text = loc.GetString("Zoom");
            if (ZoomInMenuItem != null) ZoomInMenuItem.Text = loc.GetString("ZoomIn");
            if (ZoomOutMenuItem != null) ZoomOutMenuItem.Text = loc.GetString("ZoomOut");
            if (RestoreZoomMenuItem != null) RestoreZoomMenuItem.Text = loc.GetString("RestoreDefaultZoom");
            if (StatusBarMenuItem != null) StatusBarMenuItem.Text = _isStatusBarVisible ? $"✓ {loc.GetString("StatusBar")}" : loc.GetString("StatusBar");

            if (LanguageMenu != null) LanguageMenu.Title = loc.GetString("Language");
            if (HelpMenu != null) HelpMenu.Title = loc.GetString("Help");
            if (AboutMenuItem != null) AboutMenuItem.Text = loc.GetString("About");

            if (MainEditor != null) MainEditor.PlaceholderText = loc.GetString("StartTyping");
            if (FindEntry != null) FindEntry.PlaceholderText = loc.GetString("FindPlaceholder");
            if (ReplaceEntry != null) ReplaceEntry.PlaceholderText = loc.GetString("ReplacePlaceholder");
            if (MatchCaseCheckBox != null) MatchCaseCheckBox.Content = loc.GetString("MatchCase");

            if (MainEditor != null)
            {
                MainEditor.FlowDirection = loc.IsRightToLeft ? Microsoft.UI.Xaml.FlowDirection.RightToLeft : Microsoft.UI.Xaml.FlowDirection.LeftToRight;
            }

            PopulateLanguageMenu();
            UpdateTitle();
            UpdateCursorPosLabel();
        }

        private void PopulateLanguageMenu()
        {
            if (LanguageMenu == null) return;
            string currentCode = LocalizationService.Instance.SelectedLanguageCode;

            foreach (var item in LanguageMenu.Items)
            {
                if (item is MenuFlyoutItem flyoutItem && flyoutItem.Tag is string tagCode)
                {
                    bool isSelected = tagCode.Equals(currentCode, StringComparison.OrdinalIgnoreCase);
                    string check = isSelected ? "✓ " : "    ";
                    var langInfo = LocalizationService.SupportedLanguages.FirstOrDefault(l => l.Code.Equals(tagCode, StringComparison.OrdinalIgnoreCase));
                    if (langInfo != null)
                    {
                        flyoutItem.Text = (langInfo.Code == LocalizationService.AUTO_CODE)
                            ? $"{check}{LocalizationService.Instance.GetString("AutoLanguage")}"
                            : $"{check}{langInfo.NativeName} ({langInfo.Name})";
                    }
                }
            }
        }

        private void OnLanguageItemClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string code)
            {
                LocalizationService.Instance.SetLanguage(code);
            }
        }

        private async Task ShowMessageDialogAsync(string title, string content)
        {
            if (_isClosing || this.Content == null || this.Content.XamlRoot == null) return;
            try
            {
                var loc = LocalizationService.Instance;
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = content,
                    CloseButtonText = loc.GetString("OK"),
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = (this.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
                };

                await dialog.ShowAsync();
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string AppID);

        private void SetTaskbarIcon(string iconPath)
        {
            if (!File.Exists(iconPath)) return;

            try
            {
                // 1. Set AppWindow Icon (TitleBar & Taskbar)
                this.AppWindow.SetIcon(iconPath);

                // 2. Set AppUserModelID so Windows Taskbar groups & pins icon properly
                SetCurrentProcessExplicitAppUserModelID("Dhinovatech.Notelockv3");
            }
            catch { }
        }
    }
}
