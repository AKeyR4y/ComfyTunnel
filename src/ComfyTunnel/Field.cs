using System.Windows;
using System.Windows.Controls;

namespace ComfyTunnel.Desktop;

// Presentation metadata only; credentials are never copied into these properties.
public static class Field
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Field), new PropertyMetadata(""));
    public static readonly DependencyProperty ErrorProperty = DependencyProperty.RegisterAttached("Error", typeof(string), typeof(Field), new PropertyMetadata("", ErrorChanged));
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached("HasError", typeof(bool), typeof(Field), new PropertyMetadata(false));
    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached("IsEmpty", typeof(bool), typeof(Field), new PropertyMetadata(true));
    static Field()
    {
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.TextChangedEvent, new TextChangedEventHandler((sender, _) => SetError((DependencyObject)sender, "")));
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent, new RoutedEventHandler((sender, _) =>
        {
            var box = (PasswordBox)sender;
            using var secret = box.SecurePassword;
            box.SetValue(IsEmptyProperty, secret.Length == 0);
            SetError(box, "");
        }));
    }
    private static void ErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => d.SetValue(HasErrorProperty, !string.IsNullOrEmpty((string)e.NewValue));
    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);
    public static string GetError(DependencyObject d) => (string)d.GetValue(ErrorProperty);
    public static void SetError(DependencyObject d, string value) => d.SetValue(ErrorProperty, value);
    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);
    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject d, bool value) => d.SetValue(IsEmptyProperty, value);
}
