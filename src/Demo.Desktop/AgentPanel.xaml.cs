using System.Collections.Specialized;
using System.Windows.Controls;
using Demo.Desktop.ViewModels;

namespace Demo.Desktop;

public partial class AgentPanel : UserControl
{
    public AgentPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AgentPanelViewModel old) old.Items.CollectionChanged -= ScrollToEnd;
            if (e.NewValue is AgentPanelViewModel vm) vm.Items.CollectionChanged += ScrollToEnd;
        };
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(Scroller.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
}
