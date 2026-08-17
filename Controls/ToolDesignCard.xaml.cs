using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhotoTools2.Controls;

public sealed partial class ToolDesignCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToolDesignCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(ToolDesignCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty PrimaryActionProperty = DependencyProperty.Register(nameof(PrimaryAction), typeof(string), typeof(ToolDesignCard), new PropertyMetadata("Run tool"));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string PrimaryAction { get => (string)GetValue(PrimaryActionProperty); set => SetValue(PrimaryActionProperty, value); }

    public ToolDesignCard() => InitializeComponent();
}
