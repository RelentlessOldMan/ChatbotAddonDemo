using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Demo.Desktop.ViewModels;

namespace Demo.Desktop;

public partial class ChatPanel : UserControl
{
    public ChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ChatViewModel old) old.Items.CollectionChanged -= ScrollToEnd;
            if (e.NewValue is ChatViewModel vm) vm.Items.CollectionChanged += ScrollToEnd;
        };
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(Scroller.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
}
