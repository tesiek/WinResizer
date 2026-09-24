using System.Windows;
using System.Windows.Input;

namespace WinResizer.Views;

public partial class ProfileNameDialog : Window
{
    public ProfileNameDialog(string title, string prompt, string currentName = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ProfileNameTextBox.Text = currentName;
        Loaded += OnLoaded;
    }

    public string ProfileName => ProfileNameTextBox.Text;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ProfileNameTextBox.Focus();
        ProfileNameTextBox.SelectAll();
        Keyboard.Focus(ProfileNameTextBox);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
