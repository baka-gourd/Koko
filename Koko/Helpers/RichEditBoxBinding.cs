using System;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Koko.Helpers;

public static class RichEditBoxBinding
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(RichEditBoxBinding),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public static string GetText(DependencyObject obj)
        => (string)obj.GetValue(TextProperty);

    public static void SetText(DependencyObject obj, string value)
        => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichEditBox reb)
            return;

        var newText = e.NewValue as string ?? string.Empty;

        var dq = reb.DispatcherQueue;
        if (dq is null)
            return;

        if (dq.HasThreadAccess)
        {
            SetDocumentText(reb, newText);
        }
        else
        {
            dq.TryEnqueue(() => SetDocumentText(reb, newText));
        }
    }

    private static void SetDocumentText(RichEditBox reb, string text)
    {
        reb.Document.GetText(TextGetOptions.None, out var current);
        if (string.Equals(current, text, StringComparison.Ordinal))
            return;

        reb.Document.SetText(TextSetOptions.None, text);
    }
}