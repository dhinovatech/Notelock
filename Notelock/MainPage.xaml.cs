using CommunityToolkit.Maui.Storage;
using Notelock.Services;

namespace Notelock;

public partial class MainPage : ContentPage
{
    private string? _currentFilePath;
    private bool _isDirty;
    private bool _isFirstLoad = true;

    public MainPage()
    {
        InitializeComponent();
        MainEditor.TextChanged += (s, e) => _isDirty = true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_isFirstLoad)
        {
            _isFirstLoad = false;
            await CheckCommandLineArgs();
        }
    }

    private async Task CheckCommandLineArgs()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            string fileToOpen = args[1];
            if (File.Exists(fileToOpen) && Path.GetExtension(fileToOpen).Equals(".dhin", StringComparison.OrdinalIgnoreCase))
            {
                await OpenFile(fileToOpen);
            }
        }
    }

    private async void OnNewClicked(object sender, EventArgs e)
    {
        if (await PromptSaveIfDirty())
        {
            MainEditor.Text = string.Empty;
            _currentFilePath = null;
            _isDirty = false;
            Title = "Notelock - Untitled";
        }
    }

    private async void OnOpenClicked(object sender, EventArgs e)
    {
        if (!await PromptSaveIfDirty()) return;

        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Open Encrypted File",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".dhin" } },
                    { DevicePlatform.Android, new[] { "application/octet-stream" } },
                })
            });

            if (result != null)
            {
                await OpenFile(result.FullPath);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to pick file: {ex.Message}", "OK");
        }
    }

    public async Task OpenFile(string filePath)
    {
         if (!File.Exists(filePath)) return;

        try
        {
            string? password = await PromptPassword(isEncrypt: false);
            if (password == null) return; 

            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            
            try
            {
                string content = EncryptionService.Decrypt(fileBytes, password);
                MainEditor.Text = content;
                _currentFilePath = filePath;
                _isDirty = false;
                Title = $"Notelock - {Path.GetFileName(filePath)}";
            }
            catch (Exception)
            {
                await DisplayAlert("Error", "Decryption failed. Invalid password or corrupted file.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Could not open file: {ex.Message}", "OK");
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        await PerformSave();
    }

    private async Task PerformSave()
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            await PerformSaveAs();
        }
        else
        {
            // We have a path, so we can overwrite directly.
            await SaveFile(_currentFilePath);
        }
    }

    private async void OnSaveAsClicked(object sender, EventArgs e)
    {
        await PerformSaveAs();
    }

    private async Task PerformSaveAs()
    {
        try 
        {
            // 1. Prompt for password first (so we don't open picker then cancel)
            string? password = await PromptPassword(isEncrypt: true);
            if (password == null) return;

            string text = MainEditor.Text;
            byte[] encrypted = EncryptionService.Encrypt(text, password);

            using var stream = new MemoryStream(encrypted);

            string initialName = !string.IsNullOrEmpty(_currentFilePath) ? Path.GetFileName(_currentFilePath) : "Untitled.dhin";

            // 2. Use FileSaver to save
            var fileSaverResult = await FileSaver.Default.SaveAsync(initialName, stream, CancellationToken.None);

            if (fileSaverResult.IsSuccessful)
            {
                 // On Windows/Android, FilePath *might* be available in the result.
                 // If it is, update _currentFilePath.
                 // Note: FileSaver on some platforms might not return the full path if it's using a Stream approach strictly,
                 // but checking documentation/behavior:
                 // The result usually contains FilePath property.
                 
                 _currentFilePath = fileSaverResult.FilePath;
                 _isDirty = false;
                 // If path is null (some implementations), we might just leave title as is or use the name we gave.
                 if (!string.IsNullOrEmpty(_currentFilePath))
                    Title = $"Notelock - {Path.GetFileName(_currentFilePath)}";
                 else 
                    Title = $"Notelock - Saved"; // Fallback

                 await DisplayAlert("Success", "File saved successfully.", "OK");
            }
            else 
            {
                if (fileSaverResult.Exception != null)
                     await DisplayAlert("Error", $"Save failed: {fileSaverResult.Exception.Message}", "OK");
                // Else user cancelled.
            }
        }
        catch(Exception ex)
        {
             await DisplayAlert("Error", $"An unexpected error occurred: {ex.Message}", "OK");
        }
    }

    private async Task SaveFile(string filePath)
    {
        try
        {
            // Direct overwrite
            // Prompt for password
            string? password = await PromptPassword(isEncrypt: true);
            if (password == null) return;

            string text = MainEditor.Text;
            byte[] encrypted = EncryptionService.Encrypt(text, password);
            
            await File.WriteAllBytesAsync(filePath, encrypted);
            _currentFilePath = filePath;
            _isDirty = false;
            Title = $"Notelock - {Path.GetFileName(filePath)}"; 
            
            await DisplayAlert("Success", $"Saved to {filePath}", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Failed to save: {ex.Message}", "OK");
        }
    }

    private async void OnExitClicked(object sender, EventArgs e)
    {
        if (await PromptSaveIfDirty())
        {
            Application.Current.Quit();
        }
    }

    private async Task<bool> PromptSaveIfDirty()
    {
        if (_isDirty)
        {
            string result = await DisplayActionSheet("Save changes?", "Cancel", null, "Yes", "No");
            if (result == "Cancel") return false;
            if (result == "Yes")
            {
                await PerformSave();
                // We assume user completed save or cancelled save. 
            }
            return true;
        }
        return true;
    }

    private Task<string?> PromptPassword(bool isEncrypt)
    {
        var tcs = new TaskCompletionSource<string?>();
        var page = new PasswordPage(tcs, isEncrypt);
        Navigation.PushModalAsync(page);
        return tcs.Task;
    }
}
