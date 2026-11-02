using System.Windows;

namespace Nvm03;

public partial class TagEditorWindow : Window
{
    public TagEditorWindow(string version, string tags)
    {
        InitializeComponent();
        DataContext = new TagEditorModel(version, tags);
        TagsInput.Focus();
        TagsInput.SelectAll();
    }

    public string Tags => TagsInput.Text;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private sealed record TagEditorModel(string Version, string Tags);
}
